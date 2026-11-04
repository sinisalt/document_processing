using System.Globalization;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Mapping.Identification;
using DocProc.Pipeline.Layouts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>The body of creating a tenant fallback rule (docs/11 §8.5, §18): its name, priority, rule and excluded suppliers.</summary>
public sealed record CreateTypeIdentificationRuleRequest(string Name, JsonElement Rule, int? Priority = null, IReadOnlyList<Guid>? ExcludedSupplierIds = null);

/// <summary>The body of "use for all suppliers without own layouts" in review (docs/11 §12.10 step 5): the rule, and the type when it is not the document's.</summary>
public sealed record CreateTypeIdentificationRuleFromDocumentRequest(JsonElement Rule, string? TypeKey = null, string? Name = null, int? Priority = null);

/// <summary>The body of a new or edited Draft version: the rule and the excluded suppliers.</summary>
public sealed record TypeIdentificationRuleVersionRequest(JsonElement Rule, IReadOnlyList<Guid>? ExcludedSupplierIds = null);

/// <summary>The body of <c>PATCH /identification-rules/{id}</c>.</summary>
public sealed record PatchTypeIdentificationRuleRequest(string? Name = null, int? Priority = null);

/// <summary>The body of <c>:activate</c>: a rule whose test matched documents of other types is activated only when confirmed.</summary>
public sealed record ActivateTypeIdentificationRuleRequest(bool ConfirmOtherTypes = false);

/// <summary>One document a rule under test matched (at most <see cref="TypeIdentificationRuleEndpoints.MaxListedMatches"/> listed).</summary>
public sealed record TenantRuleTestMatch(Guid DocumentId, string TypeKey, Guid? SupplierId, string? FileName);

/// <summary>
/// The save-time test of a tenant fallback rule (docs/11 §8.5 "Checks at save"): evaluated on the last 500 approved
/// documents of the tenant, without the excluded suppliers and the suppliers that have layouts (own or group); how many it
/// would have matched, per type, and the summary sentence of the editor ("would have matched 37 price-list documents, 0
/// other documents").
/// </summary>
public sealed record TenantRuleTestReport(
    string RuleHash,
    string TypeKey,
    int Tested,
    int Matched,
    int SameType,
    int OtherTypes,
    IReadOnlyDictionary<string, int> ByType,
    IReadOnlyList<TenantRuleTestMatch> OtherTypeMatches,
    IReadOnlyList<TenantRuleTestMatch> Matches,
    string Summary,
    DateTimeOffset TestedAt);

