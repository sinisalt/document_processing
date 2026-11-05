using System.Text.Json;
using System.Text.RegularExpressions;
using DocProc.Api.Auth;
using DocProc.Application.Configuration;
using DocProc.Domain.Validation;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>A lookup table without its entries: the tenant's (<c>Platform</c> false) or a platform code list.</summary>
public sealed record LookupTableSummary(string Key, string? Description, bool CaseSensitive, string Source, bool Platform, int EntryCount, int Revision, DateTimeOffset UpdatedAt);

/// <summary>One entry: from, to, valid from and to (inclusive, <c>yyyy-MM-dd</c>), labels by locale.</summary>
public sealed record LookupEntryDto(string From, string To, DateOnly? ValidFrom, DateOnly? ValidTo, IReadOnlyDictionary<string, string>? Labels);

/// <summary>A lookup table with its entries; the ETag is the revision.</summary>
public sealed record LookupTableDetail(LookupTableSummary Table, IReadOnlyList<LookupEntryDto> Entries);

public sealed record CreateLookupTableRequest(string Key, string? Description, bool? CaseSensitive);

public sealed record PatchLookupTableRequest(string? Description, bool? CaseSensitive);

/// <summary><c>PUT /lookup-tables/{key}/entries</c>: every entry of the table (replacing the stored ones), If-Match: the revision.</summary>
public sealed record PutLookupEntriesRequest(IReadOnlyList<LookupEntryDto> Entries);

/// <summary>
/// Lookup tables (docs/11 §6.1, §18 "Lookups"): codes from one system to another, which output mappings read with
/// <c>$table(key, value, default?)</c>, honouring each entry's validity. The tenant's tables and the platform code lists
/// (TenantId NULL, read-only; the <c>uom</c> list of the coded rules is built in code and shows here with
/// <c>Platform</c> true when no tenant table of that key exists). Reading needs <see cref="Policies.Read"/>, changing
/// <see cref="Policies.ConfigAdmin"/>; both need the <c>outputs</c> or the <c>configurableTypes</c> switch. CSV upload
/// comes later; the entries are sent as JSON.
/// </summary>
public static partial class LookupTableEndpoints
{
    private const string What = "Lookup tables";

    public const int MaxEntries = 50_000;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex KeyPattern();

    public static RouteGroupBuilder MapLookupTables(this RouteGroupBuilder group)
    {
        var tables = group.MapGroup("/lookup-tables").WithTags("Outputs");
        tables.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("ListLookupTables")
            .WithSummary("The tenant's lookup tables and the platform code lists.");
        tables.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.ConfigAdmin)
            .WithName("CreateLookupTable")
            .WithSummary("Adds an empty lookup table to the tenant.");
        tables.MapGet("/{key}", GetAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetLookupTable")
            .WithSummary("One lookup table with its entries (the tenant's, else the platform's of that key); the ETag is its revision.");
        tables.MapPatch("/{key}", PatchAsync)
            .RequireAuthorization(Policies.ConfigAdmin)
            .WithName("UpdateLookupTable")
            .WithSummary("Changes a table's description or case sensitivity.");
        tables.MapPut("/{key}/entries", PutEntriesAsync)
            .RequireAuthorization(Policies.ConfigAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(16L * 1024 * 1024))
            .WithName("PutLookupEntries")
            .WithSummary("Replaces every entry of one of the tenant's tables (If-Match: the revision read).");
        tables.MapDelete("/{key}", DeleteAsync)
            .RequireAuthorization(Policies.ConfigAdmin)
            .WithName("DeleteLookupTable")
            .WithSummary("Removes one of the tenant's lookup tables with its entries.");
        return group;
    }

    private static async Task<ProblemHttpResult?> GuardAsync(ITenantContext tenant, ITenantFeatures features, CancellationToken ct)
    {
        if (RequireTenant(tenant, What) is { } problem)
        {
            return problem;
        }

        var set = await features.GetAsync(tenant.TenantId!.Value, ct);
        return set.IsEnabled(TenantFeature.Outputs) || set.IsEnabled(TenantFeature.ConfigurableTypes)
            ? null
            : TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Feature not enabled", detail: "Lookup tables need the 'outputs' or the 'configurableTypes' feature; a tenant admin switches it on in the settings.");
    }

