using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Pipeline.Layouts;
using DocProc.Pipeline.Review;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>A version of a layout in a list: its lifecycle, origin, rule hash and sentence, warm-up and counters (docs/11 §6.6).</summary>
public sealed record SupplierLayoutVersionSummary(
    Guid Id,
    int Version,
    string Status,
    string Origin,
    string IdentificationHash,
    string Describe,
    int ReviewWarmup,
    int WarmupSeen,
    int Identified,
    int Applied,
    int Agreed,
    int Corrected,
    int ConsecutiveFailures,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt);

/// <summary>A supplier or group layout (docs/11 §6.6 <c>SupplierLayout</c>) with its versions, newest first.</summary>
public sealed record SupplierLayoutResponse(
    Guid Id,
    string Scope,
    Guid? SupplierId,
    Guid? GroupId,
    string DocumentTypeKey,
    string LayoutKey,
    string Name,
    Guid? ReceivingEntityId,
    IReadOnlyList<string> ItemKinds,
    int Priority,
    int? ActiveVersion,
    int? ShadowVersion,
    string Status,
    Guid? CreatedFromDocumentId,
    DateTimeOffset? LastMatchedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SupplierLayoutVersionSummary> Versions);

/// <summary>One layout version in full: its identification rule, field mapping, coverage, replay report and similarity profile as stored.</summary>
public sealed record SupplierLayoutVersionResponse(
    Guid LayoutId,
    string LayoutKey,
    SupplierLayoutVersionSummary Summary,
    JsonElement Identification,
    JsonElement Mapping,
    IReadOnlyList<string> Coverage,
    JsonElement? Replay,
    JsonElement? SimilarityProfile);

/// <summary>Why an identifier held (docs/11 §8.5 evidence): which one, what it is, the page, the box and the words matched.</summary>
public sealed record IdentificationEvidenceDto(string Path, string Kind, string Description, bool Positive, int? Page, IReadOnlyList<double>? BBox, string? Text)
{
    internal static IdentificationEvidenceDto From(IdentifierEvidence e) => new(
        e.Path,
        e.Kind,
        e.Description,
        e.Positive,
        e.Page,
        e.Box is { } b ? [Math.Round(b.X0, 2), Math.Round(b.Y0, 2), Math.Round(b.X1, 2), Math.Round(b.Y1, 2)] : null,
        e.Text);
}

/// <summary>Where a rule under test is run (docs/11 §18): a supplier's documents, a group's members' documents, or the tenant's.</summary>
public sealed record IdentificationTestScope(Guid? SupplierId = null, Guid? GroupId = null, bool? Tenant = null);

/// <summary>
/// The body of <c>POST /identification-rules:test</c> (docs/11 §12.9 step 4, §18): the rule, the scope, and either the
/// documents to test (at most 100) or the last <c>lastN</c> of the scope (50 by default, at most 200), optionally of one type.
/// </summary>
public sealed record IdentificationTestRequest(JsonElement Rule, IdentificationTestScope? Scope = null, IReadOnlyList<Guid>? DocumentIds = null, int? LastN = null, string? TypeKey = null);

/// <summary>One document of a rule test: whether the rule holds on it, with evidence, and the layout that identified it so far.</summary>
public sealed record IdentificationTestDocument(
    Guid DocumentId,
    string? FileName,
    string TypeKey,
    string Status,
    Guid? SupplierId,
    Guid? LayoutId,
    string? LayoutKey,
    bool Matched,
    IReadOnlyList<IdentificationEvidenceDto> Evidence,
    IReadOnlyList<string> Failed);

/// <summary>Documents another layout identified on which the rule under test holds too: an overlap that blocks activation (docs/11 §8.3).</summary>
public sealed record IdentificationOverlap(Guid LayoutId, string LayoutKey, int Documents, IReadOnlyList<Guid> DocumentIds);

/// <summary>The answer of <c>POST /identification-rules:test</c>.</summary>
public sealed record IdentificationTestResponse(
    string Describe,
    string Hash,
    int PositiveIdentifiers,
    int Tested,
    int Matched,
    IReadOnlyList<IdentificationTestDocument> Documents,
    IReadOnlyList<IdentificationOverlap> Overlaps);

