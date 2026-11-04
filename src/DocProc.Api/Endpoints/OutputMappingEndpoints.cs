using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Configuration;
using DocProc.Application.Outputs;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Outputs;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>A platform integration template (<c>GET /output-templates</c>, docs/11 §13.9) and the tenant's mappings copied from it.</summary>
public sealed record OutputTemplateSummary(string Key, int Version, string Name, string? Description, string? SourceTypeKey, string Engine, string ContentType, IReadOnlyList<string> CopiedAs);

/// <summary>One version of an output mapping, without its expression.</summary>
public sealed record OutputMappingVersionSummary(
    Guid Id,
    int Version,
    string State,
    string Engine,
    string ContentType,
    int Revision,
    bool HasTargetSchema,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    string? PublishedBy,
    string? ChangeNote)
{
    internal static OutputMappingVersionSummary From(OutputMappingVersion v) => new(
        v.Id, v.Version, v.State.ToString(), v.Engine, v.ContentType, v.Revision, v.TargetSchemaJson is not null, v.CreatedAt, v.CreatedBy, v.UpdatedAt, v.PublishedAt, v.PublishedBy, v.ChangeNote);
}

/// <summary>An output mapping of the tenant, or a platform template (<c>Origin</c> Platform, read-only), with its versions.</summary>
public sealed record OutputMappingDetail(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string? SourceTypeKey,
    string Origin,
    string? BaseTemplate,
    int? BaseVersion,
    string Status,
    int? PublishedVersion,
    int? DraftVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<OutputMappingVersionSummary> Versions)
{
    internal static OutputMappingDetail From(OutputMapping m, string? baseTemplate) => new(
        m.Id,
        m.Key,
        m.Name,
        m.Description,
        m.SourceTypeKey,
        m.Origin.ToString(),
        baseTemplate,
        m.BaseVersion,
        m.Status.ToString(),
        m.PublishedVersion,
        m.Versions.FirstOrDefault(v => v.State == OutputMappingVersionState.Draft)?.Version,
        m.CreatedAt,
        m.UpdatedAt,
        [.. m.Versions.OrderBy(v => v.Version).Select(OutputMappingVersionSummary.From)]);
}

/// <summary>One version with its expression, target schema and what it reads.</summary>
public sealed record OutputMappingVersionDetail(
    string Key,
    OutputMappingVersionSummary Version,
    string Expression,
    string? FileNameTemplate,
    JsonElement? TargetSchema,
    IReadOnlyList<string> ReferencedPaths,
    IReadOnlyList<Guid> SampleDocumentIds);

/// <summary>Where a new mapping comes from: a platform template to copy.</summary>
public sealed record OutputMappingSource(string Template);

/// <summary>
/// <c>POST /output-mappings</c>: <see cref="From"/> copies a platform template (under <see cref="Key"/>, or the
/// template's key), or a mapping of the tenant's own from <see cref="Key"/>, <see cref="Name"/> and an optional first
/// draft (<see cref="Engine"/>, <see cref="Expression"/>, <see cref="TargetSchema"/>). Either way version 1 is a draft.
/// </summary>
public sealed record CreateOutputMappingRequest(
    OutputMappingSource? From,
    string? Key,
    string? Name,
    string? Description,
    string? SourceTypeKey,
    string? Engine,
    string? Expression,
    string? ContentType,
    JsonElement? TargetSchema);

/// <summary>Every field optional; an empty <see cref="SourceTypeKey"/> clears it (any type). <see cref="Status"/> is Active or Archived.</summary>
public sealed record PatchOutputMappingRequest(string? Name, string? Description, string? SourceTypeKey, string? Status);

/// <summary>A draft's content (<c>PUT /output-mappings/{key}/versions/{v}</c>, If-Match: the revision).</summary>
public sealed record SaveOutputMappingDraftRequest(
    string Engine,
    string? Expression,
    string? ContentType,
    string? FileNameTemplate,
    JsonElement? TargetSchema,
    IReadOnlyList<Guid>? SampleDocumentIds);

public sealed record PublishOutputMappingRequest(string? ChangeNote);

