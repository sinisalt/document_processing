using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Outputs;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.MasterData;

/// <summary>
/// Runs a master-data source (docs/11 §6.8, §14): an http source's records are read with <see cref="DestinationReader"/>
/// (the destination's URL, headers, authentication and secret; public addresses only, at most <see cref="MaxResponseBytes"/>
/// in <see cref="Timeout"/>), an upload's are given; the source's mapping (JSONata in the out-of-process renderer) turns
/// them into supplier records, <see cref="SupplierImporter"/> applies them under the source's match keys and conflict
/// policy, and a <see cref="MasterDataRun"/> records the report. The source's last result and next scheduled run are
/// updated. Everything commits in one save; a dry run saves the run row only.
/// </summary>
public sealed class MasterDataSync(IOutputRenderer renderer, DestinationReader reader, TimeProvider clock)
{
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<MasterDataRun> RunAsync(DocProcDbContext db, MasterDataSource source, MasterDataTrigger trigger, bool dryRun, JsonNode? uploaded, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        var now = clock.GetUtcNow();
        var (records, failure) = await RecordsAsync(db, source, uploaded, cancellationToken).ConfigureAwait(false);
        var report = failure is not null
            ? MasterDataReport.Failed(failure)
            : await SupplierImporter.ImportAsync(db, source.TenantId, records, source.MatchKeys, Policies(source.ConflictPolicyJson), dryRun, now, cancellationToken).ConfigureAwait(false);
        var run = Record(db, source.TenantId, source.Id, trigger, dryRun, report, now, actor);
        source.LastRunAt = now;
        source.LastResultJson = JsonSerializer.Serialize(new
        {
            status = run.Status.ToString(),
            dryRun,
            runId = run.Id,
            created = report.Created,
            updated = report.Updated,
            unchanged = report.Unchanged,
            conflicts = report.Conflicts,
            errors = report.Errors,
            failure = report.Failure,
        });
        source.NextRunAt = NextRun(source, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    /// <summary>The run row of a report, added unsaved.</summary>
    public static MasterDataRun Record(DocProcDbContext db, Guid tenantId, Guid? sourceId, MasterDataTrigger trigger, bool dryRun, MasterDataReport report, DateTimeOffset now, string actor)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(report);
        var run = new MasterDataRun
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SourceId = sourceId,
            Trigger = trigger,
            DryRun = dryRun,
            Status = report.Failure is null ? MasterDataRunStatus.Succeeded : MasterDataRunStatus.Failed,
            Created = report.Created,
            Updated = report.Updated,
            Unchanged = report.Unchanged,
            Conflicts = report.Conflicts,
            Errors = report.Errors,
            ReportJson = report.ToJson(),
            At = now,
            By = actor.Length <= 200 ? actor : actor[..200],
        };
        db.MasterDataRuns.Add(run);
        return run;
    }

    /// <summary>The next scheduled run after <paramref name="now"/>, or null for a source without a schedule (or disabled).</summary>
    public static DateTimeOffset? NextRun(MasterDataSource source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Enabled && source.ScheduleCron is { Length: > 0 } cron && CronSchedule.TryParse(cron, out var schedule, out _) ? schedule!.Next(now) : null;
    }

    /// <summary>The conflict policy per attribute, from the source's JSON object.</summary>
    public static IReadOnlyDictionary<string, string>? Policies(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) is JsonObject obj
                ? obj.Where(p => p.Value is JsonValue v && v.TryGetValue<string>(out _)).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(IReadOnlyList<SupplierRecord> Records, string? Failure)> RecordsAsync(DocProcDbContext db, MasterDataSource source, JsonNode? uploaded, CancellationToken cancellationToken)
    {
        JsonNode? raw = uploaded;
        if (raw is null)
        {
            if (source.Transport != MasterDataTransports.Http || source.DestinationId is not { } destinationId)
            {
                return ([], "An upload source runs with the records in the request.");
            }

            var destination = await db.OutputDestinations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == destinationId && d.TenantId == source.TenantId, cancellationToken).ConfigureAwait(false);
            if (destination is null || destination.Transport != OutputTransports.Http || destination.Status != OutputDestinationStatus.Active)
            {
                return ([], "The source's destination is not an active http destination of the tenant.");
            }

            var config = JsonNode.Parse(destination.ConfigJson) as JsonObject ?? [];
            var template = config["urlTemplate"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : string.Empty;
            if (template.Contains("{{", StringComparison.Ordinal) || !Uri.TryCreate(template, UriKind.Absolute, out var uri))
            {
                return ([], "The destination's urlTemplate must be a plain absolute URL (no placeholders) for a master-data source.");
            }

            var answer = await reader.GetAsync(destination, uri, Timeout, MaxResponseBytes, null, cancellationToken).ConfigureAwait(false);
            if (answer.Body is not { } body)
            {
                return ([], answer.Problem);
            }

            try
            {
                raw = JsonNode.Parse(Encoding.UTF8.GetString(body.Span));
            }
            catch (JsonException)
            {
                return ([], "The source's answer is not JSON.");
            }
        }

        if (source.MappingExpression is { Length: > 0 } mapping)
        {
            var rendered = await renderer.RenderAsync(new RenderRequest(mapping, raw, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)), cancellationToken).ConfigureAwait(false);
            if (rendered.Error is { } error)
            {
                return ([], $"The source's mapping failed ({error.Kind}): {error.Message}");
            }

            raw = rendered.Output;
        }

        var records = SupplierImporter.FromJson(raw, out var problem);
        return (records, problem);
    }
}
