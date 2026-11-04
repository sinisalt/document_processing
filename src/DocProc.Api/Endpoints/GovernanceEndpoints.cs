using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Pipeline.Layouts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary><c>POST /documents/{id}:golden</c>: an optional note.</summary>
public sealed record MarkGoldenRequest(string? Note = null);

/// <summary>A golden document (docs/11 §6.9): the document, its type, supplier and layout version, the values it was approved with and its outputs' hashes.</summary>
public sealed record GoldenDocumentDto(Guid Id, Guid DocumentId, string TypeKey, Guid? SupplierId, Guid? SupplierLayoutVersionId, JsonElement ExpectedValues, JsonElement? ExpectedOutputs, string? Note, string? AddedBy, DateTimeOffset AddedAt);

/// <summary>
/// <c>POST /sandbox/runs</c> (docs/11 §15.3): a stored document run through a draft configuration without side effects:
/// a layout version (<see cref="LayoutVersionId"/>), or a draft rule and field mapping of <see cref="TypeKey"/> (the
/// document's type by default).
/// </summary>
public sealed record SandboxRunRequest(Guid DocumentId, Guid? LayoutVersionId = null, JsonElement? Identification = null, JsonElement? Mapping = null, string? TypeKey = null);

/// <summary><c>POST /regression-runs</c> (docs/11 §15.3): the golden documents of a layout's supplier and type through one of its versions.</summary>
public sealed record RegressionRunRequest(Guid LayoutId, int Version);

/// <summary>A sandbox or regression run as stored: what ran, whether it passed, and its trace or report.</summary>
public sealed record RegressionRunDto(Guid Id, string Kind, string Trigger, Guid? DocumentId, bool Passed, JsonElement CandidateConfig, JsonElement Result, string? CreatedBy, DateTimeOffset CreatedAt);

/// <summary><c>POST /config/bundle:import</c> (docs/11 §15.4): the bundle as exported and <c>dry-run</c> (default) or <c>apply</c>.</summary>
public sealed record BundleImportRequest(JsonElement Bundle, string? Mode = null);

/// <summary>One item of an import report: its kind and key, what the import does or did (<c>create</c>, <c>newVersion</c>, <c>skip</c>, <c>error</c>) and why.</summary>
public sealed record BundleImportItem(string Kind, string Key, string Action, string? Detail);

/// <summary>The import report: whether the signature verified, the mode, and per item what happens.</summary>
public sealed record BundleImportResponse(string Mode, bool SignatureValid, int Version, IReadOnlyList<BundleImportItem> Items);

/// <summary>
/// Governance of P4 part C (docs/11 §6.9, §15.3, §15.4): golden documents, sandbox runs of a document through a draft
/// layout configuration, regression runs of a layout version over the golden documents, and the configuration bundle
/// (types, layouts, source rules, output mappings and bindings as one signed, versioned JSON document) with its import.
/// </summary>
public static class GovernanceEndpoints
{
    public const string BundleFormat = "docproc.config-bundle";
    public const int BundleVersion = 1;
    public const string SigningKeySetting = "ConfigBundles:SigningKey";

    /// <summary>The key bundles are signed with when <see cref="SigningKeySetting"/> is not set (development only).</summary>
    public const string DevelopmentSigningKey = "docproc-development-bundle-key";

    public static readonly IReadOnlyList<string> BundleParts = ["types", "layouts", "sourceRules", "outputs", "bindings"];