public sealed record TypeIdentificationRuleVersionResponse(
    Guid Id,
    int Version,
    string State,
    JsonElement Rule,
    string Describe,
    string RuleHash,
    IReadOnlyList<Guid> ExcludedSupplierIds,
    TenantRuleTestReport? Test,
    int Revision,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record TypeIdentificationRuleResponse(
    Guid Id,
    string DocumentTypeKey,
    string Name,
    int Priority,
    int? ActiveVersion,
    string Status,
    Guid? CreatedFromDocumentId,
    int Matched,
    int Overridden,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TypeIdentificationRuleVersionResponse> Versions);

/// <summary>
/// The tenant fallback identification rules (docs/11 §6.6, §8.5, §18; P5), behind <c>supplierLayouts</c>: a type's rules
/// with their versions, a rule created by hand or from a document in review ("use for all suppliers without own
/// layouts"), Draft versions edited with If-Match, the 500-document test, activation (refused while the test matched
/// documents of other types unless confirmed) and disabling. Reading needs <c>Read</c>, every write <c>ConfigAdmin</c>.
/// A rule is checked at save in tenant scope: no <c>supplierValue</c>, the meta-schema, patterns within
/// <c>PatternSize</c>, at least one positive identifier. Each edit is a Draft; activating one retires the previous version,
/// and documents keep the version that typed them.
/// </summary>
public static class TypeIdentificationRuleEndpoints
{
    /// <summary>The approved documents a rule is tested against (docs/11 §8.5).</summary>
    public const int TestDocuments = 500;

    /// <summary>The matched documents a test report lists.</summary>
    public const int MaxListedMatches = 50;

    /// <summary>The most suppliers a version excludes.</summary>
    public const int MaxExcludedSuppliers = 500;

    private static readonly DocumentStatus[] Approved = [DocumentStatus.Approved, DocumentStatus.AutoApproved];

    public static RouteGroupBuilder MapTypeIdentificationRules(this RouteGroupBuilder group)
    {
        group.MapGet("/document-types/{key}/identification-rules", ListAsync).RequireAuthorization(Policies.Read).WithTags("Identification rules").WithName("ListTypeIdentificationRules")
            .WithSummary("The tenant fallback identification rules of a type (docs/11 §8.5), each with its versions and their tests.");
        group.MapPost("/document-types/{key}/identification-rules", CreateAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Identification rules").WithName("CreateTypeIdentificationRule")
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithSummary("Creates a tenant fallback rule for the type: version 1 as a Draft, tested at once against the last 500 approved documents of the tenant.");
        group.MapPost("/documents/{id:guid}/identification-rules", CreateFromDocumentAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Identification rules").WithName("CreateTypeIdentificationRuleFromDocument")
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithSummary("\"Use for all suppliers without own layouts\" in review (docs/11 §12.10 step 5): saves the marked rule as a Draft tenant rule of the document's type (or the type named), tested at once; it must hold on the document.");
        group.MapGet("/identification-rules/{id:guid}", GetAsync).RequireAuthorization(Policies.Read).WithTags("Identification rules").WithName("GetTypeIdentificationRule")
            .WithSummary("One tenant fallback rule with its versions.");
        group.MapPatch("/identification-rules/{id:guid}", PatchAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Identification rules").WithName("PatchTypeIdentificationRule")
            .WithSummary("Renames a tenant fallback rule or changes its priority (which orders rules of the same type only).");
        group.MapGet("/identification-rules/{id:guid}/versions", ListVersionsAsync).RequireAuthorization(Policies.Read).WithTags("Identification rules").WithName("ListTypeIdentificationRuleVersions")
            .WithSummary("The versions of a tenant fallback rule.");
        group.MapPost("/identification-rules/{id:guid}/versions", CreateVersionAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Identification rules").WithName("CreateTypeIdentificationRuleVersion")
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithSummary("A new Draft version of the rule, tested at once; a rule has at most one Draft.");
        group.MapPut("/identification-rules/{id:guid}/versions/{version:int}", SaveDraftAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Identification rules").WithName("SaveTypeIdentificationRuleDraft")
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithSummary("Replaces a Draft's rule (If-Match: the revision read), tested again; a version out of Draft never changes.");
        group.MapPost("/identification-rules/{id:guid}/versions/{version:int}:test", TestAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Identification rules").WithName("TestTypeIdentificationRuleVersion")
            .WithSummary("Runs the save-time test again: the last 500 approved documents of the tenant, without excluded suppliers and suppliers that have layouts.");
        group.MapPost("/identification-rules/{id:guid}/versions/{version:int}:activate", ActivateAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Identification rules").WithName("ActivateTypeIdentificationRuleVersion")
            .WithSummary("Activates a Draft after its test (confirmOtherTypes when it matched documents of other types); the previous Active version is retired.");
        group.MapPost("/identification-rules/{id:guid}:disable", DisableAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Identification rules").WithName("DisableTypeIdentificationRule")
            .WithSummary("Disables a tenant fallback rule: it types no document from then on; documents it typed keep their version.");
        return group;
    }

    private static async Task<Results<Ok<IReadOnlyList<TypeIdentificationRuleResponse>>, ProblemHttpResult>> ListAsync(
        string key, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var rules = await db.TypeIdentificationRules.AsNoTracking().Include(r => r.Versions).Where(r => r.DocumentTypeKey == key).ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<TypeIdentificationRuleResponse>>([.. rules.OrderBy(r => r.Name, StringComparer.Ordinal).Select(Response)]);
    }

    private static async Task<Results<Ok<TypeIdentificationRuleResponse>, ProblemHttpResult>> GetAsync(
        Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var rule = await db.TypeIdentificationRules.AsNoTracking().Include(r => r.Versions).SingleOrDefaultAsync(r => r.Id == id, ct);
        return rule is null ? SupplierGroupEndpoints.NotFound("Identification rule", id) : TypedResults.Ok(Response(rule));
    }

    private static async Task<Results<Ok<IReadOnlyList<TypeIdentificationRuleVersionResponse>>, ProblemHttpResult>> ListVersionsAsync(
        Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var rule = await db.TypeIdentificationRules.AsNoTracking().Include(r => r.Versions).SingleOrDefaultAsync(r => r.Id == id, ct);
        return rule is null ? SupplierGroupEndpoints.NotFound("Identification rule", id) : TypedResults.Ok(Response(rule).Versions);
    }

    private static async Task<Results<Created<TypeIdentificationRuleResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        string key,
        CreateTypeIdentificationRuleRequest request,
        ITenantContext tenant,
        System.Security.Claims.ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IEffectiveConfigResolver configResolver,
        [FromServices] IContainerBlobStore blobs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var errors = new Errors().Require("name", request.Name, TypeIdentificationRule.MaxNameLength);
        await CheckTypeAsync(errors, "key", key, tenant.TenantId!.Value, configResolver, ct);
        CheckPriority(errors, request.Priority);
        var rule = CheckRule(errors, request.Rule);
        await CheckExcludedAsync(errors, db, request.ExcludedSupplierIds, ct);
        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var created = await AddRuleAsync(db, blobs, tenant.TenantId!.Value, DocumentTypeKey.Parse(key).Value, request.Name.Trim(), request.Priority, rule!, request.ExcludedSupplierIds, null, DocumentQueries.ActorName(user), clock.GetUtcNow(), ct);
        return TypedResults.Created($"/api/v1/identification-rules/{created.Id}", Response(created));
    }

    private static async Task<Results<Created<TypeIdentificationRuleResponse>, ValidationProblem, ProblemHttpResult>> CreateFromDocumentAsync(
        Guid id,
        CreateTypeIdentificationRuleFromDocumentRequest request,
        ITenantContext tenant,
        System.Security.Claims.ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IEffectiveConfigResolver configResolver,
        [FromServices] IContainerBlobStore blobs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var document = await db.Documents.AsNoTracking().Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return SupplierGroupEndpoints.NotFound("Document", id);
        }

        var errors = new Errors().Optional("name", request.Name, TypeIdentificationRule.MaxNameLength);
        var typeKey = request.TypeKey ?? (document.Type.IsUnknown ? null : document.Type.Value);
        if (typeKey is null)
        {
            errors.Add("typeKey", "The document has no type yet; name the type the rule identifies (typeKey), or set the document's type first.");
        }
        else
        {
            await CheckTypeAsync(errors, "typeKey", typeKey, tenant.TenantId!.Value, configResolver, ct);
        }

        CheckPriority(errors, request.Priority);
        var rule = CheckRule(errors, request.Rule);
        if (rule is not null)
        {
            // The rule is marked on this document: it must hold on it (docs/11 §12.9, "Holds for this document").
            var words = await LayoutEndpoints.WordsAsync(blobs, document, ct);
            var held = IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(words, SupplierValues.None, LayoutSettings.SourceOf(document)));
            if (!held.Holds)
            {
                errors.Add("rule", $"The rule does not hold on this document: {string.Join("; ", held.Failed.Take(3))}.");
            }
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var type = DocumentTypeKey.Parse(typeKey!).Value;
        var name = string.IsNullOrWhiteSpace(request.Name) ? $"{type}: {Shorten(rule!.Describe(), TypeIdentificationRule.MaxNameLength - type.Length - 2)}" : request.Name.Trim();
        var created = await AddRuleAsync(db, blobs, tenant.TenantId!.Value, type, name, request.Priority, rule!, null, document.Id, DocumentQueries.ActorName(user), clock.GetUtcNow(), ct);
        return TypedResults.Created($"/api/v1/identification-rules/{created.Id}", Response(created));
    }

    private static async Task<Results<Ok<TypeIdentificationRuleResponse>, ValidationProblem, ProblemHttpResult>> PatchAsync(
        Guid id,
        PatchTypeIdentificationRuleRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var rule = await db.TypeIdentificationRules.Include(r => r.Versions).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return SupplierGroupEndpoints.NotFound("Identification rule", id);
        }

        var errors = new Errors();
        if (request.Name is not null)
        {
            errors.Require("name", request.Name, TypeIdentificationRule.MaxNameLength);
        }

        CheckPriority(errors, request.Priority);
        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        rule.Name = request.Name?.Trim() ?? rule.Name;
        rule.Priority = request.Priority ?? rule.Priority;
        rule.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(Response(rule));
    }

    private static async Task<Results<Created<TypeIdentificationRuleVersionResponse>, ValidationProblem, ProblemHttpResult>> CreateVersionAsync(
        Guid id,
        TypeIdentificationRuleVersionRequest request,
        ITenantContext tenant,
        System.Security.Claims.ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var rule = await db.TypeIdentificationRules.Include(r => r.Versions).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return SupplierGroupEndpoints.NotFound("Identification rule", id);
        }

        if (rule.Versions.Any(v => v.State == TypeIdentificationRuleVersionState.Draft))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Draft exists", detail: $"Rule {id} already has a Draft version; edit it (PUT) or activate it first.");
        }

        var errors = new Errors();
        var parsed = CheckRule(errors, request.Rule);
        await CheckExcludedAsync(errors, db, request.ExcludedSupplierIds, ct);
        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        var version = NewVersion(rule, rule.Versions.Max(v => v.Version) + 1, parsed!, request.ExcludedSupplierIds, DocumentQueries.ActorName(user), now);
        version.TestJson = JsonSerializer.Serialize(await RunTestAsync(db, blobs, rule.TenantId, rule.DocumentTypeKey, parsed!, request.ExcludedSupplierIds ?? [], now, ct), ApiJson);
        rule.Versions.Add(version);
        db.TypeIdentificationRuleVersions.Add(version);
        rule.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/identification-rules/{rule.Id}/versions/{version.Version}", VersionResponse(version));
    }

    private static async Task<Results<Ok<TypeIdentificationRuleVersionResponse>, ValidationProblem, ProblemHttpResult>> SaveDraftAsync(
        Guid id,
        int version,
        TypeIdentificationRuleVersionRequest request,
        HttpContext http,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var row = await db.TypeIdentificationRuleVersions.Include(v => v.Rule).SingleOrDefaultAsync(v => v.RuleId == id && v.Version == version, ct);
        if (row?.Rule is not { } rule)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Identification rule {id} has no version {version}.");
        }

        if (row.State != TypeIdentificationRuleVersionState.Draft)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not a draft", detail: $"Version {version} is {row.State}; a version out of Draft never changes. Create a new version.");
        }

        if (!OutputMappingEndpoints.TryReadIfMatch(http, out var revision))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match required", detail: "Send the draft's revision (from GET .../versions) in If-Match, so a concurrent edit is not overwritten.");
        }

        if (revision != row.Revision)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Changed", detail: $"The draft is at revision {row.Revision}, not {revision}.");
        }

        var errors = new Errors();
        var parsed = CheckRule(errors, request.Rule);
        await CheckExcludedAsync(errors, db, request.ExcludedSupplierIds, ct);
        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        row.RuleJson = parsed!.ToJson();
        row.RuleHash = parsed.Hash;
        row.ExcludedSupplierIdsJson = JsonSerializer.Serialize(request.ExcludedSupplierIds ?? []);
        row.TestJson = JsonSerializer.Serialize(await RunTestAsync(db, blobs, rule.TenantId, rule.DocumentTypeKey, parsed, request.ExcludedSupplierIds ?? [], now, ct), ApiJson);
        row.Revision++;
        rule.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(VersionResponse(row));
    }

    private static async Task<Results<Ok<TypeIdentificationRuleVersionResponse>, ProblemHttpResult>> TestAsync(
        Guid id,
        int version,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var row = await db.TypeIdentificationRuleVersions.Include(v => v.Rule).SingleOrDefaultAsync(v => v.RuleId == id && v.Version == version, ct);
        if (row?.Rule is not { } rule)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Identification rule {id} has no version {version}.");
        }

        var parsed = IdentificationRule.Parse(row.RuleJson);
        row.TestJson = JsonSerializer.Serialize(await RunTestAsync(db, blobs, rule.TenantId, rule.DocumentTypeKey, parsed, TenantIdentificationJson.Excluded(row.ExcludedSupplierIdsJson), clock.GetUtcNow(), ct), ApiJson);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(VersionResponse(row));
    }

    private static async Task<Results<Ok<TypeIdentificationRuleResponse>, ProblemHttpResult>> ActivateAsync(
        Guid id,
        int version,
        ActivateTypeIdentificationRuleRequest? request,
        ITenantContext tenant,
        System.Security.Claims.ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var rule = await db.TypeIdentificationRules.Include(r => r.Versions).SingleOrDefaultAsync(r => r.Id == id, ct);
        var row = rule?.Versions.SingleOrDefault(v => v.Version == version);
        if (rule is null || row is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Identification rule {id} has no version {version}.");
        }

        if (row.State != TypeIdentificationRuleVersionState.Draft)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not a draft", detail: $"Version {version} is {row.State}; only a Draft is activated.");
        }

        var now = clock.GetUtcNow();
        var test = Report(row.TestJson);
        if (test is null || test.RuleHash != row.RuleHash)
        {
            test = await RunTestAsync(db, blobs, rule.TenantId, rule.DocumentTypeKey, IdentificationRule.Parse(row.RuleJson), TenantIdentificationJson.Excluded(row.ExcludedSupplierIdsJson), now, ct);
            row.TestJson = JsonSerializer.Serialize(test, ApiJson);
        }

        if (test.OtherTypes > 0 && request?.ConfirmOtherTypes != true)
        {
            await db.SaveChangesAsync(ct);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Matches other types",
                detail: $"The rule {test.Summary}. Add an identifier that tells them apart, or activate with confirmOtherTypes.",
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["test"] = test });
        }

        foreach (var active in rule.Versions.Where(v => v.State == TypeIdentificationRuleVersionState.Active))
        {
            active.State = TypeIdentificationRuleVersionState.Retired;
        }

        row.State = TypeIdentificationRuleVersionState.Active;
        row.ApprovedBy = DocumentQueries.ActorName(user);
        row.ApprovedAt = now;
        rule.ActiveVersion = row.Version;
        rule.Status = TypeIdentificationRuleStatus.Active;
        rule.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(Response(rule));
    }

    private static async Task<Results<Ok<TypeIdentificationRuleResponse>, ProblemHttpResult>> DisableAsync(
        Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identification rules", ct) is { } problem)
        {
            return problem;
        }

        var rule = await db.TypeIdentificationRules.Include(r => r.Versions).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return SupplierGroupEndpoints.NotFound("Identification rule", id);
        }

        rule.Status = TypeIdentificationRuleStatus.Disabled;
        rule.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(Response(rule));
    }

    // ------------------------------------------------------------------ the save-time test

    /// <summary>
    /// The test of docs/11 §8.5: the rule on the last <see cref="TestDocuments"/> approved documents of the tenant, without
    /// the excluded suppliers and the suppliers that have layouts of their own or of their group (they never reach the
    /// tenant rules), without master data (a tenant rule has none).
    /// </summary>
    internal static async Task<TenantRuleTestReport> RunTestAsync(DocProcDbContext db, IContainerBlobStore blobs, Guid tenantId, string typeKey, IdentificationRule rule, IReadOnlyCollection<Guid> excluded, DateTimeOffset now, CancellationToken ct)
    {
        var withOwnLayouts = await db.SupplierLayouts.AsNoTracking()
            .Where(l => l.Status == SupplierLayoutStatus.Active && l.Scope == SupplierLayoutScope.Supplier && l.SupplierId != null)
            .Select(l => l.SupplierId!.Value).Distinct().ToListAsync(ct);
        var groupsWithLayouts = await db.SupplierLayouts.AsNoTracking()
            .Where(l => l.Status == SupplierLayoutStatus.Active && l.Scope == SupplierLayoutScope.Group && l.GroupId != null)
            .Select(l => l.GroupId!.Value).Distinct().ToListAsync(ct);
        var groupMembers = await db.Suppliers.AsNoTracking().Where(s => s.GroupId != null && groupsWithLayouts.Contains(s.GroupId.Value)).Select(s => s.Id).ToListAsync(ct);
        var skip = new HashSet<Guid>([.. excluded, .. withOwnLayouts, .. groupMembers]);

        // Ordered in memory: SQLite cannot order a DateTimeOffset column (docs/09).
        var recent = await db.Documents.AsNoTracking()
            .Where(d => d.TenantId == tenantId && Approved.Contains(d.Status))
            .Select(d => new { d.Id, d.SupplierId, d.CreatedAt })
            .ToListAsync(ct);
        var ids = recent.Where(d => d.SupplierId is not { } s || !skip.Contains(s))
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Take(TestDocuments).Select(d => d.Id).ToList();
        var documents = await db.Documents.AsNoTracking().Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).Where(d => ids.Contains(d.Id)).ToListAsync(ct);

        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        var matches = new List<TenantRuleTestMatch>();
        foreach (var document in documents.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id))
        {
            var words = await LayoutEndpoints.WordsAsync(blobs, document, ct);
            if (!IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(words, SupplierValues.None, LayoutSettings.SourceOf(document))).Holds)
            {
                continue;
            }

            byType[document.Type.Value] = byType.GetValueOrDefault(document.Type.Value) + 1;
            matches.Add(new TenantRuleTestMatch(document.Id, document.Type.Value, document.SupplierId, document.IngestionItem?.FileName));
        }

        var same = byType.GetValueOrDefault(typeKey);
        var other = matches.Count - same;
        var summary = string.Create(CultureInfo.InvariantCulture, $"would have matched {same} {typeKey} document{(same == 1 ? string.Empty : "s")}, {other} other document{(other == 1 ? string.Empty : "s")} (of the last {documents.Count} approved)");
        return new TenantRuleTestReport(
            rule.Hash,
            typeKey,
            documents.Count,
            matches.Count,
            same,
            other,
            byType,
            [.. matches.Where(m => m.TypeKey != typeKey).Take(MaxListedMatches)],
            [.. matches.Take(MaxListedMatches)],
            summary,
            now);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    private static async Task<TypeIdentificationRule> AddRuleAsync(DocProcDbContext db, IContainerBlobStore blobs, Guid tenantId, string typeKey, string name, int? priority, IdentificationRule parsed, IReadOnlyList<Guid>? excluded, Guid? fromDocument, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var rule = new TypeIdentificationRule
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            DocumentTypeKey = typeKey,
            Name = name,
            Priority = priority ?? TypeIdentificationRule.DefaultPriority,
            CreatedFromDocumentId = fromDocument,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var version = NewVersion(rule, 1, parsed, excluded, actor, now);
        version.TestJson = JsonSerializer.Serialize(await RunTestAsync(db, blobs, tenantId, typeKey, parsed, excluded ?? [], now, ct), ApiJson);
        rule.Versions.Add(version);
        db.TypeIdentificationRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    private static TypeIdentificationRuleVersion NewVersion(TypeIdentificationRule rule, int number, IdentificationRule parsed, IReadOnlyList<Guid>? excluded, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = rule.TenantId,
        RuleId = rule.Id,
        Version = number,
        State = TypeIdentificationRuleVersionState.Draft,
        RuleJson = parsed.ToJson(),
        RuleHash = parsed.Hash,
        ExcludedSupplierIdsJson = JsonSerializer.Serialize(excluded ?? []),
        CreatedBy = actor,
        CreatedAt = now,
    };

    private static async Task CheckTypeAsync(Errors errors, string field, string key, Guid tenantId, IEffectiveConfigResolver configResolver, CancellationToken ct)
    {
        if (!DocumentTypeKey.TryParse(key, out var type) || type.IsUnknown)
        {
            errors.Add(field, $"'{key}' is not a type key.");
            return;
        }

        if ((await configResolver.ResolveAsync(new EffectiveConfigRequest(tenantId, type), ct)).Type is null)
        {
            errors.Add(field, $"The tenant has no published type '{key}'.");
        }
    }

    private static void CheckPriority(Errors errors, int? priority)
    {
        if (priority is < 0 or > 1000)
        {
            errors.Add("priority", "priority is 0 to 1000.");
        }
    }

    private static IdentificationRule? CheckRule(Errors errors, JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object)
        {
            errors.Add("rule", "rule is an identification rule object (all, any, none).");
            return null;
        }

        var parsed = IdentificationRuleChecks.Check(rule.GetRawText(), IdentificationScope.Tenant, out var problems);
        foreach (var group in problems.GroupBy(p => p.Path))
        {
            errors.Add($"rule.{group.Key}", string.Join(" ", group.Select(p => p.Message).Distinct()));
        }

        return problems.Count == 0 ? parsed : null;
    }

    private static async Task CheckExcludedAsync(Errors errors, DocProcDbContext db, IReadOnlyList<Guid>? excluded, CancellationToken ct)
    {
        if (excluded is not { Count: > 0 })
        {
            return;
        }

        if (excluded.Count > MaxExcludedSuppliers)
        {
            errors.Add("excludedSupplierIds", $"At most {MaxExcludedSuppliers} suppliers.");
            return;
        }

        var known = await db.Suppliers.AsNoTracking().Where(s => excluded.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
        foreach (var missing in excluded.Except(known).Take(5))
        {
            errors.Add("excludedSupplierIds", $"No supplier {missing} in this tenant.");
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";

    private static TenantRuleTestReport? Report(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TenantRuleTestReport>(json, ApiJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TypeIdentificationRuleResponse Response(TypeIdentificationRule rule) => new(
        rule.Id,
        rule.DocumentTypeKey,
        rule.Name,
        rule.Priority,
        rule.ActiveVersion,
        rule.Status.ToString(),
        rule.CreatedFromDocumentId,
        rule.Matched,
        rule.Overridden,
        rule.CreatedBy,
        rule.CreatedAt,
        rule.UpdatedAt,
        [.. rule.Versions.OrderBy(v => v.Version).Select(VersionResponse)]);

    private static TypeIdentificationRuleVersionResponse VersionResponse(TypeIdentificationRuleVersion version)
    {
        using var document = JsonDocument.Parse(version.RuleJson);
        var describe = IdentificationRule.TryParse(version.RuleJson, out _) is { } parsed ? parsed.Describe() : string.Empty;
        return new TypeIdentificationRuleVersionResponse(
            version.Id,
            version.Version,
            version.State.ToString(),
            document.RootElement.Clone(),
            describe,
            version.RuleHash,
            [.. TenantIdentificationJson.Excluded(version.ExcludedSupplierIdsJson)],
            Report(version.TestJson),
            version.Revision,
            version.ApprovedBy,
            version.ApprovedAt,
            version.CreatedBy,
            version.CreatedAt);
    }
}