/// <summary>One layout version as the resolver evaluated it for a document (<c>layouts:resolve</c>).</summary>
public sealed record LayoutEvaluationDto(Guid LayoutId, string LayoutKey, Guid VersionId, int Version, string DocumentTypeKey, string State, string Scope, int Priority, int PositiveIdentifiers, bool Holds, IReadOnlyList<IdentificationEvidenceDto> Evidence, IReadOnlyList<string> Failed);

/// <summary>A layout a reviewer may look at for a document no layout identified (docs/11 §8.4 step 5): shown, never applied.</summary>
public sealed record LayoutSuggestionDto(Guid LayoutId, string LayoutKey, Guid VersionId, string DocumentTypeKey, double Similarity, IReadOnlyList<string> Differences);

/// <summary>
/// The answer of <c>GET /suppliers/{id}/layouts:resolve?documentId=</c> (docs/11 §18): what <c>ILayoutResolver</c>
/// decides for the document now (<c>Identified</c>, <c>Ambiguous</c>, <c>NewLayout</c>, <c>NotManaged</c>,
/// <c>Exempt</c>), the layout it selects, the tied ones, a pending Candidate, every candidate's result and the
/// suggestions. It changes nothing.
/// </summary>
public sealed record LayoutResolveResponse(
    Guid DocumentId,
    Guid SupplierId,
    string Resolution,
    bool LayoutManaged,
    LayoutEvaluationDto? Selected,
    IReadOnlyList<LayoutEvaluationDto> Tied,
    LayoutEvaluationDto? Pending,
    IReadOnlyList<LayoutEvaluationDto> Evaluated,
    IReadOnlyList<LayoutSuggestionDto> Suggestions);

/// <summary>
/// Supplier layouts and identification rules (docs/11 §6.6, §8, §18), behind the <c>supplierLayouts</c> switch: the
/// supplier's and a group's layouts with their versions, one version in full, a dry run of the resolver on a document,
/// the layout suggestions for a document, and the identification rule test. All reads; layouts are learned and
/// activated from review (part C of P4).
/// </summary>
public static class LayoutEndpoints
{
    public const int DefaultLastN = 50;
    public const int MaxLastN = 200;
    public const int MaxDocumentIds = 100;

    public static RouteGroupBuilder MapLayouts(this RouteGroupBuilder group)
    {
        group.MapGet("/suppliers/{supplierId:guid}/layouts", ListSupplierLayoutsAsync).RequireAuthorization(Policies.Read).WithTags("Layouts").WithName("ListSupplierLayouts")
            .WithSummary("The supplier's layouts (docs/11 §6.6), optionally of one type (?type=), each with its versions.");
        group.MapGet("/supplier-groups/{groupId:guid}/layouts", ListGroupLayoutsAsync).RequireAuthorization(Policies.Read).WithTags("Layouts").WithName("ListGroupLayouts")
            .WithSummary("The layouts of a supplier group, used by members without layouts of their own.");
        group.MapGet("/supplier-layouts/{layoutId:guid}", GetLayoutAsync).RequireAuthorization(Policies.Read).WithTags("Layouts").WithName("GetSupplierLayout")
            .WithSummary("One layout with its versions.");
        group.MapGet("/supplier-layouts/{layoutId:guid}/versions/{version:int}", GetVersionAsync).RequireAuthorization(Policies.Read).WithTags("Layouts").WithName("GetSupplierLayoutVersion")
            .WithSummary("One layout version in full: identification rule, field mapping, coverage, replay report and similarity profile.");
        group.MapGet("/suppliers/{supplierId:guid}/layouts:resolve", ResolveAsync).RequireAuthorization(Policies.Read).WithTags("Layouts").WithName("ResolveSupplierLayout")
            .WithSummary("Dry-runs the layout resolver for a document (?documentId=) against the supplier's layouts and explains every candidate; changes nothing.");
        group.MapGet("/documents/{id:guid}/layout-suggestions", SuggestionsAsync).RequireAuthorization(Policies.Read).WithTags("Layouts").WithName("GetLayoutSuggestions")
            .WithSummary("The supplier's layouts ranked by similarity to the document (docs/11 §8.4 step 5); informational, never applied.");
        group.MapPost("/identification-rules:test", TestAsync).RequireAuthorization(Policies.Read).WithTags("Layouts").WithName("TestIdentificationRule")
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithSummary("Checks an identification rule and evaluates it on documents of a supplier, a group or the tenant: per document whether it holds, with evidence, and the overlaps with other layouts (docs/11 §8.3, §12.9).");
        return group;
    }

