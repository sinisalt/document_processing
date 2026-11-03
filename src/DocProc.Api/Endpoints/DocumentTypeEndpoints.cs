using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>
/// A platform template in the catalogue (<c>GET /document-templates</c>): its key and version, its name and
/// description, the legacy key existing tenants adopted it under (if any), the key adopting or copying it uses when
/// the request names none (<c>DefaultKey</c>) and the tenant's type keys that come from it (<c>AdoptedAs</c>).
/// </summary>
public sealed record DocumentTemplateSummary(string Key, int Version, IReadOnlyDictionary<string, string> Name, string? Description, string? LegacyKey, string DefaultKey, IReadOnlyList<string> AdoptedAs);

/// <summary>One version of a tenant's type, without its JSON.</summary>
public sealed record DocumentTypeVersionSummary(
    int Version,
    string State,
    int Revision,
    string? ContentHash,
    string? LlmSchema,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    string? PublishedBy,
    string? ChangeNote)
{
    internal static DocumentTypeVersionSummary From(DocumentTypeVersion v) => new(
        v.Version,
        v.State.ToString(),
        v.Revision,
        v.ContentHash?.Trim(),
        v.LlmSchemaName is null ? null : $"{v.LlmSchemaName}@{v.LlmSchemaVersion}",
        v.CreatedAt,
        v.CreatedBy,
        v.UpdatedAt,
        v.PublishedAt,
        v.PublishedBy,
        v.ChangeNote);
}

/// <summary>
/// A field of a type that <c>DocumentFieldIndex</c> holds (docs/11 §6.2): its definition path, which is the key of the
/// document list's <c>?field.&lt;path&gt;=value</c> filter and of its <c>fields</c> columns, its kind and its label by locale.
/// </summary>
public sealed record IndexedFieldDto(string Path, string Kind, IReadOnlyDictionary<string, string>? Label)
{
    /// <summary>
    /// The indexed fields of a definition, depth first: the published version's, else (a type not published yet) the
    /// latest version's. A definition that does not parse lists none.
    /// </summary>
    internal static IReadOnlyList<IndexedFieldDto> Of(IReadOnlyList<DocumentTypeVersion> versions)
    {
        var version = versions.Where(v => v.State == DocumentTypeVersionState.Published).MaxBy(v => v.Version) ?? versions.MaxBy(v => v.Version);
        if (version is null)
        {
            return [];
        }

        TypeDefinition definition;
        try
        {
            definition = DefinitionSerializer.Parse(version.DefinitionJson);
        }
        catch (DefinitionFormatException)
        {
            return [];
        }

        var fields = new List<IndexedFieldDto>();
        Collect(definition.Fields, null, fields);
        return fields;
    }

    private static void Collect(IReadOnlyList<FieldDefinition> definitions, string? prefix, List<IndexedFieldDto> into)
    {
        foreach (var field in definitions)
        {
            var path = prefix is null ? field.Key : $"{prefix}.{field.Key}";
            if (field.Indexed)
            {
                into.Add(new IndexedFieldDto(path, field.Kind, field.Label));
            }

            if (field.Item?.Fields is { } rows)
            {
                Collect(rows, $"{path}[*]", into);
            }

            if (field.Fields is { } members)
            {
                Collect(members, path, into);
            }
        }
    }
}

/// <summary>
/// A tenant's document type (docs/11 §6.1) with its versions, and the fields the inbox can filter by
/// (<c>IndexedFields</c>).
/// </summary>
public sealed record DocumentTypeDetail(
    string Key,
    IReadOnlyDictionary<string, string> Name,
    string? Description,
    string Origin,
    string Status,
    string? BaseTemplate,
    int? BaseVersion,
    int? PublishedVersion,
    int? DraftVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DocumentTypeVersionSummary> Versions,
    IReadOnlyList<IndexedFieldDto> IndexedFields)
{
    internal static DocumentTypeDetail From(CatalogueType type)
    {
        var d = type.Definition;
        return new DocumentTypeDetail(
            d.Key,
            d.Name,
            d.Description,
            d.Origin.ToString(),
            d.Status.ToString(),
            type.BaseTemplate,
            d.BaseVersion,
            d.PublishedVersion,
            type.Versions.FirstOrDefault(v => v.State == DocumentTypeVersionState.Draft)?.Version,
            d.CreatedAt,
            d.UpdatedAt,
            [.. type.Versions.Select(DocumentTypeVersionSummary.From)],
            IndexedFieldDto.Of(type.Versions));
    }
}