/// <summary>A render error as the API shows it: its kind (<c>syntax</c>, <c>runtime</c>, <c>required</c>, <c>timeLimit</c>, ...), the engine's code, the message and the position in the expression.</summary>
public sealed record RenderErrorDto(string Kind, string? Code, string Message, int? Position)
{
    internal static RenderErrorDto From(RenderError e) => new(e.Kind, e.Code, e.Message, e.Position);
}

public sealed record TargetSchemaErrorDto(string Path, string Location, string Keyword, string Message)
{
    internal static TargetSchemaErrorDto From(TargetSchemaError e) => new(e.Path, e.Location, e.Keyword, e.Message);
}

/// <summary>Why a version cannot be published: the expression's or the target schema's error.</summary>
public sealed record OutputMappingCheckReport(IReadOnlyList<RenderErrorDto> Errors, IReadOnlyList<string> ReferencedPaths, IReadOnlyList<string> Tables);

/// <summary>
/// <c>POST /output-mappings:preview</c> (docs/11 §13.1, §18): a mapping version (<see cref="MappingVersionId"/>) or an
/// expression (<see cref="Expression"/>, with <see cref="Engine"/> and <see cref="TargetSchema"/>), rendered over a
/// document's values view (<see cref="DocumentId"/>) or over <see cref="SampleValues"/>, without delivering.
/// <see cref="Event"/> sets <c>meta.event</c> (default <c>document.approved</c>); <see cref="IncludeValues"/> returns
/// the values view rendered over, for the editor's source tree.
/// </summary>
public sealed record OutputPreviewRequest(
    Guid? MappingVersionId,
    string? Expression,
    string? Engine,
    JsonElement? TargetSchema,
    Guid? DocumentId,
    JsonElement? SampleValues,
    string? Event,
    bool? IncludeValues);

/// <summary>
/// What the preview rendered: the output (null with <c>undefined</c> when the expression yields nothing), its size,
/// the render errors, the target-schema errors, the values-view paths and lookup tables the expression reads, how long
/// the render took and, on request, the values view.
/// </summary>
public sealed record OutputPreviewResponse(
    JsonElement? Output,
    bool Undefined,
    int OutputBytes,
    IReadOnlyList<RenderErrorDto> Errors,
    IReadOnlyList<TargetSchemaErrorDto> SchemaErrors,
    IReadOnlyList<string> ReferencedPaths,
    IReadOnlyList<string> Tables,
    long ElapsedMs,
    JsonElement? Values);

/// <summary>
/// Output mappings (docs/11 §13, §18 "Outputs"): the platform integration templates, the tenant's mappings with
/// versions, draft edits with If-Match, publish, and the server-side preview. Everything needs the tenant's
/// <c>outputs</c> switch; reading needs <see cref="Policies.Read"/>, changing and previewing
/// <see cref="Policies.IntegrationAdmin"/> (TenantAdmin, SuperAdmin; docs/11 §16).
/// </summary>
public static partial class OutputMappingEndpoints
{
    private const string What = "Output mappings";

    /// <summary>The request limit of the writes that carry an expression and a schema.</summary>
    private const long MaxMappingRequestBytes = RenderLimits.MaxExpressionLength + RenderLimits.MaxTargetSchemaLength + (64 * 1024);

    /// <summary>The request limit of a preview (sample values up to the render's input size).</summary>
    private const long MaxPreviewRequestBytes = 8L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex KeyPattern();

    public static RouteGroupBuilder MapOutputMappings(this RouteGroupBuilder group)
    {
        group.MapGet("/output-templates", ListTemplatesAsync)
            .WithTags("Outputs")
            .RequireAuthorization(Policies.Read)
            .WithName("ListOutputTemplates")
            .WithSummary("The platform integration templates a tenant can copy (std.event, generic-ap-json), with the tenant's mappings copied from each.");

        group.MapPost("/output-mappings:preview", PreviewAsync)
            .WithTags("Outputs")
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(MaxPreviewRequestBytes))
            .RequireRateLimiting(OutputPreviewOptions.RateLimitPolicy)
            .WithName("PreviewOutputMapping")
            .WithSummary("Renders a mapping version or an expression over a document's values view (or sample values) on the server, without delivering; returns the output, render and target-schema errors and the paths it reads. A few previews run at a time per tenant (429 beyond).");