    public static ProblemHttpResult FeatureOff() => TypedResults.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Feature not enabled",
        detail: "The 'supplierLayouts' feature is off for this tenant; a tenant admin switches it on in the settings (features.supplierLayouts). Layouts, identification rules and the layout warm-up need it.");

    private static async Task<ProblemHttpResult?> GuardAsync(ITenantContext tenant, ITenantFeatures features, string what, CancellationToken ct)
    {
        if (RequireTenant(tenant, what) is { } problem)
        {
            return problem;
        }

        return await features.IsEnabledAsync(tenant.TenantId!.Value, TenantFeature.SupplierLayouts, ct) ? null : FeatureOff();
    }

    private static async Task<Results<Ok<IReadOnlyList<SupplierLayoutResponse>>, ProblemHttpResult>> ListSupplierLayoutsAsync(Guid supplierId, string? type, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, "Layouts", ct) is { } problem)
        {
            return problem;
        }

        if (!await db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == supplierId, ct))
        {
            return SupplierGroupEndpoints.NotFound("Supplier", supplierId);
        }

        var layouts = await db.SupplierLayouts.AsNoTracking().Include(l => l.Versions)
            .Where(l => l.Scope == SupplierLayoutScope.Supplier && l.SupplierId == supplierId && (type == null || l.DocumentTypeKey == type))
            .ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<SupplierLayoutResponse>>([.. layouts.OrderBy(l => l.DocumentTypeKey, StringComparer.Ordinal).ThenBy(l => l.LayoutKey, StringComparer.Ordinal).Select(Response)]);
    }

    private static async Task<Results<Ok<IReadOnlyList<SupplierLayoutResponse>>, ProblemHttpResult>> ListGroupLayoutsAsync(Guid groupId, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, "Layouts", ct) is { } problem)
        {
            return problem;
        }

        if (!await db.SupplierGroups.AsNoTracking().AnyAsync(g => g.Id == groupId, ct))
        {
            return SupplierGroupEndpoints.NotFound("Supplier group", groupId);
        }

        var layouts = await db.SupplierLayouts.AsNoTracking().Include(l => l.Versions)
            .Where(l => l.Scope == SupplierLayoutScope.Group && l.GroupId == groupId)
            .ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<SupplierLayoutResponse>>([.. layouts.OrderBy(l => l.DocumentTypeKey, StringComparer.Ordinal).ThenBy(l => l.LayoutKey, StringComparer.Ordinal).Select(Response)]);
    }

    private static async Task<Results<Ok<SupplierLayoutResponse>, ProblemHttpResult>> GetLayoutAsync(Guid layoutId, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, "Layouts", ct) is { } problem)
        {
            return problem;
        }

        var layout = await db.SupplierLayouts.AsNoTracking().Include(l => l.Versions).SingleOrDefaultAsync(l => l.Id == layoutId, ct);
        return layout is null ? SupplierGroupEndpoints.NotFound("Layout", layoutId) : TypedResults.Ok(Response(layout));
    }

    private static async Task<Results<Ok<SupplierLayoutVersionResponse>, ProblemHttpResult>> GetVersionAsync(Guid layoutId, int version, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, "Layouts", ct) is { } problem)
        {
            return problem;
        }

        var row = await db.SupplierLayoutVersions.AsNoTracking().Include(v => v.Layout).SingleOrDefaultAsync(v => v.LayoutId == layoutId && v.Version == version, ct);
        if (row?.Layout is not { } layout)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Layout {layoutId} has no version {version}.");
        }

        var mapping = LayoutMapping.TryParse(row.MappingJson, out _);
        return TypedResults.Ok(new SupplierLayoutVersionResponse(
            layout.Id,
            layout.LayoutKey,
            Summary(row),
            Element(row.IdentificationJson) ?? default,
            Element(row.MappingJson) ?? default,
            mapping?.Coverage ?? [],
            Element(row.ReplayJson),
            Element(row.SimilarityProfileJson)));
    }

    private static async Task<Results<Ok<LayoutResolveResponse>, ValidationProblem, ProblemHttpResult>> ResolveAsync(
        Guid supplierId,
        Guid? documentId,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, "Layouts", ct) is { } problem)
        {
            return problem;
        }

        if (documentId is not { } id)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["documentId"] = ["documentId names the document to resolve the layout for."] });
        }

        var setting = await LayoutSettings.LoadAsync(db, tenant.TenantId!.Value, supplierId, ct);
        if (setting is null)
        {
            return SupplierGroupEndpoints.NotFound("Supplier", supplierId);
        }

        var document = await db.Documents.AsNoTracking().Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return SupplierGroupEndpoints.NotFound("Document", id);
        }

        var words = await WordsAsync(blobs, document, ct);
        var fixedType = document.TypeSource is TypeSource.Human or TypeSource.Supplier or TypeSource.Structured && !document.Type.IsUnknown ? document.Type.Value : null;
        var result = LayoutResolver.Instance.Resolve(new LayoutResolverInput(
            new IdentificationContext(words, setting.Values, LayoutSettings.SourceOf(document)),
            setting.Candidates,
            LayoutSettings.ItemKindOf(document),
            fixedType,
            setting.LayoutsRequired,
            document.ReceivingEntityId));
        var suggestions = result.Resolution == LayoutResolution.Identified ? [] : Suggest(words, document, result, setting);
        return TypedResults.Ok(new LayoutResolveResponse(
            document.Id,
            supplierId,
            result.Resolution.ToString(),
            result.LayoutManaged,
            result.Selected is { } s ? Evaluation(s) : null,
            [.. result.Tied.Select(Evaluation)],
            result.Pending is { } p ? Evaluation(p) : null,
            [.. result.Evaluated.Select(Evaluation)],
            suggestions));
    }

    private static async Task<Results<Ok<IReadOnlyList<LayoutSuggestionDto>>, ProblemHttpResult>> SuggestionsAsync(
        Guid id,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, "Layout suggestions", ct) is { } problem)
        {
            return problem;
        }

        var document = await db.Documents.AsNoTracking().Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return SupplierGroupEndpoints.NotFound("Document", id);
        }

        if (document.SupplierId is not { } supplierId || await LayoutSettings.LoadAsync(db, tenant.TenantId!.Value, supplierId, ct) is not { } setting)
        {
            return TypedResults.Ok<IReadOnlyList<LayoutSuggestionDto>>([]);
        }

        var words = await WordsAsync(blobs, document, ct);
        var result = LayoutResolver.Instance.Resolve(new LayoutResolverInput(
            new IdentificationContext(words, setting.Values, LayoutSettings.SourceOf(document)),
            setting.Candidates,
            LayoutSettings.ItemKindOf(document),
            null,
            setting.LayoutsRequired,
            document.ReceivingEntityId));
        return TypedResults.Ok<IReadOnlyList<LayoutSuggestionDto>>(Suggest(words, document, result, setting));
    }

    private static async Task<Results<Ok<IdentificationTestResponse>, ValidationProblem, ProblemHttpResult>> TestAsync(
        IdentificationTestRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, "Identification rule tests", ct) is { } problem)
        {
            return problem;
        }

        var errors = new Errors();
        var scope = request.Scope ?? new IdentificationTestScope();
        var scopes = (scope.SupplierId is null ? 0 : 1) + (scope.GroupId is null ? 0 : 1) + (scope.Tenant == true ? 1 : 0);
        if (scopes != 1)
        {
            errors.Add("scope", "scope names exactly one of supplierId, groupId and tenant: true.");
        }

        if (request.DocumentIds is { Count: > MaxDocumentIds })
        {
            errors.Add("documentIds", $"At most {MaxDocumentIds} documents.");
        }

        if (request.LastN is { } n && n is < 1 or > MaxLastN)
        {
            errors.Add("lastN", $"lastN is 1 to {MaxLastN}.");
        }

        if (request.TypeKey is { } typeKey && !DocumentTypeKey.TryParse(typeKey, out _))
        {
            errors.Add("typeKey", $"'{typeKey}' is not a type key.");
        }

        IdentificationRule? rule = null;
        if (request.Rule.ValueKind != JsonValueKind.Object)
        {
            errors.Add("rule", "rule is an identification rule object (all, any, none).");
        }
        else
        {
            rule = IdentificationRuleChecks.Check(request.Rule.GetRawText(), scope.Tenant == true ? IdentificationScope.Tenant : IdentificationScope.Layout, out var problems);
            foreach (var group in problems.GroupBy(p => p.Path))
            {
                errors.Add($"rule.{group.Key}", string.Join(" ", group.Select(p => p.Message).Distinct()));
            }
        }

        if (scope.SupplierId is { } sid && !await db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == sid, ct))
        {
            errors.Add("scope.supplierId", $"No supplier {sid} in this tenant.");
        }

        if (scope.GroupId is { } gid && !await db.SupplierGroups.AsNoTracking().AnyAsync(g => g.Id == gid, ct))
        {
            errors.Add("scope.groupId", $"No supplier group {gid} in this tenant.");
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        // The documents of the scope: the ones named, or the latest of the scope (ordered in memory: SQLite cannot order a
        // DateTimeOffset column, docs/09).
        var query = db.Documents.AsNoTracking().Where(d => d.Status != DocumentStatus.Cancelled);
        if (scope.SupplierId is { } supplierId)
        {
            query = query.Where(d => d.SupplierId == supplierId);
        }
        else if (scope.GroupId is { } groupId)
        {
            var members = db.Suppliers.Where(s => s.GroupId == groupId).Select(s => s.Id);
            query = query.Where(d => d.SupplierId != null && members.Contains(d.SupplierId.Value));
        }

        if (request.TypeKey is { } type)
        {
            var key = DocumentTypeKey.Parse(type);
            query = query.Where(d => d.Type == key);
        }

        List<Guid> ids;
        if (request.DocumentIds is { Count: > 0 } named)
        {
            ids = await query.Where(d => named.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct);
        }
        else
        {
            var recent = await query.Select(d => new { d.Id, d.CreatedAt }).ToListAsync(ct);
            ids = [.. recent.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(request.LastN ?? DefaultLastN).Select(d => d.Id)];
        }

        var documents = await db.Documents.AsNoTracking().Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion)
            .Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        var versionIds = documents.Where(d => d.SupplierLayoutVersionId != null).Select(d => d.SupplierLayoutVersionId!.Value).Distinct().ToList();
        var layoutsOfVersions = await db.SupplierLayoutVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.LayoutId, v.Layout!.LayoutKey }).ToListAsync(ct);
        var values = new Dictionary<Guid, SupplierValues>();
        var results = new List<IdentificationTestDocument>();
        foreach (var document in documents.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id))
        {
            var supplierValues = SupplierValues.None;
            if (scope.Tenant != true && document.SupplierId is { } owner)
            {
                if (!values.TryGetValue(owner, out var known))
                {
                    known = (await LayoutSettings.LoadAsync(db, tenant.TenantId!.Value, owner, ct))?.Values ?? SupplierValues.None;
                    values[owner] = known;
                }

                supplierValues = known;
            }

            var words = await WordsAsync(blobs, document, ct);
            var evaluated = IdentificationRuleEvaluator.Evaluate(rule!, new IdentificationContext(words, supplierValues, LayoutSettings.SourceOf(document)));
            var layout = layoutsOfVersions.FirstOrDefault(v => v.Id == document.SupplierLayoutVersionId);
            results.Add(new IdentificationTestDocument(
                document.Id,
                document.IngestionItem?.FileName,
                document.Type.Value,
                document.Status.ToString(),
                document.SupplierId,
                layout?.LayoutId,
                layout?.LayoutKey,
                evaluated.Holds,
                [.. evaluated.Evidence.Select(IdentificationEvidenceDto.From)],
                evaluated.Failed));
        }

        var overlaps = results.Where(r => r.Matched && r.LayoutId is not null)
            .GroupBy(r => (r.LayoutId!.Value, r.LayoutKey!))
            .Select(g => new IdentificationOverlap(g.Key.Value, g.Key.Item2, g.Count(), [.. g.Select(r => r.DocumentId)]))
            .ToList();
        return TypedResults.Ok(new IdentificationTestResponse(rule!.Describe(), rule.Hash, rule.PositiveCount, results.Count, results.Count(r => r.Matched), results, overlaps));
    }

    private static List<LayoutSuggestionDto> Suggest(DocumentWords words, Document document, LayoutResolverResult result, LayoutSetting setting) =>
    [
        .. LayoutSuggester.Suggest(
                SimilarityProfile.Of(words, LayoutSettings.ItemKindOf(document)),
                result.Evaluated.Where(e => e.Candidate.State is LayoutCandidateState.Active or LayoutCandidateState.Degraded)
                    .Select(e => (e, setting.Profiles.GetValueOrDefault(e.Candidate.VersionId))))
            .Select(s => new LayoutSuggestionDto(s.LayoutId, s.LayoutKey, s.VersionId, s.DocumentTypeKey, s.Similarity, s.Differences)),
    ];

    private static async Task<DocumentWords> WordsAsync(IContainerBlobStore blobs, Document document, CancellationToken ct)
    {
        try
        {
            return DocumentWords.From(await PageSources.ReadTextLayerAsync(blobs, document, ct));
        }
        catch (BlobNotFoundException)
        {
            return DocumentWords.Empty;
        }
    }

    private static LayoutEvaluationDto Evaluation(LayoutEvaluation e) => new(
        e.Candidate.LayoutId,
        e.Candidate.LayoutKey,
        e.Candidate.VersionId,
        e.Candidate.Version,
        e.Candidate.DocumentTypeKey,
        e.Candidate.State.ToString(),
        e.Candidate.Scope.ToString(),
        e.Candidate.Priority,
        e.Candidate.Rule.PositiveCount,
        e.Result.Holds,
        [.. e.Result.Evidence.Select(IdentificationEvidenceDto.From)],
        e.Result.Failed);

    private static SupplierLayoutResponse Response(SupplierLayout layout) => new(
        layout.Id,
        layout.Scope.ToString(),
        layout.SupplierId,
        layout.GroupId,
        layout.DocumentTypeKey,
        layout.LayoutKey,
        layout.Name,
        layout.ReceivingEntityId,
        layout.ItemKinds,
        layout.Priority,
        layout.ActiveVersion,
        layout.ShadowVersion,
        layout.Status.ToString(),
        layout.CreatedFromDocumentId,
        layout.LastMatchedAt,
        layout.CreatedAt,
        [.. layout.Versions.OrderByDescending(v => v.Version).Select(Summary)]);

    private static SupplierLayoutVersionSummary Summary(SupplierLayoutVersion v) => new(
        v.Id,
        v.Version,
        v.Status.ToString(),
        v.Origin.ToString(),
        v.IdentificationHash,
        IdentificationRule.TryParse(v.IdentificationJson, out var error) is { } rule ? rule.Describe() : $"(the stored rule does not read: {error})",
        v.ReviewWarmup,
        v.WarmupSeen,
        v.Identified,
        v.Applied,
        v.Agreed,
        v.Corrected,
        v.ConsecutiveFailures,
        v.CreatedBy,
        v.CreatedAt,
        v.ApprovedBy,
        v.ApprovedAt);

    private static JsonElement? Element(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