/// <summary>One version with its definition and, on request (<c>include=formSpec,jsonSchema,llmSchema,descriptors</c>), its compiled artifacts.</summary>
public sealed record DocumentTypeVersionDetail(
    string Key,
    DocumentTypeVersionSummary Version,
    JsonElement Definition,
    JsonElement? FormSpec,
    JsonElement? JsonSchema,
    JsonElement? LlmSchema,
    JsonElement? Descriptors);

/// <summary>Where a new type comes from: a platform template, adopted (follows it) or copied (a fork).</summary>
public sealed record DocumentTypeSource(string Template, string Mode);

/// <summary>
/// <c>POST /document-types</c> (docs/11 §18): <see cref="From"/> to adopt or copy a template (under <see cref="Key"/>,
/// or its legacy key or its id without <c>std.</c>), or <see cref="Definition"/> for a type of the tenant's own.
/// </summary>
public sealed record CreateDocumentTypeRequest(DocumentTypeSource? From, string? Key, JsonElement? Definition);

public sealed record PublishDocumentTypeRequest(string? ChangeNote);

public sealed record DefinitionProblemDto(string Where, string Message)
{
    internal static DefinitionProblemDto From(DefinitionProblem p) => new(p.Where, p.Message);
}

public sealed record FieldChangeDto(string Change, string FieldId, string? OldPath, string? NewPath, string? OldKind, string? NewKind);

public sealed record DependentReferenceDto(string Kind, string Name, string? FieldId, string Path);

/// <summary>The publish-time checks' report (docs/11 §5.7): what blocks, what to know, which fields changed.</summary>
public sealed record PublishReportDto(
    bool CanPublish,
    IReadOnlyList<DefinitionProblemDto> Problems,
    IReadOnlyList<DefinitionProblemDto> Warnings,
    IReadOnlyList<FieldChangeDto> Changes,
    IReadOnlyList<DependentReferenceDto> BlockingDependents,
    string? LlmSchema,
    string? ContentHash)
{
    internal static PublishReportDto From(PublishReport r) => new(
        r.CanPublish,
        [.. r.Problems.Select(DefinitionProblemDto.From)],
        [.. r.Warnings.Select(DefinitionProblemDto.From)],
        [.. r.Changes.Select(c => new FieldChangeDto(c.Change.ToString(), c.FieldId, c.OldPath, c.NewPath, c.OldKind, c.NewKind))],
        [.. r.BlockingDependents.Select(d => new DependentReferenceDto(d.Kind, d.Name, d.FieldId, d.Path))],
        r.Compiled?.LlmSchema.ToString(),
        r.Compiled?.ContentHash);
}

/// <summary>
/// The type catalogue API (docs/11 §18 "Types"): the platform templates, the tenant's types and their versions,
/// adopt, copy and new, draft edits with If-Match, validate and publish. Reading needs <see cref="Policies.Read"/>,
/// changing needs <see cref="Policies.ConfigAdmin"/> and the tenant's <c>configurableTypes</c> switch; a type of the
/// tenant's own (not from a template) also needs <c>customTypes</c>.
/// </summary>
public static class DocumentTypeEndpoints
{
    private const string What = "Document types";

    /// <summary>The request limit of the writes that carry a definition: its length cap and room for the wrapper.</summary>
    private const long MaxDefinitionRequestBytes = DocumentTypeCatalogue.MaxDefinitionLength + (16 * 1024);

