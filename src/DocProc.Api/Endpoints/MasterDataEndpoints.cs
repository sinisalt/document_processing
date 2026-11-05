using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Outputs;
using DocProc.Infrastructure.MasterData;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>
/// <c>POST /suppliers:import</c> (docs/11 §14): supplier records as CSV with a column map, or as JSON, matched by
/// <c>matchKeys</c> (default <c>attributes.erpVendorId</c>, <c>vatId</c>, <c>iban</c>) under the per-attribute
/// <c>conflictPolicy</c>; <c>dryRun</c> (the default) answers the report without changing anything.
/// </summary>
public sealed record SupplierImportRequest(
    string? Format,
    string? Content,
    JsonElement? Records,
    IReadOnlyDictionary<string, string>? Columns,
    IReadOnlyList<string>? MatchKeys,
    IReadOnlyDictionary<string, string>? ConflictPolicy,
    bool? DryRun);

/// <summary>A master-data source as the API shows it (docs/11 §6.8).</summary>
public sealed record MasterDataSourceDto(
    Guid Id,
    string Name,
    string Target,
    string Transport,
    Guid? DestinationId,
    string? Mapping,
    IReadOnlyList<string> MatchKeys,
    JsonElement? ConflictPolicy,
    string? Schedule,
    bool Enabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    JsonElement? LastResult,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static MasterDataSourceDto From(MasterDataSource s) => new(
        s.Id, s.Name, s.Target, s.Transport, s.DestinationId, s.MappingExpression, s.MatchKeys, Element(s.ConflictPolicyJson), s.ScheduleCron, s.Enabled, s.NextRunAt, s.LastRunAt, Element(s.LastResultJson), s.CreatedAt, s.UpdatedAt);

    internal static JsonElement? Element(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

/// <summary>Create or change a master-data source; on PATCH a property left out keeps its value.</summary>
public sealed record MasterDataSourceRequest(
    string? Name,
    string? Target,
    string? Transport,
    Guid? DestinationId,
    string? Mapping,
    IReadOnlyList<string>? MatchKeys,
    IReadOnlyDictionary<string, string>? ConflictPolicy,
    string? Schedule,
    bool? Enabled);

/// <summary><c>:run</c> and <c>:dry-run</c>: the records of an upload source (JSON, before the source's mapping).</summary>
public sealed record MasterDataRunRequest(JsonElement? Records);

/// <summary>One run with its report.</summary>
public sealed record MasterDataRunDto(
    Guid Id,
    Guid? SourceId,
    MasterDataTrigger Trigger,
    bool DryRun,
    MasterDataRunStatus Status,
    int Created,
    int Updated,
    int Unchanged,
    int Conflicts,
    int Errors,
    JsonElement Report,
    DateTimeOffset At,
    string By)
{
    internal static MasterDataRunDto From(MasterDataRun r) =>
        new(r.Id, r.SourceId, r.Trigger, r.DryRun, r.Status, r.Created, r.Updated, r.Unchanged, r.Conflicts, r.Errors, MasterDataSourceDto.Element(r.ReportJson) ?? default, r.At, r.By);
}

/// <summary>
/// Master data from the systems the tenant plugs in (docs/11 §6.8, §14): the supplier import, the master-data sources
/// (an http destination read on a schedule, or uploads) with their runs. Every write is a <c>TenantAdmin</c>'s, as the
/// supplier list is; a source's http destination is read through the delivery client (public addresses only).
/// </summary>
public static class MasterDataEndpoints
{
    private const string What = "Master data";

    public const int MaxSourcesPerTenant = 25;

    /// <summary>The shortest time a schedule may leave between two runs.</summary>
    public static readonly TimeSpan MinScheduleGap = TimeSpan.FromMinutes(15);

    /// <summary>The request limit of an import: a 2 MB file and its wrapper.</summary>
    public const long MaxImportBytes = (2 * 1024 * 1024) + (64 * 1024);

    public static RouteGroupBuilder MapMasterData(this RouteGroupBuilder group)
    {
        group.MapPost("/suppliers:import", ImportAsync)
            .WithTags("Suppliers")
            .RequireAuthorization(Policies.TenantAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(MaxImportBytes))
            .WithName("ImportSuppliers")
            .WithSummary("Imports suppliers from CSV (with a column map) or JSON (docs/11 §14): matched by attributes.erpVendorId, vatId, then IBAN, each attribute under its conflict policy (externalWins, localWins, flag). A dry run (the default) answers the report only; the same file twice changes nothing.");

        var sources = group.MapGroup("/master-data-sources").WithTags("Suppliers");
        sources.MapGet("/", ListAsync).RequireAuthorization(Policies.TenantAdmin).WithName("ListMasterDataSources").WithSummary("The tenant's master-data sources.");
        sources.MapPost("/", CreateAsync).RequireAuthorization(Policies.TenantAdmin).WithMetadata(new RequestSizeLimitAttribute(64 * 1024)).WithName("CreateMasterDataSource")
            .WithSummary("Adds a master-data source: an http destination read on a schedule (CRON_TZ=<zone> m h dom mon dow) or an upload source, its JSONata mapping to supplier records, match keys and conflict policy.");
        sources.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Policies.TenantAdmin).WithName("GetMasterDataSource").WithSummary("One master-data source.");
        sources.MapPatch("/{id:guid}", PatchAsync).RequireAuthorization(Policies.TenantAdmin).WithMetadata(new RequestSizeLimitAttribute(64 * 1024)).WithName("UpdateMasterDataSource").WithSummary("Changes a master-data source.");
        sources.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(Policies.TenantAdmin).WithName("DeleteMasterDataSource").WithSummary("Removes a master-data source and its runs.");
        sources.MapPost("/{id:guid}:run", RunAsync).RequireAuthorization(Policies.TenantAdmin).WithMetadata(new RequestSizeLimitAttribute(MaxImportBytes)).WithName("RunMasterDataSource")
            .WithSummary("Runs the source now and applies its records (an upload source takes them in the body); answers the run with its report.");
        sources.MapPost("/{id:guid}:dry-run", DryRunAsync).RequireAuthorization(Policies.TenantAdmin).WithMetadata(new RequestSizeLimitAttribute(MaxImportBytes)).WithName("DryRunMasterDataSource")
            .WithSummary("Runs the source without changing anything: the report says what a run would create, update and flag.");
        sources.MapGet("/{id:guid}/runs", RunsAsync).RequireAuthorization(Policies.TenantAdmin).WithName("ListMasterDataRuns").WithSummary("The source's runs, newest first (at most 50).");
        group.MapGet("/master-data-runs/{id:guid}", GetRunAsync).WithTags("Suppliers").RequireAuthorization(Policies.TenantAdmin).WithName("GetMasterDataRun").WithSummary("One run with its report.");
        return group;
    }

    private static async Task<Results<Ok<MasterDataRunDto>, ValidationProblem, ProblemHttpResult>> ImportAsync(
        SupplierImportRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var errors = new Errors();
        foreach (var key in request.MatchKeys ?? [])
        {
            if (!MatchKeys.IsValid(key))
            {
                errors.Add("matchKeys", $"'{key}' is not a match key (attributes.<key>, vatId, iban).");
            }
        }

        CheckPolicies(errors, "conflictPolicy", request.ConflictPolicy);
        IReadOnlyList<SupplierRecord> records = [];
        var format = request.Format?.Trim().ToLowerInvariant() ?? (request.Content is not null ? "csv" : "json");
        if (format == "csv")
        {
            if (string.IsNullOrEmpty(request.Content))
            {
                errors.Add("content", "content holds the CSV text.");
            }
            else if (request.Columns is not { Count: > 0 } columns)
            {
                errors.Add("columns", "columns maps name, vatId, taxId, ibans, currency and attributes.<key> to the file's header names.");
            }
            else
            {
                foreach (var target in columns.Keys.Where(k => k is not ("name" or "vatId" or "taxId" or "ibans" or "currency") && !k.StartsWith("attributes.", StringComparison.Ordinal)))
                {
                    errors.Add("columns", $"'{target}' is not a supplier attribute an import writes.");
                }

                if (errors.Count == 0)
                {
                    try
                    {
                        var (header, rows) = CsvTable.Parse(request.Content);
                        records = SupplierImporter.FromCsv(header, rows, columns, out var csvProblem);
                        if (csvProblem is not null)
                        {
                            errors.Add("columns", csvProblem);
                        }
                    }
                    catch (CsvFormatException e)
                    {
                        errors.Add("content", e.Message);
                    }
                }
            }
        }
        else if (format == "json")
        {
            if (request.Records is not { ValueKind: JsonValueKind.Array } array)
            {
                errors.Add("records", "records is a JSON array of {name, vatId, taxId, ibans, currency, attributes}.");
            }
            else
            {
                records = SupplierImporter.FromJson(JsonNode.Parse(array.GetRawText()), out var jsonProblem);
                if (jsonProblem is not null)
                {
                    errors.Add("records", jsonProblem);
                }
            }
        }
        else
        {
            errors.Add("format", "format is csv or json.");
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var dryRun = request.DryRun ?? true;
        var now = clock.GetUtcNow();
        var report = await SupplierImporter.ImportAsync(db, tenant.TenantId!.Value, records, request.MatchKeys, request.ConflictPolicy, dryRun, now, ct);
        var run = MasterDataSync.Record(db, tenant.TenantId!.Value, null, MasterDataTrigger.Import, dryRun, report, now, DocumentQueries.ActorName(user));
        await db.SaveChangesAsync(ct);
        await MasterDataSync.PruneAsync(db, tenant.TenantId!.Value, null, ct);
        return TypedResults.Ok(MasterDataRunDto.From(run));
    }

    private static async Task<Results<Ok<IReadOnlyList<MasterDataSourceDto>>, ProblemHttpResult>> ListAsync(ITenantContext tenant, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var rows = await db.MasterDataSources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<MasterDataSourceDto>>([.. rows.Select(MasterDataSourceDto.From)]);
    }

    private static async Task<Results<Ok<MasterDataSourceDto>, ProblemHttpResult>> GetAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var row = await db.MasterDataSources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        return row is null ? NotFound(id) : TypedResults.Ok(MasterDataSourceDto.From(row));
    }

    private static async Task<Results<Created<MasterDataSourceDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        MasterDataSourceRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] IOutputRenderer renderer,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        if (await db.MasterDataSources.CountAsync(ct) >= MaxSourcesPerTenant)
        {
            return Conflict("Too many sources", $"A tenant has at most {MaxSourcesPerTenant} master-data sources.");
        }

        var now = clock.GetUtcNow();
        var source = new MasterDataSource { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId!.Value, Name = string.Empty, CreatedAt = now, CreatedBy = DocumentQueries.ActorName(user), UpdatedAt = now };
        var errors = new Errors().Require("name", request.Name, MasterDataSource.MaxNameLength);
        if (await ApplyAsync(db, renderer, source, request, errors, ct) is { } unavailable)
        {
            return unavailable;
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        if (await db.MasterDataSources.AnyAsync(s => s.Name == source.Name, ct))
        {
            return Conflict("Name in use", $"A master-data source '{source.Name}' exists already.");
        }

        source.NextRunAt = MasterDataSync.NextRun(source, now);
        db.MasterDataSources.Add(source);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/master-data-sources/{source.Id}", MasterDataSourceDto.From(source));
    }

    private static async Task<Results<Ok<MasterDataSourceDto>, ValidationProblem, ProblemHttpResult>> PatchAsync(
        Guid id,
        MasterDataSourceRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] IOutputRenderer renderer,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var source = await db.MasterDataSources.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (source is null)
        {
            return NotFound(id);
        }

        var errors = new Errors().Optional("name", request.Name, MasterDataSource.MaxNameLength);
        if (await ApplyAsync(db, renderer, source, request, errors, ct) is { } unavailable)
        {
            return unavailable;
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        source.UpdatedAt = now;
        source.NextRunAt = MasterDataSync.NextRun(source, now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Source changed", "The source changed (a scheduled run started) while this request ran; reload it and try again.");
        }
        catch (DbUpdateException)
        {
            return Conflict("Name in use", $"A master-data source '{source.Name}' exists already.");
        }

        return TypedResults.Ok(MasterDataSourceDto.From(source));
    }

    /// <summary>The request's settings onto <paramref name="source"/>, checked; a 503 when the renderer cannot check the mapping.</summary>
    private static async Task<ProblemHttpResult?> ApplyAsync(DocProcDbContext db, IOutputRenderer renderer, MasterDataSource source, MasterDataSourceRequest request, Errors errors, CancellationToken ct)
    {
        if (request.Name is { } name)
        {
            source.Name = name.Trim();
        }

        if (request.Target is { } target)
        {
            if (!MasterDataTargets.All.Contains(target, StringComparer.Ordinal))
            {
                errors.Add("target", $"target is {string.Join(", ", MasterDataTargets.All)} (lookup tables and receiving entities come later).");
            }

            source.Target = target;
        }

        if (request.Transport is { } transport)
        {
            if (!MasterDataTransports.All.Contains(transport, StringComparer.Ordinal))
            {
                errors.Add("transport", $"transport is {string.Join(" or ", MasterDataTransports.All)}.");
            }

            source.Transport = transport;
        }

        if (request.DestinationId is { } destinationId)
        {
            source.DestinationId = destinationId;
        }

        if (source.Transport == MasterDataTransports.Http)
        {
            var destination = source.DestinationId is { } did ? await db.OutputDestinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == did, ct) : null;
            if (destination is null || destination.Transport != OutputTransports.Http)
            {
                errors.Add("destinationId", "An http source reads an http destination of the tenant (its urlTemplate, headers and authentication).");
            }
        }
        else
        {
            source.DestinationId = null;
        }

        if (request.Mapping is { } mapping)
        {
            source.MappingExpression = string.IsNullOrWhiteSpace(mapping) ? null : mapping;
            if (mapping.Length > MasterDataSource.MaxMappingLength)
            {
                errors.Add("mapping", $"mapping is at most {MasterDataSource.MaxMappingLength} characters.");
            }
            else if (source.MappingExpression is { } expression)
            {
                var check = await renderer.CheckAsync(expression, null, ct);
                if (check.Error is { Kind: RenderErrorKinds.Unavailable } unavailable)
                {
                    return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Renderer unavailable", detail: unavailable.Message);
                }

                if (check.Error is { } error)
                {
                    errors.Add("mapping", $"mapping is not a JSONata expression ({error.Kind}): {error.Message}");
                }
            }
        }

        if (request.MatchKeys is { } keys)
        {
            if (keys.Count > 5 || keys.Any(k => !MatchKeys.IsValid(k)))
            {
                errors.Add("matchKeys", "matchKeys are at most 5 of attributes.<key>, vatId and iban.");
            }

            source.MatchKeys = [.. keys];
        }

        if (request.ConflictPolicy is { } policies)
        {
            CheckPolicies(errors, "conflictPolicy", policies);
            source.ConflictPolicyJson = policies.Count == 0 ? null : JsonSerializer.Serialize(policies);
        }

        if (request.Schedule is { } schedule)
        {
            source.ScheduleCron = string.IsNullOrWhiteSpace(schedule) ? null : schedule.Trim();
            if (source.ScheduleCron is { } cron)
            {
                string? cronError = null;
                if (cron.Length > MasterDataSource.MaxCronLength || !CronSchedule.TryParse(cron, out var parsed, out cronError))
                {
                    errors.Add("schedule", $"schedule is a cron expression (CRON_TZ=<zone> minute hour day month weekday): {cronError ?? "too long"}.");
                }
                else if (parsed!.ShortestGap() < MinScheduleGap)
                {
                    // Every run reads up to 2 MB from the tenant's system and rewrites suppliers: not every minute.
                    errors.Add("schedule", $"schedule runs at most every {MinScheduleGap.TotalMinutes:0} minutes.");
                }
                else if (source.Transport != MasterDataTransports.Http)
                {
                    errors.Add("schedule", "Only an http source runs on a schedule; an upload source runs with its records.");
                }
            }
        }

        if (request.Enabled is { } enabled)
        {
            source.Enabled = enabled;
        }

        return null;
    }

    private static void CheckPolicies(Errors errors, string field, IReadOnlyDictionary<string, string>? policies)
    {
        foreach (var (attribute, policy) in policies ?? new Dictionary<string, string>())
        {
            if (!ConflictPolicies.All.Contains(policy, StringComparer.Ordinal))
            {
                errors.Add(field, $"'{policy}' (for {attribute}) is not one of {string.Join(", ", ConflictPolicies.All)}.");
            }
            else if (attribute is not ("*" or "name" or "vatId" or "taxId" or "ibans" or "currency" or "attributes") && !attribute.StartsWith("attributes.", StringComparison.Ordinal))
            {
                errors.Add(field, $"'{attribute}' is not an attribute a sync writes (name, vatId, taxId, ibans, currency, attributes, attributes.<key>, *).");
            }
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var source = await db.MasterDataSources.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (source is null)
        {
            return NotFound(id);
        }

        db.MasterDataRuns.RemoveRange(await db.MasterDataRuns.Where(r => r.SourceId == id).ToListAsync(ct));
        db.MasterDataSources.Remove(source);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static Task<Results<Ok<MasterDataRunDto>, ValidationProblem, ProblemHttpResult>> RunAsync(Guid id, MasterDataRunRequest? request, ITenantContext tenant, ClaimsPrincipal user, [FromServices] DocProcDbContext db, [FromServices] MasterDataSync sync, CancellationToken ct) =>
        RunCoreAsync(id, request, dryRun: false, tenant, user, db, sync, ct);

    private static Task<Results<Ok<MasterDataRunDto>, ValidationProblem, ProblemHttpResult>> DryRunAsync(Guid id, MasterDataRunRequest? request, ITenantContext tenant, ClaimsPrincipal user, [FromServices] DocProcDbContext db, [FromServices] MasterDataSync sync, CancellationToken ct) =>
        RunCoreAsync(id, request, dryRun: true, tenant, user, db, sync, ct);

    private static async Task<Results<Ok<MasterDataRunDto>, ValidationProblem, ProblemHttpResult>> RunCoreAsync(Guid id, MasterDataRunRequest? request, bool dryRun, ITenantContext tenant, ClaimsPrincipal user, DocProcDbContext db, MasterDataSync sync, CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var source = await db.MasterDataSources.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (source is null)
        {
            return NotFound(id);
        }

        JsonNode? uploaded = null;
        if (request?.Records is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } records)
        {
            uploaded = JsonNode.Parse(records.GetRawText());
        }
        else if (source.Transport == MasterDataTransports.Upload)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["records"] = ["An upload source runs with its records in the body."] });
        }

        try
        {
            var run = await sync.RunAsync(db, source, MasterDataTrigger.Manual, dryRun, uploaded, DocumentQueries.ActorName(user), ct);
            return TypedResults.Ok(MasterDataRunDto.From(run));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Source busy", "The source changed (a scheduled run) while this one ran; nothing was applied. Run it again.");
        }
    }

    private static async Task<Results<Ok<IReadOnlyList<MasterDataRunDto>>, ProblemHttpResult>> RunsAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        if (!await db.MasterDataSources.AnyAsync(s => s.Id == id, ct))
        {
            return NotFound(id);
        }

        var runs = await db.MasterDataRuns.AsNoTracking().Where(r => r.SourceId == id).OrderByDescending(r => r.At).Take(50).ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<MasterDataRunDto>>([.. runs.Select(MasterDataRunDto.From)]);
    }

    private static async Task<Results<Ok<MasterDataRunDto>, ProblemHttpResult>> GetRunAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var run = await db.MasterDataRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        return run is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Run not found", detail: $"The tenant has no master-data run {id}.")
            : TypedResults.Ok(MasterDataRunDto.From(run));
    }

    private static ProblemHttpResult NotFound(Guid id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Source not found", detail: $"The tenant has no master-data source {id}.");
}