    public static RouteGroupBuilder MapGovernance(this RouteGroupBuilder group)
    {
        group.MapPost("/documents/{id:guid}:golden", MarkGoldenAsync).RequireAuthorization(Policies.Review).WithTags("Governance").WithName("MarkGoldenDocument")
            .WithSummary("Keeps an approved document as a golden document (docs/11 §15.3): a snapshot of its values and its outputs' hashes; replays and regression runs read it.");
        group.MapGet("/golden-documents", ListGoldenAsync).RequireAuthorization(Policies.Read).WithTags("Governance").WithName("ListGoldenDocuments")
            .WithSummary("The golden documents, optionally of one supplier (?supplierId=) and type (?typeKey=).");
        group.MapDelete("/golden-documents/{id:guid}", DeleteGoldenAsync).RequireAuthorization(Policies.Review).WithTags("Governance").WithName("DeleteGoldenDocument")
            .WithSummary("Removes a golden document (the document stays).");
        group.MapPost("/sandbox/runs", SandboxAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Governance").WithName("CreateSandboxRun")
            .WithMetadata(new RequestSizeLimitAttribute(256 * 1024))
            .WithSummary("Runs a stored document through a draft layout (a version, or a rule and mapping) without side effects (docs/11 §15.3): the identification evidence, or NEW_LAYOUT with the identifiers that failed, the values the mapping reads and its drift.");
        group.MapGet("/sandbox/runs/{id:guid}", GetRunAsync).RequireAuthorization(Policies.Read).WithTags("Governance").WithName("GetSandboxRun")
            .WithSummary("A sandbox run.");
        group.MapPost("/regression-runs", RegressionAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Governance").WithName("CreateRegressionRun")
            .WithSummary("Runs the golden documents of a layout's supplier and type through one of its versions and compares the values with the snapshots (docs/11 §15.3).");
        group.MapGet("/regression-runs/{id:guid}", GetRunAsync).RequireAuthorization(Policies.Read).WithTags("Governance").WithName("GetRegressionRun")
            .WithSummary("A regression run.");
        group.MapGet("/config/bundle", ExportAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Governance").WithName("ExportConfigBundle")
            .WithSummary("Exports the tenant's configuration as one signed, versioned JSON bundle (docs/11 §15.4): ?include=types,layouts,sourceRules,outputs,bindings; secrets are never in it.");
        group.MapPost("/config/bundle:import", ImportAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Governance").WithName("ImportConfigBundle")
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024))
            .WithSummary("Checks a bundle's signature and contents and reports per item what an import does (dry-run), or does it (apply): types and output mappings as drafts, layouts as Candidates, source rules and bindings disabled.");
        return group;
    }

    private static async Task<Results<Created<GoldenDocumentDto>, ProblemHttpResult>> MarkGoldenAsync(
        Guid id,
        MarkGoldenRequest? request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] IContainerBlobStore blobs,
        [FromServices] IEffectiveConfigResolver configs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, "Golden documents") is { } scope)
        {
            return scope;
        }

        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return SupplierGroupEndpoints.NotFound("Document", id);
        }

        if (document.Status is not (DocumentStatus.Approved or DocumentStatus.AutoApproved) || document.CanonicalJson is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not approved", detail: $"Document {id} is {document.Status}; only an approved document with data is a golden document.");
        }

        if (await db.GoldenDocuments.AnyAsync(g => g.DocumentId == id, ct))
        {
            return Conflict("Golden already", $"Document {id} is a golden document already.");
        }

        var type = await new LayoutLearning(db, blobs, configs, clock).TypeOfAsync(document, null, ct);
        if (type?.Compiled is not { } compiled || JsonNode.Parse(document.CanonicalJson) is not JsonObject tree)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No definition", detail: $"Document {id}'s type has no definition to snapshot its values against.");
        }

        var outputs = await db.OutboxDeliveries.AsNoTracking().Where(d => d.DocumentId == id && d.PayloadSha256 != null).Select(d => new { d.BindingId, d.PayloadSha256, d.CreatedAt }).ToListAsync(ct);
        var hashes = new JsonObject([.. outputs.GroupBy(o => o.BindingId).Select(g => KeyValuePair.Create(g.Key.ToString(), (JsonNode?)JsonValue.Create(g.OrderByDescending(o => o.CreatedAt).First().PayloadSha256)))]);
        var golden = new GoldenDocument
        {
            Id = Guid.CreateVersion7(),
            TenantId = document.TenantId,
            DocumentId = id,
            TypeKey = document.Type.Value,
            SupplierId = document.SupplierId,
            SupplierLayoutVersionId = document.SupplierLayoutVersionId,
            ExpectedValuesJson = LayoutLearning.Snapshot(tree, compiled.Descriptors).ToJsonString(),
            ExpectedOutputsJson = hashes.Count > 0 ? hashes.ToJsonString() : null,
            Note = request?.Note is { Length: > 0 } note ? note[..Math.Min(note.Length, GoldenDocument.MaxNoteLength)] : null,
            AddedBy = DocumentQueries.ActorName(user),
            AddedAt = clock.GetUtcNow(),
        };
        db.GoldenDocuments.Add(golden);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/golden-documents/{golden.Id}", ToDto(golden));
    }

    private static async Task<Results<Ok<IReadOnlyList<GoldenDocumentDto>>, ProblemHttpResult>> ListGoldenAsync(Guid? supplierId, string? typeKey, ITenantContext tenant, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (RequireTenant(tenant, "Golden documents") is { } scope)
        {
            return scope;
        }

        var rows = await db.GoldenDocuments.AsNoTracking()
            .Where(g => (supplierId == null || g.SupplierId == supplierId) && (typeKey == null || g.TypeKey == typeKey))
            .ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<GoldenDocumentDto>>([.. rows.OrderByDescending(g => g.AddedAt).Select(ToDto)]);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteGoldenAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (RequireTenant(tenant, "Golden documents") is { } scope)
        {
            return scope;
        }

        var row = await db.GoldenDocuments.SingleOrDefaultAsync(g => g.Id == id, ct);
        if (row is null)
        {
            return SupplierGroupEndpoints.NotFound("Golden document", id);
        }

        db.GoldenDocuments.Remove(row);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Created<RegressionRunDto>, ValidationProblem, ProblemHttpResult>> SandboxAsync(
        SandboxRunRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        [FromServices] IEffectiveConfigResolver configs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Sandbox runs", ct) is { } problem)
        {
            return problem;
        }

        var document = await db.Documents.AsNoTracking().Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).SingleOrDefaultAsync(d => d.Id == request.DocumentId, ct);
        if (document is null)
        {
            return SupplierGroupEndpoints.NotFound("Document", request.DocumentId);
        }

        var errors = new Errors();
        string ruleJson;
        string mappingJson;
        string typeKey;
        string layoutRef;
        if (request.LayoutVersionId is { } versionId)
        {
            var version = await db.SupplierLayoutVersions.AsNoTracking().Include(v => v.Layout).SingleOrDefaultAsync(v => v.Id == versionId, ct);
            if (version?.Layout is null)
            {
                return SupplierGroupEndpoints.NotFound("Layout version", versionId);
            }

            (ruleJson, mappingJson, typeKey, layoutRef) = (version.IdentificationJson, version.MappingJson, version.Layout.DocumentTypeKey, $"{version.Layout.LayoutKey}@{version.Version}");
        }
        else
        {
            if (request.Identification is not { ValueKind: JsonValueKind.Object } identification || request.Mapping is not { ValueKind: JsonValueKind.Object } mapping)
            {
                errors.Add("identification", "Name a layoutVersionId, or give a draft identification rule and mapping.");
                return errors.ToProblem()!;
            }

            (ruleJson, mappingJson, typeKey, layoutRef) = (identification.GetRawText(), mapping.GetRawText(), request.TypeKey ?? document.Type.Value, "sandbox@0");
        }

        var rule = IdentificationRuleChecks.Check(ruleJson, IdentificationScope.Layout, out var problems);
        foreach (var group in problems.GroupBy(p => p.Path))
        {
            errors.Add($"identification.{group.Key}", string.Join(" ", group.Select(p => p.Message).Distinct()));
        }

        var parsed = LayoutMapping.TryParse(mappingJson, out var mappingError);
        if (parsed is null)
        {
            errors.Add("mapping", mappingError!);
        }

        if (!DocumentTypeKey.TryParse(typeKey, out var key) || key.IsUnknown)
        {
            errors.Add("typeKey", $"'{typeKey}' is not a type key.");
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var learning = new LayoutLearning(db, blobs, configs, clock);
        var (evaluated, tree, drift) = await learning.DryRunAsync(document, typeKey, rule!, parsed!, layoutRef, ct);
        var type = await learning.TypeOfAsync(document, typeKey, ct);
        var values = tree is not null && type?.Compiled is { } compiled ? LayoutLearning.Snapshot(tree, compiled.Descriptors) : null;
        var result = new JsonObject
        {
            ["resolution"] = evaluated.Holds ? "identified" : "newLayout",
            ["evidence"] = JsonSerializer.SerializeToNode(evaluated.Evidence.Where(e => e.Positive).Select(IdentificationEvidenceDto.From).ToList()),
            ["failed"] = new JsonArray([.. evaluated.Failed.Select(f => (JsonNode?)JsonValue.Create(f))]),
            ["values"] = values,
            ["drift"] = drift is null ? null : JsonSerializer.SerializeToNode(drift),
            ["supplierId"] = document.SupplierId?.ToString(),
            ["supplierMatchMethod"] = document.SupplierMatchMethod?.ToString(),
            ["typeKey"] = typeKey,
            ["sideEffects"] = "none: nothing was written to the document, its layouts or its deliveries",
        };
        var run = new RegressionRun
        {
            Id = Guid.CreateVersion7(),
            TenantId = document.TenantId,
            Kind = RegressionRunKind.Sandbox,
            Trigger = "sandbox",
            DocumentId = document.Id,
            CandidateConfigJson = new JsonObject { ["layoutVersionId"] = request.LayoutVersionId?.ToString(), ["identification"] = JsonNode.Parse(ruleJson), ["mapping"] = JsonNode.Parse(mappingJson), ["typeKey"] = typeKey }.ToJsonString(),
            ResultJson = result.ToJsonString(),
            Passed = evaluated.Holds && drift is { IsDrift: false },
            CreatedBy = DocumentQueries.ActorName(user),
            CreatedAt = clock.GetUtcNow(),
        };
        db.RegressionRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/sandbox/runs/{run.Id}", ToDto(run));
    }

    private static async Task<Results<Created<RegressionRunDto>, ProblemHttpResult>> RegressionAsync(
        RegressionRunRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        [FromServices] IEffectiveConfigResolver configs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Regression runs", ct) is { } problem)
        {
            return problem;
        }

        var layout = await db.SupplierLayouts.AsNoTracking().Include(l => l.Versions).SingleOrDefaultAsync(l => l.Id == request.LayoutId, ct);
        var version = layout?.Versions.FirstOrDefault(v => v.Version == request.Version);
        if (layout is null || version is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Layout {request.LayoutId} has no version {request.Version}.");
        }

        var suppliers = layout.Scope == SupplierLayoutScope.Supplier
            ? [layout.OwnerId]
            : await db.Suppliers.AsNoTracking().Where(s => s.GroupId == layout.OwnerId).Select(s => s.Id).ToListAsync(ct);
        var goldens = await db.GoldenDocuments.AsNoTracking().Include(g => g.Document).ThenInclude(d => d!.IngestionItem).ThenInclude(i => i!.Ingestion)
            .Where(g => g.SupplierId != null && suppliers.Contains(g.SupplierId.Value) && g.TypeKey == layout.DocumentTypeKey)
            .ToListAsync(ct);
        var learning = new LayoutLearning(db, blobs, configs, clock);
        var rule = IdentificationRule.Parse(version.IdentificationJson);
        var mapping = LayoutMapping.Parse(version.MappingJson);
        var documents = new JsonArray();
        var passed = true;
        foreach (var golden in goldens.Where(g => g.Document is not null))
        {
            var (evaluated, tree, _) = await learning.DryRunAsync(golden.Document!, layout.DocumentTypeKey, rule, mapping, $"{layout.LayoutKey}@{version.Version}", ct);
            var type = await learning.TypeOfAsync(golden.Document!, layout.DocumentTypeKey, ct);
            var expected = JsonNode.Parse(golden.ExpectedValuesJson) as JsonObject ?? [];
            var actual = tree is not null && type?.Compiled is { } compiled ? LayoutLearning.Snapshot(tree, compiled.Descriptors) : [];
            var covered = mapping.Coverage.ToHashSet(StringComparer.Ordinal);
            var differences = new JsonArray();
            foreach (var (path, value) in expected)
            {
                var root = path.Split('[', '.')[0];
                if (!covered.Contains(path) && !covered.Contains(root))
                {
                    continue;
                }

                var got = actual[path]?.ToJsonString();
                if (!string.Equals(got, value?.ToJsonString(), StringComparison.Ordinal))
                {
                    differences.Add(new JsonObject { ["path"] = path, ["expected"] = value?.DeepClone(), ["actual"] = actual[path]?.DeepClone() });
                }
            }

            var ok = evaluated.Holds && differences.Count == 0;
            passed &= ok;
            documents.Add(new JsonObject { ["documentId"] = golden.DocumentId.ToString(), ["holds"] = evaluated.Holds, ["passed"] = ok, ["differences"] = differences, ["failed"] = new JsonArray([.. evaluated.Failed.Select(f => (JsonNode?)JsonValue.Create(f))]) });
        }

        var run = new RegressionRun
        {
            Id = Guid.CreateVersion7(),
            TenantId = layout.TenantId,
            Kind = RegressionRunKind.Regression,
            Trigger = "layoutVersion",
            CandidateConfigJson = new JsonObject { ["layoutId"] = layout.Id.ToString(), ["layoutKey"] = layout.LayoutKey, ["version"] = version.Version, ["versionId"] = version.Id.ToString() }.ToJsonString(),
            ResultJson = new JsonObject { ["goldenDocuments"] = goldens.Count, ["documents"] = documents }.ToJsonString(),
            Passed = passed && goldens.Count > 0,
            CreatedBy = DocumentQueries.ActorName(user),
            CreatedAt = clock.GetUtcNow(),
        };
        db.RegressionRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/regression-runs/{run.Id}", ToDto(run));
    }

    private static async Task<Results<Ok<RegressionRunDto>, ProblemHttpResult>> GetRunAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (RequireTenant(tenant, "Runs") is { } scope)
        {
            return scope;
        }

        var run = await db.RegressionRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        return run is null ? SupplierGroupEndpoints.NotFound("Run", id) : TypedResults.Ok(ToDto(run));
    }

    /// <summary>The bundle's signature: HMAC-SHA256 over the bundle's JSON without its <c>signature</c>, hex.</summary>
    public static string Sign(JsonObject bundle, string key)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var copy = (JsonObject)bundle.DeepClone();
        copy.Remove("signature");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(copy.ToJsonString()));
        return Convert.ToHexStringLower(hash);
    }

    private static string KeyOf(IConfiguration configuration) =>
        configuration[SigningKeySetting] is { Length: > 0 } configured ? configured : DevelopmentSigningKey;

    private static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> ExportAsync(
        string? include,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        IConfiguration configuration,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, "Configuration bundles") is { } scope)
        {
            return scope;
        }

        var parts = (include ?? string.Join(',', BundleParts)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (parts.FirstOrDefault(p => !BundleParts.Contains(p, StringComparer.Ordinal)) is { } unknown)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["include"] = [$"'{unknown}' is not a part; include lists {string.Join(", ", BundleParts)}."] });
        }

        var tenantId = tenant.TenantId!.Value;
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
        JsonObject SupplierRef(Guid? id) => id is { } sid && suppliers.TryGetValue(sid, out var s) ? new JsonObject { ["name"] = s.Name, ["vatId"] = s.VatId } : new JsonObject();
        var bundle = new JsonObject
        {
            ["format"] = BundleFormat,
            ["version"] = BundleVersion,
            ["exportedAt"] = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["sourceTenantId"] = tenantId.ToString(),
            ["include"] = new JsonArray([.. parts.Select(p => (JsonNode?)JsonValue.Create(p))]),
        };
        if (parts.Contains("types"))
        {
            var definitions = await db.DocumentTypeDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId).ToListAsync(ct);
            var versions = await db.DocumentTypeVersions.AsNoTracking().Where(v => v.TenantId == tenantId && v.State == DocumentTypeVersionState.Published).ToListAsync(ct);
            bundle["types"] = new JsonArray([.. definitions.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => versions.Where(v => v.DefinitionId == d.Id).OrderByDescending(v => v.Version).FirstOrDefault() is { } v
                ? (JsonNode?)new JsonObject { ["key"] = d.Key, ["version"] = v.Version, ["definition"] = JsonNode.Parse(v.DefinitionJson) }
                : null).Where(n => n is not null)]);
        }

        if (parts.Contains("layouts"))
        {
            var layouts = await db.SupplierLayouts.AsNoTracking().Include(l => l.Versions).Where(l => l.Status == SupplierLayoutStatus.Active && l.Scope == SupplierLayoutScope.Supplier).ToListAsync(ct);
            bundle["layouts"] = new JsonArray([.. layouts.OrderBy(l => l.LayoutKey, StringComparer.Ordinal).Select(l => (l.Versions.FirstOrDefault(v => v.Version == l.ActiveVersion) ?? l.Versions.OrderByDescending(v => v.Version).First()) is var v
                ? (JsonNode?)new JsonObject
                {
                    ["supplier"] = SupplierRef(l.SupplierId),
                    ["layoutKey"] = l.LayoutKey,
                    ["name"] = l.Name,
                    ["documentTypeKey"] = l.DocumentTypeKey,
                    ["priority"] = l.Priority,
                    ["itemKinds"] = new JsonArray([.. l.ItemKinds.Select(k => (JsonNode?)JsonValue.Create(k))]),
                    ["version"] = v.Version,
                    ["status"] = v.Status.ToString(),
                    ["identification"] = JsonNode.Parse(v.IdentificationJson),
                    ["mapping"] = JsonNode.Parse(v.MappingJson),
                }
                : null)]);
        }

        if (parts.Contains("sourceRules"))
        {
            var rules = await db.SupplierSourceRules.AsNoTracking().ToListAsync(ct);
            bundle["sourceRules"] = new JsonArray([.. rules.OrderBy(r => r.ConditionHash, StringComparer.Ordinal).Select(r => (JsonNode?)new JsonObject
            {
                ["supplier"] = SupplierRef(r.SupplierId),
                ["recipient"] = r.Recipient,
                ["sender"] = r.Sender,
                ["senderDomain"] = r.SenderDomain,
                ["folderPrefix"] = r.FolderPrefix,
                ["subjectRegex"] = r.SubjectRegex,
                ["fileNameRegex"] = r.FileNameRegex,
                ["mode"] = r.Mode.ToString(),
                ["requireSenderAuth"] = r.RequireSenderAuth,
                ["priority"] = r.Priority,
                ["pinTypeKey"] = r.PinTypeKey,
                ["enabled"] = r.Enabled,
            })]);
        }

        if (parts.Contains("outputs"))
        {
            var mappings = await db.OutputMappings.AsNoTracking().Include(m => m.Versions).Where(m => m.TenantId == tenantId).ToListAsync(ct);
            bundle["outputs"] = new JsonArray([.. mappings.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => (m.Versions.FirstOrDefault(v => v.Version == m.PublishedVersion) ?? m.Versions.OrderByDescending(v => v.Version).FirstOrDefault()) is { } v
                ? (JsonNode?)new JsonObject
                {
                    ["key"] = m.Key,
                    ["name"] = m.Name,
                    ["sourceTypeKey"] = m.SourceTypeKey,
                    ["version"] = v.Version,
                    ["engine"] = v.Engine,
                    ["expression"] = v.Expression,
                    ["contentType"] = v.ContentType,
                    ["fileNameTemplate"] = v.FileNameTemplate,
                    ["targetSchema"] = v.TargetSchemaJson is null ? null : JsonNode.Parse(v.TargetSchemaJson),
                }
                : null).Where(n => n is not null)]);
        }

        if (parts.Contains("bindings"))
        {
            var bindings = await db.OutputBindings.AsNoTracking().Include(b => b.Mapping).Include(b => b.Destination).ToListAsync(ct);
            bundle["bindings"] = new JsonArray([.. bindings.OrderBy(b => b.Name, StringComparer.Ordinal).Select(b => (JsonNode?)new JsonObject
            {
                ["name"] = b.Name,
                ["events"] = new JsonArray([.. b.Events.Select(e => (JsonNode?)JsonValue.Create(e))]),
                ["documentTypeKey"] = b.DocumentTypeKey,
                ["supplier"] = b.SupplierId is null ? null : SupplierRef(b.SupplierId),
                ["condition"] = b.Condition,
                ["mappingKey"] = b.Mapping?.Key,
                ["pinnedMappingVersion"] = b.PinnedMappingVersion,
                ["destination"] = b.Destination is null ? null : new JsonObject
                {
                    ["name"] = b.Destination.Name,
                    ["transport"] = b.Destination.Transport,
                    ["config"] = JsonNode.Parse(b.Destination.ConfigJson),
                    ["secret"] = b.Destination.SecretRef is null ? null : "<secret: set it on the target with PUT /destinations/{id}/secret>",
                },
                ["onReapproval"] = b.OnReapproval.ToString(),
                ["priority"] = b.Priority,
                ["exclusiveGroup"] = b.ExclusiveGroup,
            })]);
        }

        bundle["signature"] = Sign(bundle, KeyOf(configuration));
        return TypedResults.Ok(bundle);
    }

    private static async Task<Results<Ok<BundleImportResponse>, ValidationProblem, ProblemHttpResult>> ImportAsync(
        BundleImportRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        IConfiguration configuration,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, "Configuration bundles") is { } scope)
        {
            return scope;
        }

        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "dry-run" : request.Mode.Trim();
        if (mode is not ("dry-run" or "apply"))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["mode"] = ["mode is dry-run or apply."] });
        }

        if (request.Bundle.ValueKind != JsonValueKind.Object || JsonNode.Parse(request.Bundle.GetRawText()) is not JsonObject bundle
            || bundle["format"]?.GetValue<string>() != BundleFormat || bundle["version"] is not JsonValue v || !v.TryGetValue<int>(out var version))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["bundle"] = [$"bundle is a {BundleFormat} document with a version."] });
        }

        if (version != BundleVersion)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["bundle.version"] = [$"Version {version} is not read by this build; it reads version {BundleVersion}."] });
        }

        var signature = bundle["signature"] is JsonValue s && s.TryGetValue<string>(out var given) ? given : null;
        var signatureValid = signature is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Sign(bundle, KeyOf(configuration))));
        if (!signatureValid)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Signature invalid", detail: "The bundle's signature does not verify with this environment's ConfigBundles:SigningKey: it was changed after export, or exported where another key is set.", extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "BUNDLE_SIGNATURE_INVALID" });
        }

        var apply = mode == "apply";
        var tenantId = tenant.TenantId!.Value;
        var actor = DocumentQueries.ActorName(user);
        var now = clock.GetUtcNow();
        var items = new List<BundleImportItem>();
        var suppliers = await db.Suppliers.ToListAsync(ct);
        Supplier? Match(JsonNode? reference)
        {
            var vat = reference?["vatId"]?.GetValue<string>();
            var name = reference?["name"]?.GetValue<string>();
            return (vat is { Length: > 0 } ? suppliers.FirstOrDefault(x => string.Equals(x.VatId, vat, StringComparison.OrdinalIgnoreCase)) : null)
                   ?? (name is { Length: > 0 } ? suppliers.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) : null);
        }

        // Types: a new key becomes a tenant type (through the catalogue's checks), an existing one gets a draft version.
        var catalogue = new DocumentTypeCatalogue(db, clock);
        foreach (var type in (bundle["types"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = type["key"]?.GetValue<string>() ?? "?";
            var definition = type["definition"]?.ToJsonString();
            if (definition is null)
            {
                items.Add(new BundleImportItem("type", key, "error", "No definition."));
                continue;
            }

            var exists = await catalogue.FindAsync(tenantId, key, ct) is not null;
            if (!apply)
            {
                items.Add(new BundleImportItem("type", key, exists ? "newVersion" : "create", exists ? "A draft version with the bundle's definition." : "A new tenant type."));
                continue;
            }

            if (!exists)
            {
                var created = await catalogue.CreateAsync(tenantId, definition, actor, ct);
                items.Add(new BundleImportItem("type", key, created.Status == CatalogueStatus.Created ? "create" : "error", created.Detail));
            }
            else
            {
                var draft = await catalogue.CreateDraftAsync(tenantId, key, actor, ct);
                var saved = draft.Value is { } d ? await catalogue.SaveDraftAsync(tenantId, key, d.Version, definition, d.Revision, actor, ct) : null;
                items.Add(new BundleImportItem("type", key, saved?.Value is not null ? "newVersion" : "error", saved?.Detail ?? draft.Detail));
            }
        }

        // Layouts: matched to a supplier by VAT id, then name; a new layout or a new version, always a Candidate (Imported).
        foreach (var layout in (bundle["layouts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = layout["layoutKey"]?.GetValue<string>() ?? "?";
            var supplier = Match(layout["supplier"]);
            var typeKey = layout["documentTypeKey"]?.GetValue<string>() ?? "Invoice";
            var ruleJson = layout["identification"]?.ToJsonString();
            var mappingJson = layout["mapping"]?.ToJsonString();
            if (supplier is null)
            {
                items.Add(new BundleImportItem("layout", key, "error", "No supplier of this tenant has the layout's supplier VAT id or name."));
                continue;
            }

            var rule = ruleJson is null ? null : IdentificationRuleChecks.Check(ruleJson, IdentificationScope.Layout, out var ruleProblems) is { } checkedRule && ruleProblems.Count == 0 ? checkedRule : null;
            var mapping = LayoutMapping.TryParse(mappingJson, out var mappingError);
            if (rule is null || mapping is null || mappingJson is null)
            {
                items.Add(new BundleImportItem("layout", key, "error", rule is null ? "The identification rule does not pass the checks." : $"The mapping does not read: {mappingError}"));
                continue;
            }

            var existing = await db.SupplierLayouts.Include(l => l.Versions).SingleOrDefaultAsync(l => l.Scope == SupplierLayoutScope.Supplier && l.OwnerId == supplier.Id && l.DocumentTypeKey == typeKey && l.LayoutKey == key, ct);
            if (existing?.Versions.Any(x => x.IdentificationHash == rule.Hash && x.MappingJson == mappingJson) == true)
            {
                items.Add(new BundleImportItem("layout", key, "skip", "The layout has this rule and mapping already."));
                continue;
            }

            items.Add(new BundleImportItem("layout", key, existing is null ? "create" : "newVersion", $"{supplier.Name}: a Candidate a MappingAuthor activates after its replay."));
            if (!apply)
            {
                continue;
            }

            var target = existing ?? new SupplierLayout
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Scope = SupplierLayoutScope.Supplier,
                SupplierId = supplier.Id,
                OwnerId = supplier.Id,
                DocumentTypeKey = typeKey,
                LayoutKey = key,
                Name = layout["name"]?.GetValue<string>() ?? key,
                Priority = layout["priority"] is JsonValue p && p.TryGetValue<int>(out var priority) ? priority : SupplierLayout.DefaultPriority,
                ItemKinds = [.. (layout["itemKinds"] as JsonArray ?? []).Select(k => k?.GetValue<string>()).OfType<string>()],
                CreatedBy = actor,
                CreatedAt = now,
                UpdatedAt = now,
            };
            if (existing is null)
            {
                db.SupplierLayouts.Add(target);
            }

            var added = new SupplierLayoutVersion
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                LayoutId = target.Id,
                Version = target.Versions.Count == 0 ? 1 : target.Versions.Max(x => x.Version) + 1,
                Status = SupplierLayoutVersionStatus.Candidate,
                Origin = SupplierLayoutOrigin.Imported,
                IdentificationJson = rule.ToJson(),
                IdentificationHash = rule.Hash,
                MappingJson = mappingJson,
                CoverageJson = JsonSerializer.Serialize(mapping.Coverage),
                CreatedBy = actor,
                CreatedAt = now,
            };
            target.Versions.Add(added);
            if (existing is not null)
            {
                db.SupplierLayoutVersions.Add(added);
            }
        }

        // Source rules: the conditions checked as POST /suppliers/{id}/source-rules checks them, saved disabled.
        foreach (var rule in (bundle["sourceRules"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var supplier = Match(rule["supplier"]);
            var describe = rule["sender"]?.GetValue<string>() ?? rule["senderDomain"]?.GetValue<string>() ?? rule["folderPrefix"]?.GetValue<string>() ?? rule["recipient"]?.GetValue<string>() ?? "rule";
            if (supplier is null)
            {
                items.Add(new BundleImportItem("sourceRule", describe, "error", "No supplier of this tenant has the rule's supplier VAT id or name."));
                continue;
            }

            var entity = new SupplierSourceRule { Id = Guid.CreateVersion7(), TenantId = tenantId, SupplierId = supplier.Id, ConditionHash = string.Empty, CreatedBy = actor, CreatedAt = now, UpdatedAt = now };
            var refusal = await SourceRuleEndpoints.ApplyAsync(db, entity, new SourceRuleRequest(
                Recipient: rule["recipient"]?.GetValue<string>(),
                Sender: rule["sender"]?.GetValue<string>(),
                SenderDomain: rule["senderDomain"]?.GetValue<string>(),
                FolderPrefix: rule["folderPrefix"]?.GetValue<string>(),
                SubjectRegex: rule["subjectRegex"]?.GetValue<string>(),
                FileNameRegex: rule["fileNameRegex"]?.GetValue<string>(),
                Mode: rule["mode"]?.GetValue<string>(),
                RequireSenderAuth: rule["requireSenderAuth"] is JsonValue auth && auth.TryGetValue<bool>(out var a) ? a : null,
                Priority: rule["priority"] is JsonValue pr && pr.TryGetValue<int>(out var rp) ? rp : null,
                PinTypeKey: rule["pinTypeKey"]?.GetValue<string>(),
                Enabled: false), supplier, ct);
            if (refusal is not null)
            {
                items.Add(new BundleImportItem("sourceRule", describe, refusal.Problem is not null ? "skip" : "error", refusal.Problem?.ProblemDetails.Detail ?? "The rule's conditions do not pass the checks."));
                continue;
            }

            items.Add(new BundleImportItem("sourceRule", describe, "create", $"{supplier.Name}: saved disabled; a ConfigAdmin enables it."));
            if (apply)
            {
                db.SupplierSourceRules.Add(entity);
            }
        }

        // Output mappings: a new key with a draft version; bindings disabled, to destinations of the same name.
        var mappingIds = await db.OutputMappings.Where(m => m.TenantId == tenantId).ToDictionaryAsync(m => m.Key, m => m.Id, StringComparer.Ordinal, ct);
        foreach (var output in (bundle["outputs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = output["key"]?.GetValue<string>() ?? "?";
            if (mappingIds.ContainsKey(key))
            {
                items.Add(new BundleImportItem("output", key, "skip", "The tenant has an output mapping with this key; edit it there."));
                continue;
            }

            items.Add(new BundleImportItem("output", key, "create", "A mapping with a draft version, published in the Integrations UI."));
            if (!apply)
            {
                continue;
            }

            var mapping = new OutputMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, Key = key, Name = output["name"]?.GetValue<string>() ?? key, SourceTypeKey = output["sourceTypeKey"]?.GetValue<string>(), Origin = OutputMappingOrigin.Custom, CreatedAt = now, CreatedBy = actor, UpdatedAt = now };
            mapping.Versions.Add(new OutputMappingVersion
            {
                Id = Guid.CreateVersion7(),
                MappingId = mapping.Id,
                TenantId = tenantId,
                Version = 1,
                Engine = output["engine"]?.GetValue<string>() ?? OutputEngines.Jsonata,
                Expression = output["expression"]?.GetValue<string>() ?? string.Empty,
                ContentType = output["contentType"]?.GetValue<string>() ?? "application/json",
                FileNameTemplate = output["fileNameTemplate"]?.GetValue<string>(),
                TargetSchemaJson = output["targetSchema"]?.ToJsonString(),
                ChangeNote = "Imported from a configuration bundle.",
                CreatedAt = now,
                CreatedBy = actor,
                UpdatedAt = now,
            });
            db.OutputMappings.Add(mapping);
            mappingIds[key] = mapping.Id;
        }

        var destinations = await db.OutputDestinations.AsNoTracking().ToDictionaryAsync(d => d.Name, d => d.Id, StringComparer.Ordinal, ct);
        var bindingNames = await db.OutputBindings.AsNoTracking().Select(b => b.Name).ToListAsync(ct);
        foreach (var binding in (bundle["bindings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = binding["name"]?.GetValue<string>() ?? "?";
            var destinationName = binding["destination"]?["name"]?.GetValue<string>();
            var mappingKey = binding["mappingKey"]?.GetValue<string>();
            if (bindingNames.Contains(name, StringComparer.Ordinal))
            {
                items.Add(new BundleImportItem("binding", name, "skip", "The tenant has a binding with this name."));
                continue;
            }

            if (destinationName is null || !destinations.TryGetValue(destinationName, out var destinationId))
            {
                items.Add(new BundleImportItem("binding", name, "error", $"No destination named '{destinationName}': create it (with its secret) first; secrets are never in a bundle."));
                continue;
            }

            if (mappingKey is null || !mappingIds.TryGetValue(mappingKey, out var mappingId))
            {
                items.Add(new BundleImportItem("binding", name, "error", $"No output mapping '{mappingKey}' in the tenant or the bundle."));
                continue;
            }

            items.Add(new BundleImportItem("binding", name, "create", "Saved disabled; enable it once its mapping is published."));
            if (apply)
            {
                db.OutputBindings.Add(new OutputBinding
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Name = name,
                    Events = [.. (binding["events"] as JsonArray ?? []).Select(e => e?.GetValue<string>()).OfType<string>()],
                    DocumentTypeKey = binding["documentTypeKey"]?.GetValue<string>(),
                    SupplierId = binding["supplier"] is JsonObject sref ? Match(sref)?.Id : null,
                    Condition = binding["condition"]?.GetValue<string>(),
                    MappingId = mappingId,
                    DestinationId = destinationId,
                    Priority = binding["priority"] is JsonValue bp && bp.TryGetValue<int>(out var bpv) ? bpv : 0,
                    ExclusiveGroup = binding["exclusiveGroup"]?.GetValue<string>(),
                    OnReapproval = Enum.TryParse<OnReapproval>(binding["onReapproval"]?.GetValue<string>(), out var re) ? re : OnReapproval.Resend,
                    Enabled = false,
                    CreatedAt = now,
                    CreatedBy = actor,
                    UpdatedAt = now,
                });
            }
        }

        if (apply)
        {
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.Ok(new BundleImportResponse(mode, signatureValid, version, items));
    }

    private static GoldenDocumentDto ToDto(GoldenDocument g) =>
        new(g.Id, g.DocumentId, g.TypeKey, g.SupplierId, g.SupplierLayoutVersionId, LayoutEndpoints.Element(g.ExpectedValuesJson) ?? default, LayoutEndpoints.Element(g.ExpectedOutputsJson), g.Note, g.AddedBy, g.AddedAt);

    private static RegressionRunDto ToDto(RegressionRun r) =>
        new(r.Id, r.Kind.ToString(), r.Trigger, r.DocumentId, r.Passed, LayoutEndpoints.Element(r.CandidateConfigJson) ?? default, LayoutEndpoints.Element(r.ResultJson) ?? default, r.CreatedBy, r.CreatedAt);
}