    private static async Task<Results<Ok<IReadOnlyList<LookupTableSummary>>, ProblemHttpResult>> ListAsync(ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        var rows = await db.LookupTables.AsNoTracking()
            .Select(t => new { Table = t, Count = t.Entries.Count })
            .ToListAsync(ct);
        var result = rows.OrderBy(r => r.Table.TenantId is null ? 1 : 0).ThenBy(r => r.Table.Key, StringComparer.Ordinal)
            .Select(r => Summary(r.Table, r.Count)).ToList();
        foreach (var (key, list) in CodeLists.Platform)
        {
            if (result.All(r => r.Key != key))
            {
                result.Add(new LookupTableSummary(key, "Platform code list (UN/ECE Recommendation 20 units with their names and printed aliases).", false, nameof(LookupTableSource.Manual), true, list.Entries.Count(), 0, DateTimeOffset.UnixEpoch));
            }
        }

        return TypedResults.Ok<IReadOnlyList<LookupTableSummary>>(result);
    }

    private static async Task<Results<Ok<LookupTableDetail>, ProblemHttpResult>> GetAsync(string key, ITenantContext tenant, HttpContext http, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        var table = (await db.LookupTables.AsNoTracking().Where(t => t.Key == key).ToListAsync(ct)).OrderBy(t => t.TenantId is null ? 1 : 0).FirstOrDefault();
        if (table is null)
        {
            if (CodeLists.Platform.TryGetValue(key, out var list))
            {
                IReadOnlyList<LookupEntryDto> codes = [.. list.Entries.Order(StringComparer.Ordinal).Select(e => new LookupEntryDto(e, list.Resolve(e)!, null, null, null))];
                return TypedResults.Ok(new LookupTableDetail(new LookupTableSummary(key, null, false, nameof(LookupTableSource.Manual), true, codes.Count, 0, DateTimeOffset.UnixEpoch), codes));
            }

            return NotFound(key);
        }

        var entries = await db.LookupEntries.AsNoTracking().Where(e => e.TableId == table.Id).OrderBy(e => e.From).ThenBy(e => e.ValidFrom).ToListAsync(ct);
        http.Response.Headers.ETag = OutputMappingEndpoints.ETagOf(table.Revision);
        return TypedResults.Ok(new LookupTableDetail(Summary(table, entries.Count), [.. entries.Select(Dto)]));
    }