    public static RouteGroupBuilder MapDocumentTypes(this RouteGroupBuilder group)
    {
        group.MapGet("/document-templates", ListTemplatesAsync)
            .WithTags("Document types")
            .RequireAuthorization(Policies.Read)
            .WithName("ListDocumentTemplates")
            .WithSummary("The platform templates a tenant can adopt or copy, with the tenant's types that come from each.");

        var types = group.MapGroup("/document-types").WithTags("Document types");
        types.MapGet("/", ListTypesAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("ListDocumentTypes")
            .WithSummary("The tenant's document types.");
        types.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.ConfigAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(MaxDefinitionRequestBytes))
            .WithName("CreateDocumentType")
            .WithSummary("Adopts or copies a platform template ({from}), or creates a type of the tenant's own ({definition}) as draft version 1.");
        types.MapGet("/{key}", GetTypeAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDocumentType")
            .WithSummary("One of the tenant's types with its versions.");
        types.MapGet("/{key}/versions", ListVersionsAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("ListDocumentTypeVersions")
            .WithSummary("The versions of a type, oldest first.");
        types.MapPost("/{key}/versions", CreateDraftAsync)
            .RequireAuthorization(Policies.ConfigAdmin)
            .WithName("CreateDocumentTypeDraft")
            .WithSummary("Starts the next version of a copied or custom type as a draft, from the published version.");
        types.MapGet("/{key}/versions/{version:int}", GetVersionAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDocumentTypeVersion")
            .WithSummary("One version with its definition; include=formSpec,jsonSchema,llmSchema,descriptors adds the compiled artifacts. The ETag is the draft revision.");
        types.MapPut("/{key}/versions/{version:int}", SaveDraftAsync)
            .RequireAuthorization(Policies.ConfigAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(MaxDefinitionRequestBytes))
            .WithName("SaveDocumentTypeDraft")
            .WithSummary("Replaces a draft's definition (If-Match: the revision read). The meta-schema is checked on save; everything else on validate and publish.");
        types.MapPost("/{key}/versions/{version:int}:validate", ValidateAsync)
            .RequireAuthorization(Policies.ConfigAdmin)
            .WithName("ValidateDocumentTypeVersion")
            .WithSummary("Runs the publish-time checks on a version and returns the report, without publishing.");
        types.MapPost("/{key}/versions/{version:int}:publish", PublishAsync)
            .RequireAuthorization(Policies.ConfigAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithName("PublishDocumentTypeVersion")
            .WithSummary("Publishes a draft when the publish-time checks pass (422 with the report otherwise); the previous version is deprecated.");
        return group;
    }

    private static async Task<Results<Ok<IReadOnlyList<DocumentTemplateSummary>>, ProblemHttpResult>> ListTemplatesAsync(ITenantContext tenant, [FromServices] DocumentTypeCatalogue catalogue, CancellationToken ct)
    {
        IReadOnlyList<CatalogueType> types = tenant.TenantId is { } tenantId ? await catalogue.ListAsync(tenantId, ct) : [];
        IReadOnlyList<DocumentTemplateSummary> templates =
        [
            .. StandardTemplates.All.Select(t => new DocumentTemplateSummary(
                t.Key,
                t.Version,
                t.Name,
                t.Description,
                t.LegacyKey,
                DocumentTypeCatalogue.DefaultKey(t),
                [.. types.Where(x => x.BaseTemplate == t.Key).Select(x => x.Definition.Key)])),
        ];
        return TypedResults.Ok(templates);
    }

    private static async Task<Results<Ok<IReadOnlyList<DocumentTypeDetail>>, ProblemHttpResult>> ListTypesAsync(ITenantContext tenant, [FromServices] DocumentTypeCatalogue catalogue, CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        IReadOnlyList<DocumentTypeDetail> types = [.. (await catalogue.ListAsync(tenant.TenantId!.Value, ct)).Select(DocumentTypeDetail.From)];
        return TypedResults.Ok(types);
    }

    private static async Task<Results<Ok<DocumentTypeDetail>, ProblemHttpResult>> GetTypeAsync(string key, ITenantContext tenant, [FromServices] DocumentTypeCatalogue catalogue, CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var type = await catalogue.FindAsync(tenant.TenantId!.Value, key, ct);
        return type is null ? TypeNotFound(key) : TypedResults.Ok(DocumentTypeDetail.From(type));
    }

    private static async Task<Results<Ok<IReadOnlyList<DocumentTypeVersionSummary>>, ProblemHttpResult>> ListVersionsAsync(string key, ITenantContext tenant, [FromServices] DocumentTypeCatalogue catalogue, CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var type = await catalogue.FindAsync(tenant.TenantId!.Value, key, ct);
        if (type is null)
        {
            return TypeNotFound(key);
        }

        IReadOnlyList<DocumentTypeVersionSummary> versions = [.. type.Versions.Select(DocumentTypeVersionSummary.From)];
        return TypedResults.Ok(versions);
    }

    private static async Task<Results<Ok<DocumentTypeVersionDetail>, ProblemHttpResult>> GetVersionAsync(
        string key,
        int version,
        string? include,
        ITenantContext tenant,
        HttpContext http,
        [FromServices] DocumentTypeCatalogue catalogue,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var type = await catalogue.FindAsync(tenant.TenantId!.Value, key, ct);
        var row = type?.Versions.SingleOrDefault(v => v.Version == version);
        if (row is null)
        {
            return VersionNotFound(key, version);
        }

        var parts = (include ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        http.Response.Headers.ETag = ETagOf(row.Revision);
        return TypedResults.Ok(new DocumentTypeVersionDetail(
            key,
            DocumentTypeVersionSummary.From(row),
            Parse(row.DefinitionJson)!.Value,
            parts.Contains("formSpec") ? Parse(row.FormSpecJson) : null,
            parts.Contains("jsonSchema") ? Parse(row.CompiledJsonSchema) : null,
            parts.Contains("llmSchema") ? Parse(row.CompiledLlmSchema) : null,
            parts.Contains("descriptors") ? Parse(row.DescriptorsJson) : null));
    }

    private static async Task<Results<Created<DocumentTypeDetail>, Ok<DocumentTypeDetail>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateDocumentTypeRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] ITenantFeatures features,
        [FromServices] DocumentTypeCatalogue catalogue,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var tenantId = tenant.TenantId!.Value;
        if (await RequireFeatureAsync(features, tenantId, TenantFeature.ConfigurableTypes, ct) is { } off)
        {
            return off;
        }

        var actor = DocumentQueries.ActorName(user);
        CatalogueResult<CatalogueType> result;
        switch (request)
        {
            case { From: { } from, Definition: null }:
                result = from.Mode switch
                {
                    "adopt" => await catalogue.AdoptAsync(tenantId, from.Template, request.Key, actor, ct),
                    "copy" => await catalogue.CopyAsync(tenantId, from.Template, request.Key, actor, ct),
                    _ => CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Invalid, "from.mode is 'adopt' or 'copy'.", [new DefinitionProblem("from.mode", $"'{from.Mode}' is neither 'adopt' nor 'copy'.")]),
                };
                break;
            case { From: null, Definition: { ValueKind: JsonValueKind.Object } definition }:
                if (await RequireFeatureAsync(features, tenantId, TenantFeature.CustomTypes, ct) is { } customOff)
                {
                    return customOff;
                }

                if (request.Key is { } named && (!definition.TryGetProperty("key", out var inner) || inner.ValueKind != JsonValueKind.String || inner.GetString() != named))
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["key"] = ["When both are given, key must equal definition.key."] });
                }

                result = await catalogue.CreateAsync(tenantId, definition.GetRawText(), actor, ct);
                break;
            default:
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["from"] = ["Give either from {template, mode} or a definition object, not both or neither."] });
        }

        switch (result.Status)
        {
            case CatalogueStatus.Created:
                return TypedResults.Created($"/api/v1/document-types/{Uri.EscapeDataString(result.Value!.Definition.Key)}", DocumentTypeDetail.From(result.Value));
            case CatalogueStatus.Ok:
                return TypedResults.Ok(DocumentTypeDetail.From(result.Value!));
            case CatalogueStatus.Invalid:
                return Invalid(result);
            default:
                return Problem(result);
        }
    }

    private static async Task<Results<Created<DocumentTypeVersionSummary>, ValidationProblem, ProblemHttpResult>> CreateDraftAsync(
        string key,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] ITenantFeatures features,
        [FromServices] DocumentTypeCatalogue catalogue,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        if (await RequireFeatureAsync(features, tenant.TenantId!.Value, TenantFeature.ConfigurableTypes, ct) is { } off)
        {
            return off;
        }

        if (await RequireCustomTypesForOwnTypeAsync(features, catalogue, tenant.TenantId!.Value, key, ct) is { } customOff)
        {
            return customOff;
        }

        var result = await catalogue.CreateDraftAsync(tenant.TenantId!.Value, key, DocumentQueries.ActorName(user), ct);
        if (result.Status == CatalogueStatus.Created)
        {
            return TypedResults.Created($"/api/v1/document-types/{Uri.EscapeDataString(key)}/versions/{result.Value!.Version}", DocumentTypeVersionSummary.From(result.Value));
        }

        return result.Status == CatalogueStatus.Invalid ? Invalid(result) : Problem(result);
    }

    private static async Task<Results<Ok<DocumentTypeVersionSummary>, ValidationProblem, ProblemHttpResult>> SaveDraftAsync(
        string key,
        int version,
        JsonElement definition,
        ITenantContext tenant,
        HttpContext http,
        ClaimsPrincipal user,
        [FromServices] ITenantFeatures features,
        [FromServices] DocumentTypeCatalogue catalogue,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        if (await RequireFeatureAsync(features, tenant.TenantId!.Value, TenantFeature.ConfigurableTypes, ct) is { } off)
        {
            return off;
        }

        if (await RequireCustomTypesForOwnTypeAsync(features, catalogue, tenant.TenantId!.Value, key, ct) is { } customOff)
        {
            return customOff;
        }

        if (!TryReadIfMatch(http, out var revision))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match required", detail: "Send the draft's ETag (from GET .../versions/{v}) in If-Match, so a concurrent edit is not overwritten.");
        }

        if (definition.ValueKind != JsonValueKind.Object)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["(root)"] = ["The body is the definition, a JSON object."] });
        }

        var result = await catalogue.SaveDraftAsync(tenant.TenantId!.Value, key, version, definition.GetRawText(), revision, DocumentQueries.ActorName(user), ct);
        if (result.Status == CatalogueStatus.Invalid)
        {
            return Invalid(result);
        }

        if (result.Status != CatalogueStatus.Ok)
        {
            return Problem(result);
        }

        http.Response.Headers.ETag = ETagOf(result.Value!.Revision);
        return TypedResults.Ok(DocumentTypeVersionSummary.From(result.Value));
    }

    private static async Task<Results<Ok<PublishReportDto>, ValidationProblem, ProblemHttpResult>> ValidateAsync(
        string key,
        int version,
        ITenantContext tenant,
        [FromServices] DocumentTypeCatalogue catalogue,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var result = await catalogue.ValidateAsync(tenant.TenantId!.Value, key, version, ct);
        if (result.Status == CatalogueStatus.Ok)
        {
            return TypedResults.Ok(PublishReportDto.From(result.Value!));
        }

        return result.Status == CatalogueStatus.Invalid ? Invalid(result) : Problem(result);
    }

    private static async Task<Results<Ok<DocumentTypeDetail>, UnprocessableEntity<PublishReportDto>, ValidationProblem, ProblemHttpResult>> PublishAsync(
        string key,
        int version,
        ITenantContext tenant,
        HttpContext http,
        ClaimsPrincipal user,
        [FromServices] ITenantFeatures features,
        [FromServices] DocumentTypeCatalogue catalogue,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        if (await RequireFeatureAsync(features, tenant.TenantId!.Value, TenantFeature.ConfigurableTypes, ct) is { } off)
        {
            return off;
        }

        if (await RequireCustomTypesForOwnTypeAsync(features, catalogue, tenant.TenantId!.Value, key, ct) is { } customOff)
        {
            return customOff;
        }

        int? revision = TryReadIfMatch(http, out var read) ? read : null;
        // The body is optional ({ changeNote }); read here, because an endpoint with a body parameter does not match a
        // request that sends none (no Content-Type), and "publish" without a note is the common call.
        PublishDocumentTypeRequest? request = null;
        if (http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                request = await http.Request.ReadFromJsonAsync<PublishDocumentTypeRequest>(ct);
            }
            catch (JsonException)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["(body)"] = ["The body is { \"changeNote\": \"...\" } or empty."] });
            }
        }

        var note = request?.ChangeNote?.Trim();
        if (note is { Length: > 2000 })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["changeNote"] = ["At most 2000 characters."] });
        }

        var result = await catalogue.PublishAsync(tenant.TenantId!.Value, key, version, revision, string.IsNullOrEmpty(note) ? null : note, DocumentQueries.ActorName(user), ct);
        switch (result.Status)
        {
            case CatalogueStatus.Ok:
                return TypedResults.Ok(DocumentTypeDetail.From(result.Value!));
            case CatalogueStatus.Blocked:
                return TypedResults.UnprocessableEntity(PublishReportDto.From(result.Report!));
            case CatalogueStatus.Invalid:
                return Invalid(result);
            default:
                return Problem(result);
        }
    }

    /// <summary>A type of the tenant's own (origin Custom) also needs <c>customTypes</c> to be edited or published (docs/07 P1 part A).</summary>
    private static async Task<ProblemHttpResult?> RequireCustomTypesForOwnTypeAsync(ITenantFeatures features, DocumentTypeCatalogue catalogue, Guid tenantId, string key, CancellationToken ct) =>
        (await catalogue.FindAsync(tenantId, key, ct))?.Definition.Origin == DefinitionOrigin.Custom
            ? await RequireFeatureAsync(features, tenantId, TenantFeature.CustomTypes, ct)
            : null;

    private static async Task<ProblemHttpResult?> RequireFeatureAsync(ITenantFeatures features, Guid tenantId, TenantFeature feature, CancellationToken ct)
    {
        if (await features.IsEnabledAsync(tenantId, feature, ct))
        {
            return null;
        }

        var name = JsonNamingPolicy.CamelCase.ConvertName(feature.ToString());
        return TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Feature not enabled",
            detail: $"The '{name}' feature is off for this tenant; a tenant admin switches it on in the settings (features.{name}).");
    }

    /// <summary>Reads If-Match as the draft revision: <c>"3"</c>, <c>W/"3"</c> or <c>3</c>.</summary>
    private static bool TryReadIfMatch(HttpContext http, out int revision)
    {
        var value = http.Request.Headers.IfMatch.ToString().Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return int.TryParse(value.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }

    private static string ETagOf(int revision) => $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";

    private static JsonElement? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ProblemHttpResult TypeNotFound(string key) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Document type not found", detail: $"The tenant has no document type '{key}'.");

    private static ProblemHttpResult VersionNotFound(string key, int version) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Version not found", detail: $"Document type '{key}' has no version {version}.");

    private static ValidationProblem Invalid<T>(CatalogueResult<T> result) => result.Problems is { Count: > 0 } problems
        ? TypedResults.ValidationProblem(
            problems.GroupBy(p => p.Where, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray(), StringComparer.Ordinal),
            detail: result.Detail)
        : TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["(root)"] = [result.Detail ?? "Invalid."] });

    private static ProblemHttpResult Problem<T>(CatalogueResult<T> result) => result.Status switch
    {
        CatalogueStatus.NotFound => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: result.Detail),
        CatalogueStatus.Conflict => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: result.Detail),
        CatalogueStatus.PreconditionFailed => TypedResults.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Draft changed", detail: result.Detail),
        _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected catalogue result", detail: result.Status.ToString()),
    };
}