        var mappings = group.MapGroup("/output-mappings").WithTags("Outputs");
        mappings.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("ListOutputMappings")
            .WithSummary("The tenant's output mappings and the platform templates, with their versions.");
        mappings.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(MaxMappingRequestBytes))
            .WithName("CreateOutputMapping")
            .WithSummary("Copies a platform template ({from: {template}}) or creates a mapping of the tenant's own; version 1 is a draft.");
        mappings.MapGet("/{key}", GetAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetOutputMapping")
            .WithSummary("One mapping (the tenant's, else the platform template of that key) with its versions.");
        mappings.MapPatch("/{key}", PatchAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithName("UpdateOutputMapping")
            .WithSummary("Renames, describes, scopes to a type or archives one of the tenant's mappings.");
        mappings.MapPost("/{key}/versions", CreateDraftAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithName("CreateOutputMappingDraft")
            .WithSummary("Starts the next version as a draft, from the published version (409 when a draft exists).");
        mappings.MapGet("/{key}/versions/{version:int}", GetVersionAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetOutputMappingVersion")
            .WithSummary("One version with its expression and target schema; the ETag is the draft revision.");
        mappings.MapPut("/{key}/versions/{version:int}", SaveDraftAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(MaxMappingRequestBytes))
            .WithName("SaveOutputMappingDraft")
            .WithSummary("Replaces a draft (If-Match: the revision read). The expression and target schema are checked on publish.");
        mappings.MapPost("/{key}/versions/{version:int}:publish", PublishAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithName("PublishOutputMappingVersion")
            .WithSummary("Publishes a draft when its expression parses and its target schema is one (422 with the errors otherwise); the previous version is deprecated.");
        return group;
    }

    private static async Task<Results<Ok<IReadOnlyList<OutputTemplateSummary>>, ProblemHttpResult>> ListTemplatesAsync(ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        var copies = await db.OutputMappings.AsNoTracking().Where(m => m.TenantId != null && m.BaseMappingId != null).Select(m => new { m.Key, m.BaseMappingId }).ToListAsync(ct);
        IReadOnlyList<OutputTemplateSummary> templates =
        [
            .. OutputTemplates.All.Select(t => new OutputTemplateSummary(
                t.Key, t.Version, t.Name, t.Description, t.SourceTypeKey, t.Engine, t.ContentType,
                [.. copies.Where(c => c.BaseMappingId == OutputTemplates.PlatformMappingId(t.Key)).Select(c => c.Key).Order(StringComparer.Ordinal)])),
        ];
        return TypedResults.Ok(templates);
    }

    private static async Task<Results<Ok<IReadOnlyList<OutputMappingDetail>>, ProblemHttpResult>> ListAsync(ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        var rows = await db.OutputMappings.AsNoTracking().Include(m => m.Versions).ToListAsync(ct);
        var templates = rows.Where(m => m.TenantId is null).ToDictionary(m => m.Id, m => m.Key);
        IReadOnlyList<OutputMappingDetail> result =
        [
            .. rows.OrderBy(m => m.TenantId is null ? 1 : 0).ThenBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => OutputMappingDetail.From(m, m.BaseMappingId is { } b && templates.TryGetValue(b, out var k) ? k : null)),
        ];
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<OutputMappingDetail>, ProblemHttpResult>> GetAsync(string key, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        var mapping = await FindAsync(db, key, tracked: false, ct);
        return mapping is null ? MappingNotFound(key) : TypedResults.Ok(await DetailAsync(db, mapping, ct));
    }

    private static async Task<Results<Created<OutputMappingDetail>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateOutputMappingRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        var tenantId = tenant.TenantId!.Value;
        var actor = DocumentQueries.ActorName(user);
        var now = clock.GetUtcNow();
        OutputMapping mapping;
        OutputMappingVersion draft;
        if (request.From is { } from)
        {
            var template = OutputTemplates.Find(from.Template);
            if (template is null)
            {
                return TypedResults.ValidationProblem(Field("from.template", $"There is no output template '{from.Template}'; GET /output-templates lists them."));
            }

            var key = request.Key ?? template.Key;
            if (Problems(key, request.Name, request.Description, request.SourceTypeKey) is { } invalid)
            {
                return invalid;
            }

            mapping = NewMapping(tenantId, key, request.Name ?? template.Name, request.Description ?? template.Description, request.SourceTypeKey ?? template.SourceTypeKey, OutputMappingOrigin.Copied, actor, now);
            mapping.BaseMappingId = OutputTemplates.PlatformMappingId(template.Key);
            mapping.BaseVersion = template.Version;
            draft = NewVersion(mapping, 1, template.Engine, template.Expression, template.ContentType, null, template.TargetSchemaJson, actor, now);
            draft.ChangeNote = $"Copied {template.Key} v{template.Version}";
        }
        else
        {
            if (request.Key is null || request.Name is null)
            {
                return TypedResults.ValidationProblem(Field("key", "Give from {template} to copy a template, or key and name for a mapping of the tenant's own."));
            }

            if (Problems(request.Key, request.Name, request.Description, request.SourceTypeKey) is { } invalid)
            {
                return invalid;
            }

            var engine = request.Engine ?? OutputEngines.Jsonata;
            if (ContentProblems(engine, request.Expression, request.ContentType, null, request.TargetSchema, null) is { } contentInvalid)
            {
                return contentInvalid;
            }

            mapping = NewMapping(tenantId, request.Key, request.Name, request.Description, request.SourceTypeKey, OutputMappingOrigin.Custom, actor, now);
            draft = NewVersion(mapping, 1, engine, request.Expression ?? string.Empty, request.ContentType ?? "application/json", null, SchemaText(request.TargetSchema), actor, now);
        }

        if (await db.OutputMappings.AnyAsync(m => m.TenantId == tenantId && m.Key == mapping.Key, ct))
        {
            return Conflict("Mapping exists", $"The tenant already has an output mapping '{mapping.Key}'.");
        }

        mapping.Versions.Add(draft);
        db.OutputMappings.Add(mapping);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/output-mappings/{Uri.EscapeDataString(mapping.Key)}", await DetailAsync(db, mapping, ct));
    }

    private static async Task<Results<Ok<OutputMappingDetail>, ValidationProblem, ProblemHttpResult>> PatchAsync(
        string key,
        PatchOutputMappingRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        if (Problems(null, request.Name, request.Description, string.IsNullOrEmpty(request.SourceTypeKey) ? null : request.SourceTypeKey) is { } invalid)
        {
            return invalid;
        }

        OutputMappingStatus? status = null;
        if (request.Status is not null)
        {
            if (!TryParseEnum<OutputMappingStatus>(request.Status, out var parsed))
            {
                return TypedResults.ValidationProblem(Field("status", $"status is one of {Names<OutputMappingStatus>()}."));
            }

            status = parsed;
        }

        var mapping = await OwnAsync(db, tenant, key, ct);
        if (mapping is null)
        {
            return OwnNotFound(key);
        }

        mapping.Name = request.Name?.Trim() ?? mapping.Name;
        mapping.Description = request.Description is null ? mapping.Description : TrimToNull(request.Description);
        mapping.SourceTypeKey = request.SourceTypeKey is null ? mapping.SourceTypeKey : TrimToNull(request.SourceTypeKey);
        mapping.Status = status ?? mapping.Status;
        mapping.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, mapping, ct));
    }

    private static async Task<Results<Created<OutputMappingVersionSummary>, ProblemHttpResult>> CreateDraftAsync(
        string key,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        var mapping = await OwnAsync(db, tenant, key, ct);
        if (mapping is null)
        {
            return OwnNotFound(key);
        }

        await db.Entry(mapping).Collection(m => m.Versions).LoadAsync(ct);
        if (mapping.Versions.Any(v => v.State == OutputMappingVersionState.Draft))
        {
            return Conflict("Draft exists", $"Mapping '{key}' already has a draft; edit it or publish it first.");
        }

        var basis = mapping.Versions.FirstOrDefault(v => v.Version == mapping.PublishedVersion) ?? mapping.Versions.OrderByDescending(v => v.Version).First();
        var now = clock.GetUtcNow();
        var draft = NewVersion(mapping, mapping.Versions.Max(v => v.Version) + 1, basis.Engine, basis.Expression, basis.ContentType, basis.FileNameTemplate, basis.TargetSchemaJson, DocumentQueries.ActorName(user), now);
        draft.SampleDocumentIdsJson = basis.SampleDocumentIdsJson;
        db.OutputMappingVersions.Add(draft);
        mapping.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/output-mappings/{Uri.EscapeDataString(key)}/versions/{draft.Version}", OutputMappingVersionSummary.From(draft));
    }

    private static async Task<Results<Ok<OutputMappingVersionDetail>, ProblemHttpResult>> GetVersionAsync(
        string key,
        int version,
        ITenantContext tenant,
        HttpContext http,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        var mapping = await FindAsync(db, key, tracked: false, ct);
        var row = mapping is null ? null : await db.OutputMappingVersions.AsNoTracking().SingleOrDefaultAsync(v => v.MappingId == mapping.Id && v.Version == version, ct);
        if (row is null)
        {
            return VersionNotFound(key, version);
        }

        http.Response.Headers.ETag = ETagOf(row.Revision);
        return TypedResults.Ok(VersionDetail(key, row));
    }

    private static async Task<Results<Ok<OutputMappingVersionSummary>, ValidationProblem, ProblemHttpResult>> SaveDraftAsync(
        string key,
        int version,
        SaveOutputMappingDraftRequest request,
        ITenantContext tenant,
        HttpContext http,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        if (!TryReadIfMatch(http, out var revision))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match required", detail: "Send the draft's ETag (from GET .../versions/{v}) in If-Match, so a concurrent edit is not overwritten.");
        }

        if (ContentProblems(request.Engine, request.Expression, request.ContentType, request.FileNameTemplate, request.TargetSchema, request.SampleDocumentIds) is { } invalid)
        {
            return invalid;
        }

        var mapping = await OwnAsync(db, tenant, key, ct);
        var draft = mapping is null ? null : await db.OutputMappingVersions.SingleOrDefaultAsync(v => v.MappingId == mapping.Id && v.Version == version, ct);
        if (mapping is null || draft is null)
        {
            return mapping is null ? OwnNotFound(key) : VersionNotFound(key, version);
        }

        if (draft.State != OutputMappingVersionState.Draft)
        {
            return Conflict("Not a draft", $"Version {version} of '{key}' is {draft.State}; published versions never change. POST .../versions starts a new draft.");
        }

        if (draft.Revision != revision)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Draft changed", detail: $"The draft is at revision {draft.Revision}; read it again and reapply the change.");
        }

        var now = clock.GetUtcNow();
        draft.Engine = request.Engine;
        draft.Expression = request.Expression ?? string.Empty;
        draft.ContentType = request.ContentType ?? "application/json";
        draft.FileNameTemplate = TrimToNull(request.FileNameTemplate);
        draft.TargetSchemaJson = SchemaText(request.TargetSchema);
        draft.SampleDocumentIdsJson = JsonSerializer.Serialize(request.SampleDocumentIds ?? [], Json);
        draft.Revision++;
        draft.UpdatedAt = now;
        mapping.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Draft changed", detail: "The draft changed while it was saved; read it again.");
        }

        http.Response.Headers.ETag = ETagOf(draft.Revision);
        return TypedResults.Ok(OutputMappingVersionSummary.From(draft));
    }

    private static async Task<Results<Ok<OutputMappingDetail>, UnprocessableEntity<OutputMappingCheckReport>, ValidationProblem, ProblemHttpResult>> PublishAsync(
        string key,
        int version,
        ITenantContext tenant,
        HttpContext http,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IOutputRenderer renderer,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        PublishOutputMappingRequest? request = null;
        if (http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                request = await http.Request.ReadFromJsonAsync<PublishOutputMappingRequest>(ct);
            }
            catch (JsonException)
            {
                return TypedResults.ValidationProblem(Field("(body)", "The body is { \"changeNote\": \"...\" } or empty."));
            }
        }

        var note = TrimToNull(request?.ChangeNote);
        if (note is { Length: > 2000 })
        {
            return TypedResults.ValidationProblem(Field("changeNote", "At most 2000 characters."));
        }

        var mapping = await OwnAsync(db, tenant, key, ct);
        if (mapping is null)
        {
            return OwnNotFound(key);
        }

        await db.Entry(mapping).Collection(m => m.Versions).LoadAsync(ct);
        var draft = mapping.Versions.SingleOrDefault(v => v.Version == version);
        if (draft is null)
        {
            return VersionNotFound(key, version);
        }

        if (draft.State != OutputMappingVersionState.Draft)
        {
            return Conflict("Not a draft", $"Version {version} of '{key}' is {draft.State}.");
        }

        if (TryReadIfMatch(http, out var revision) && revision != draft.Revision)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Draft changed", detail: $"The draft is at revision {draft.Revision}.");
        }

        var check = await renderer.CheckAsync(
            draft.Engine == OutputEngines.Passthrough ? "$" : draft.Expression,
            draft.TargetSchemaJson is null ? null : JsonNode.Parse(draft.TargetSchemaJson) as JsonObject,
            ct);
        if (check.Error is { } error)
        {
            return TypedResults.UnprocessableEntity(new OutputMappingCheckReport([RenderErrorDto.From(error)], check.ReferencedPaths, check.Tables));
        }

        var now = clock.GetUtcNow();
        foreach (var older in mapping.Versions.Where(v => v.State == OutputMappingVersionState.Published))
        {
            older.State = OutputMappingVersionState.Deprecated;
            older.UpdatedAt = now;
        }

        draft.State = OutputMappingVersionState.Published;
        draft.ReferencedPathsJson = JsonSerializer.Serialize(check.ReferencedPaths, Json);
        draft.PublishedAt = now;
        draft.PublishedBy = DocumentQueries.ActorName(user);
        draft.ChangeNote = note ?? draft.ChangeNote;
        draft.UpdatedAt = now;
        mapping.PublishedVersion = version;
        mapping.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Mapping changed", "The mapping changed while it was published; read it again.");
        }

        return TypedResults.Ok(await DetailAsync(db, mapping, ct));
    }

    private static async Task<Results<Ok<OutputPreviewResponse>, ValidationProblem, ProblemHttpResult>> PreviewAsync(
        OutputPreviewRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IDocumentDataViewBuilder views,
        [FromServices] OutputRenderService render,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        if ((request.MappingVersionId is null) == (request.Expression is null && request.Engine != OutputEngines.Passthrough))
        {
            return TypedResults.ValidationProblem(Field("mappingVersionId", "Give either mappingVersionId or an expression (or engine passthrough), not both or neither."));
        }

        if ((request.DocumentId is null) == (request.SampleValues is null))
        {
            return TypedResults.ValidationProblem(Field("documentId", "Give either documentId or sampleValues, not both or neither."));
        }

        if (request.Event is not null && !OutputEvents.All.Contains(request.Event, StringComparer.Ordinal))
        {
            return TypedResults.ValidationProblem(Field("event", $"event is one of {string.Join(", ", OutputEvents.All)}."));
        }

        string engine;
        string expression;
        JsonObject? schema;
        if (request.MappingVersionId is { } versionId)
        {
            var version = await db.OutputMappingVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, ct);
            if (version is null)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Mapping version not found", detail: $"There is no output mapping version {versionId} for this tenant.");
            }

            engine = version.Engine;
            expression = version.Expression;
            schema = version.TargetSchemaJson is null ? null : JsonNode.Parse(version.TargetSchemaJson) as JsonObject;
        }
        else
        {
            engine = request.Engine ?? OutputEngines.Jsonata;
            if (ContentProblems(engine, request.Expression, null, null, request.TargetSchema, null) is { } invalid)
            {
                return invalid;
            }

            expression = request.Expression ?? string.Empty;
            schema = request.TargetSchema is { ValueKind: JsonValueKind.Object } given ? JsonNode.Parse(given.GetRawText()) as JsonObject : null;
        }

        JsonObject values;
        JsonObject? fields = null;
        DateOnly today;
        if (request.DocumentId is { } documentId)
        {
            var built = await views.BuildAsync(new DataViewRequest(documentId) { Event = request.Event ?? OutputEvents.Approved, EventAt = clock.GetUtcNow(), IncludeFields = OutputRenderService.UsesField(expression) }, ct);
            if (built is null)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Document not found", detail: $"The tenant has no document {documentId}.");
            }

            values = built.View;
            fields = built.Fields;
            today = built.Today;
        }
        else
        {
            if (request.SampleValues is not { ValueKind: JsonValueKind.Object } sample)
            {
                return TypedResults.ValidationProblem(Field("sampleValues", "sampleValues is a values view, a JSON object."));
            }

            values = JsonNode.Parse(sample.GetRawText())!.AsObject();
            var current = await CurrentTenantAsync(db, ct);
            if (current is null)
            {
                return UnknownTenant();
            }

            today = TenantClock.Today(current.TimeZone, clock.GetUtcNow());
        }

        var result = await render.RenderAsync(engine, expression, schema, values, fields, today, ct);
        return TypedResults.Ok(new OutputPreviewResponse(
            result.OutputText is null ? null : JsonDocument.Parse(result.OutputText).RootElement.Clone(),
            result.Undefined,
            result.OutputBytes,
            result.Error is null ? [] : [RenderErrorDto.From(result.Error)],
            [.. result.SchemaErrors.Select(TargetSchemaErrorDto.From)],
            result.ReferencedPaths,
            result.Tables,
            (long)result.Elapsed.TotalMilliseconds,
            request.IncludeValues == true ? JsonDocument.Parse(values.ToJsonString()).RootElement.Clone() : null));
    }

    /// <summary>The tenant guard and the <c>outputs</c> switch every route of this group checks first.</summary>
    internal static async Task<ProblemHttpResult?> GuardAsync(ITenantContext tenant, ITenantFeatures features, CancellationToken ct, string what = What)
    {
        if (RequireTenant(tenant, what) is { } problem)
        {
            return problem;
        }

        return await features.IsEnabledAsync(tenant.TenantId!.Value, TenantFeature.Outputs, ct)
            ? null
            : TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Feature not enabled",
                detail: "The 'outputs' feature is off for this tenant; a tenant admin switches it on in the settings (features.outputs).");
    }

    /// <summary>The tenant's mapping of the key, else the platform template of the key.</summary>
    internal static async Task<OutputMapping?> FindAsync(DocProcDbContext db, string key, bool tracked, CancellationToken ct)
    {
        var query = tracked ? db.OutputMappings : db.OutputMappings.AsNoTracking();
        var rows = await query.Where(m => m.Key == key).ToListAsync(ct);
        return rows.OrderBy(m => m.TenantId is null ? 1 : 0).FirstOrDefault();
    }

    private static async Task<OutputMapping?> OwnAsync(DocProcDbContext db, ITenantContext tenant, string key, CancellationToken ct) =>
        await db.OutputMappings.SingleOrDefaultAsync(m => m.TenantId == tenant.TenantId && m.Key == key, ct);

    private static async Task<OutputMappingDetail> DetailAsync(DocProcDbContext db, OutputMapping mapping, CancellationToken ct)
    {
        if (db.Entry(mapping).State == EntityState.Detached)
        {
            var versions = await db.OutputMappingVersions.AsNoTracking().Where(v => v.MappingId == mapping.Id).ToListAsync(ct);
            mapping.Versions.Clear();
            foreach (var v in versions)
            {
                mapping.Versions.Add(v);
            }
        }
        else
        {
            await db.Entry(mapping).Collection(m => m.Versions).LoadAsync(ct);
        }

        var template = mapping.BaseMappingId is { } baseId ? OutputTemplates.All.FirstOrDefault(t => OutputTemplates.PlatformMappingId(t.Key) == baseId)?.Key : null;
        return OutputMappingDetail.From(mapping, template);
    }

    private static OutputMappingVersionDetail VersionDetail(string key, OutputMappingVersion row) => new(
        key,
        OutputMappingVersionSummary.From(row),
        row.Expression,
        row.FileNameTemplate,
        row.TargetSchemaJson is null ? null : JsonDocument.Parse(row.TargetSchemaJson).RootElement.Clone(),
        JsonSerializer.Deserialize<List<string>>(row.ReferencedPathsJson, Json) ?? [],
        JsonSerializer.Deserialize<List<Guid>>(row.SampleDocumentIdsJson, Json) ?? []);

    private static OutputMapping NewMapping(Guid tenantId, string key, string name, string? description, string? sourceTypeKey, OutputMappingOrigin origin, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Key = key,
        Name = name.Trim(),
        Description = TrimToNull(description),
        SourceTypeKey = TrimToNull(sourceTypeKey),
        Origin = origin,
        Status = OutputMappingStatus.Active,
        CreatedAt = now,
        CreatedBy = actor,
        UpdatedAt = now,
    };

    private static OutputMappingVersion NewVersion(OutputMapping mapping, int version, string engine, string expression, string contentType, string? fileNameTemplate, string? targetSchema, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        MappingId = mapping.Id,
        TenantId = mapping.TenantId,
        Version = version,
        Engine = engine,
        Expression = expression,
        ContentType = contentType,
        FileNameTemplate = fileNameTemplate,
        TargetSchemaJson = targetSchema,
        State = OutputMappingVersionState.Draft,
        CreatedAt = now,
        CreatedBy = actor,
        UpdatedAt = now,
    };

    private static ValidationProblem? Problems(string? key, string? name, string? description, string? sourceTypeKey)
    {
        var errors = new Errors().Optional("name", name, 200).Optional("description", description, 2000);
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            errors.Add("name", "name is not empty.");
        }

        if (key is not null && !KeyPattern().IsMatch(key))
        {
            errors.Add("key", "key is a letter, then letters, digits, '_', '.' or '-', at most 64 characters.");
        }
        else if (key is not null && key.StartsWith("std.", StringComparison.Ordinal))
        {
            errors.Add("key", "Keys starting with 'std.' are the platform's.");
        }

        if (sourceTypeKey is not null && !DocumentTypeKey.TryParse(sourceTypeKey, out _))
        {
            errors.Add("sourceTypeKey", "sourceTypeKey is a document type key.");
        }

        return errors.ToProblem();
    }

    private static ValidationProblem? ContentProblems(string engine, string? expression, string? contentType, string? fileNameTemplate, JsonElement? targetSchema, IReadOnlyList<Guid>? samples)
    {
        var errors = new Errors().Optional("contentType", contentType, 100).Optional("fileNameTemplate", fileNameTemplate, 400);
        if (!OutputEngines.Available.Contains(engine, StringComparer.Ordinal))
        {
            errors.Add("engine", engine is "fieldmap" or "liquid"
                ? $"The '{engine}' engine comes later (fieldmap with the visual mapper, liquid in P6); this build has {string.Join(", ", OutputEngines.Available)}."
                : $"engine is one of {string.Join(", ", OutputEngines.Available)}.");
        }

        if (expression is { Length: > RenderLimits.MaxExpressionLength })
        {
            errors.Add("expression", $"The expression has more than {RenderLimits.MaxExpressionLength} characters.");
        }

        if (targetSchema is { } schema && schema.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            errors.Add("targetSchema", "targetSchema is a JSON Schema, a JSON object.");
        }
        else if (targetSchema is { ValueKind: JsonValueKind.Object } given && given.GetRawText().Length > RenderLimits.MaxTargetSchemaLength)
        {
            errors.Add("targetSchema", $"The target schema has more than {RenderLimits.MaxTargetSchemaLength} characters.");
        }

        if (samples is { Count: > 20 })
        {
            errors.Add("sampleDocumentIds", "At most 20 sample documents.");
        }

        return errors.ToProblem();
    }

    private static string? SchemaText(JsonElement? schema) => schema is { ValueKind: JsonValueKind.Object } given ? given.GetRawText() : null;

    private static Dictionary<string, string[]> Field(string field, string message) => new(StringComparer.Ordinal) { [field] = [message] };

    /// <summary>Reads If-Match as the draft revision: <c>"3"</c>, <c>W/"3"</c> or <c>3</c>.</summary>
    internal static bool TryReadIfMatch(HttpContext http, out int revision)
    {
        var value = http.Request.Headers.IfMatch.ToString().Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return int.TryParse(value.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }

    internal static string ETagOf(int revision) => $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";

    private static ProblemHttpResult MappingNotFound(string key) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Output mapping not found", detail: $"There is no output mapping '{key}'.");

    private static ProblemHttpResult OwnNotFound(string key) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Output mapping not found", detail: $"The tenant has no output mapping '{key}' (a platform template is copied, not changed).");

    private static ProblemHttpResult VersionNotFound(string key, int version) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Version not found", detail: $"Output mapping '{key}' has no version {version}.");
}