    private static async Task<Results<Created<LookupTableSummary>, ValidationProblem, ProblemHttpResult>> CreateAsync(CreateLookupTableRequest request, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        var errors = new Errors().Optional("description", request.Description, 2000);
        if (request.Key is null || !KeyPattern().IsMatch(request.Key))
        {
            errors.Add("key", "key is a letter, then letters, digits, '_', '.' or '-', at most 64 characters.");
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        if (await db.LookupTables.AnyAsync(t => t.TenantId == tenant.TenantId && t.Key == request.Key, ct))
        {
            return Conflict("Lookup table exists", $"The tenant already has a lookup table '{request.Key}'.");
        }

        var now = clock.GetUtcNow();
        var table = new LookupTable
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.TenantId,
            Key = request.Key!,
            Description = TrimToNull(request.Description),
            CaseSensitive = request.CaseSensitive ?? false,
            Source = LookupTableSource.Manual,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.LookupTables.Add(table);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/lookup-tables/{Uri.EscapeDataString(table.Key)}", Summary(table, 0));
    }

    private static async Task<Results<Ok<LookupTableSummary>, ValidationProblem, ProblemHttpResult>> PatchAsync(string key, PatchLookupTableRequest request, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        if (new Errors().Optional("description", request.Description, 2000).ToProblem() is { } invalid)
        {
            return invalid;
        }

        var table = await db.LookupTables.SingleOrDefaultAsync(t => t.TenantId == tenant.TenantId && t.Key == key, ct);
        if (table is null)
        {
            return NotFound(key);
        }

        table.Description = request.Description is null ? table.Description : TrimToNull(request.Description);
        table.CaseSensitive = request.CaseSensitive ?? table.CaseSensitive;
        table.Revision++;
        table.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(Summary(table, await db.LookupEntries.CountAsync(e => e.TableId == table.Id, ct)));
    }

    private static async Task<Results<Ok<LookupTableSummary>, ValidationProblem, ProblemHttpResult>> PutEntriesAsync(string key, PutLookupEntriesRequest request, ITenantContext tenant, HttpContext http, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        if (!OutputMappingEndpoints.TryReadIfMatch(http, out var revision))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match required", detail: "Send the table's ETag (from GET /lookup-tables/{key}) in If-Match.");
        }

        var errors = new Errors();
        var entries = request.Entries ?? [];
        if (entries.Count > MaxEntries)
        {
            errors.Add("entries", $"At most {MaxEntries} entries.");
        }

        for (var i = 0; i < entries.Count && errors.Count == 0; i++)
        {
            var e = entries[i];
            if (string.IsNullOrWhiteSpace(e.From) || e.From.Trim().Length > 200)
            {
                errors.Add($"entries[{i}].from", "from is required and at most 200 characters.");
            }
            else if (e.To is null || e.To.Length > 400)
            {
                errors.Add($"entries[{i}].to", "to is required and at most 400 characters.");
            }
            else if (e.ValidFrom is { } from && e.ValidTo is { } to && to < from)
            {
                errors.Add($"entries[{i}].validTo", "validTo is on or after validFrom.");
            }
            else if (e.Labels is { Count: > 20 } || e.Labels?.Any(l => l.Key.Length > 16 || l.Value.Length > 200) == true)
            {
                errors.Add($"entries[{i}].labels", "At most 20 labels, a locale of at most 16 and a label of at most 200 characters.");
            }
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var table = await db.LookupTables.SingleOrDefaultAsync(t => t.TenantId == tenant.TenantId && t.Key == key, ct);
        if (table is null)
        {
            return NotFound(key);
        }

        if (table.Revision != revision)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Lookup table changed", detail: $"The table is at revision {table.Revision}; read it again.");
        }

        // Removed through the change tracker, so the old entries go in the same save as the new ones and the revision.
        db.LookupEntries.RemoveRange(await db.LookupEntries.Where(e => e.TableId == table.Id).ToListAsync(ct));
        foreach (var e in entries)
        {
            db.LookupEntries.Add(new LookupEntry
            {
                TableId = table.Id,
                TenantId = table.TenantId,
                From = e.From.Trim(),
                To = e.To,
                ValidFrom = e.ValidFrom,
                ValidTo = e.ValidTo,
                LabelsJson = e.Labels is { Count: > 0 } labels ? JsonSerializer.Serialize(labels) : null,
            });
        }

        table.Revision++;
        table.Source = LookupTableSource.Manual;
        table.UpdatedAt = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Lookup table changed", detail: "The table changed while it was saved; read it again.");
        }

        http.Response.Headers.ETag = OutputMappingEndpoints.ETagOf(table.Revision);
        return TypedResults.Ok(Summary(table, entries.Count));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(string key, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await GuardAsync(tenant, features, ct) is { } problem)
        {
            return problem;
        }

        var table = await db.LookupTables.SingleOrDefaultAsync(t => t.TenantId == tenant.TenantId && t.Key == key, ct);
        if (table is null)
        {
            return NotFound(key);
        }

        db.LookupTables.Remove(table);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static LookupTableSummary Summary(LookupTable t, int count) =>
        new(t.Key, t.Description, t.CaseSensitive, t.Source.ToString(), t.TenantId is null, count, t.Revision, t.UpdatedAt);

    private static LookupEntryDto Dto(LookupEntry e) =>
        new(e.From, e.To, e.ValidFrom, e.ValidTo, e.LabelsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(e.LabelsJson));

    private static ProblemHttpResult NotFound(string key) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Lookup table not found", detail: $"The tenant has no lookup table '{key}'.");
}
