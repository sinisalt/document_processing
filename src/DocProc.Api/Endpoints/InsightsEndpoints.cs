using DocProc.Api.Auth;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>A layout of a supplier as Insights lists it: its type, key, the state of its newest version and the documents waiting for a Candidate.</summary>
public sealed record InsightLayout(Guid LayoutId, string TypeKey, string LayoutKey, string Name, int? Version, string State, DateTimeOffset? LastMatchedAt);

/// <summary>
/// One supplier's automation in the window (docs/11 §17): documents, auto-approved, corrections per document, LLM cost,
/// its layouts with their state (none, Candidate, Active, Degraded), the open <c>NEW_LAYOUT</c> documents and a sample
/// document to open.
/// </summary>
public sealed record SupplierAutomation(
    Guid SupplierId,
    string Name,
    int Documents,
    int AutoApproved,
    double AutoApprovalRate,
    double CorrectionsPerDocument,
    decimal LlmCostUsd,
    int OpenNewLayout,
    IReadOnlyList<InsightLayout> Layouts,
    Guid? SampleDocumentId);

/// <summary>The Unknown rate of one channel or one source rule.</summary>
public sealed record UnknownRate(Guid Id, string Name, Guid? SupplierId, int Documents, int Unknown, double Rate);

/// <summary>A layout worth a look: degraded, not matched for 180 days, or a Candidate with documents waiting for its activation.</summary>
public sealed record LayoutAttention(Guid LayoutId, Guid OwnerId, string? SupplierName, string TypeKey, string LayoutKey, int? Version, string State, DateTimeOffset? LastMatchedAt, int Waiting);

/// <summary>A tenant identification rule with how often reviewers overrode the type it gave.</summary>
public sealed record OverriddenRule(Guid RuleId, string Name, string TypeKey, int Matched, int Overridden, double Rate);

/// <summary>Deliveries of one destination in the window: total, failed, dead-lettered and the failure rate.</summary>
public sealed record DestinationHealth(Guid DestinationId, string Name, int Total, int Failed, int DeadLettered, double FailureRate);

/// <summary>The last run of a master-data source when it left conflicts open.</summary>
public sealed record MasterDataConflicts(Guid? SourceId, string Name, Guid RunId, DateTimeOffset At, int Conflicts);

/// <summary>
/// <c>GET /insights/automation</c> (docs/11 §17, the Insights &gt; Onboarding screen): what to onboard next and what
/// needs attention, for the window <c>[from, to]</c> (UTC dates, at most <see cref="InsightsEndpoints.MaxDays"/> days).
/// </summary>
public sealed record AutomationInsights(
    DateOnly From,
    DateOnly To,
    int Documents,
    int AutoApproved,
    int Unknown,
    IReadOnlyList<SupplierAutomation> Suppliers,
    IReadOnlyList<SupplierAutomation> SuppliersWithoutLayouts,
    IReadOnlyList<UnknownRate> UnknownByChannel,
    IReadOnlyList<UnknownRate> UnknownBySourceRule,
    IReadOnlyList<LayoutAttention> DegradedLayouts,
    IReadOnlyList<LayoutAttention> StaleLayouts,
    IReadOnlyList<LayoutAttention> CandidatesWaiting,
    IReadOnlyList<OverriddenRule> OverriddenRules,
    IReadOnlyList<DestinationHealth> Destinations,
    IReadOnlyList<MasterDataConflicts> MasterDataConflicts);

/// <summary>
/// The onboarding and operations view of docs/11 §17, from live queries over the window.
/// <para>
/// Correction (P5, part D): §17 backs the report with a nightly <c>SupplierDailyStats</c> roll-up; as built it reads the
/// window's documents directly (at most <see cref="MaxDays"/> days, filtered in SQL on SQL Server and in memory on the
/// development SQLite provider, which cannot compare <c>DateTimeOffset</c>, as the LLM usage report does), which is
/// enough for the pilot's volumes; the roll-up joins when a tenant's window outgrows it.
/// </para>
/// </summary>
public static class InsightsEndpoints
{
    public const int MaxDays = 92;

    public const int DefaultDays = 30;

    /// <summary>Layouts that have not matched for this long are listed as candidates to retire (advisory only, §17).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(180);

    /// <summary>The most rows of each list.</summary>
    public const int MaxRows = 50;

    public static RouteGroupBuilder MapInsights(this RouteGroupBuilder group)
    {
        group.MapGet("/insights/automation", AutomationAsync)
            .RequireAuthorization(Policies.Read)
            .WithTags("Insights")
            .WithName("GetAutomationInsights")
            .WithSummary("Automation per supplier, suppliers without layouts by volume, Unknown rates, layouts and rules to look at, delivery health and master-data conflicts (docs/11 §17).");
        return group;
    }

    private static async Task<Results<Ok<AutomationInsights>, ValidationProblem, ProblemHttpResult>> AutomationAsync(
        DateOnly? from,
        DateOnly? to,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, "Insights") is { } problem)
        {
            return problem;
        }

        var now = clock.GetUtcNow();
        var end = to ?? DateOnly.FromDateTime(now.UtcDateTime);
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        var errors = new Errors();
        if (start > end)
        {
            errors.Add("from", "from must not be after to.");
        }
        else if (end.DayNumber - start.DayNumber + 1 > MaxDays)
        {
            errors.Add("to", $"The window is at most {MaxDays} days.");
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var startAt = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endAt = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var sqlite = UsesSqlite(db);

        var documentQuery = db.Documents.AsNoTracking().Where(d => d.Status != DocumentStatus.Cancelled);
        if (!sqlite)
        {
            documentQuery = documentQuery.Where(d => d.CreatedAt >= startAt && d.CreatedAt < endAt);
        }

        var documents = (await documentQuery
                .Select(d => new { d.Id, d.SupplierId, d.Status, d.Type, d.SupplierSourceRuleId, ChannelId = d.IngestionItem!.Ingestion!.ChannelId, d.CreatedAt })
                .ToListAsync(ct))
            .Where(d => d.CreatedAt >= startAt && d.CreatedAt < endAt)
            .ToList();
        var ids = documents.Select(d => d.Id).ToHashSet();

        var corrections = (await db.Corrections.AsNoTracking().GroupBy(c => c.DocumentId).Select(g => new { DocumentId = g.Key, Count = g.Count() }).ToListAsync(ct))
            .Where(c => ids.Contains(c.DocumentId))
            .ToDictionary(c => c.DocumentId, c => c.Count);
        var cost = (await db.LlmUsage.AsNoTracking().Where(u => u.DocumentId != null).GroupBy(u => u.DocumentId!.Value).Select(g => new { DocumentId = g.Key, Cost = g.Sum(u => u.EstimatedCostUsd ?? 0m) }).ToListAsync(ct))
            .Where(c => ids.Contains(c.DocumentId))
            .ToDictionary(c => c.DocumentId, c => c.Cost);
        var openNewLayout = (await db.DocumentFlags.AsNoTracking()
                .Where(f => f.Code == FlagCodes.NewLayout && f.ResolvedAt == null)
                .Select(f => new { f.DocumentId, f.Document!.SupplierId, f.Document.Status })
                .ToListAsync(ct))
            .Where(f => f.SupplierId is not null && f.Status == DocumentStatus.NeedsReview)
            .GroupBy(f => f.SupplierId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(f => f.DocumentId).Distinct().Count());

        var suppliers = await db.Suppliers.AsNoTracking().Select(s => new { s.Id, s.Name, s.GroupId }).ToListAsync(ct);
        var supplierNames = suppliers.ToDictionary(s => s.Id, s => s.Name);
        var layouts = await db.SupplierLayouts.AsNoTracking()
            .Where(l => l.Status == SupplierLayoutStatus.Active)
            .Select(l => new { l.Id, l.Scope, l.OwnerId, l.DocumentTypeKey, l.LayoutKey, l.Name, l.ActiveVersion, l.LastMatchedAt })
            .ToListAsync(ct);
        var layoutIds = layouts.Select(l => l.Id).ToList();
        var versions = (await db.SupplierLayoutVersions.AsNoTracking()
                .Select(v => new { v.Id, v.LayoutId, v.Version, v.Status })
                .ToListAsync(ct))
            .Where(v => layoutIds.Contains(v.LayoutId))
            .ToList();
        var waiting = (await db.Documents.AsNoTracking()
                .Where(d => d.PendingLayoutVersionId != null && d.Status == DocumentStatus.NeedsReview)
                .GroupBy(d => d.PendingLayoutVersionId!.Value)
                .Select(g => new { VersionId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(w => w.VersionId, w => w.Count);

        // The state of a layout is that of its active version, else of its newest version that is not retired.
        InsightLayout Describe(Guid layoutId)
        {
            var layout = layouts.Single(l => l.Id == layoutId);
            var own = versions.Where(v => v.LayoutId == layoutId && v.Status != SupplierLayoutVersionStatus.Retired).OrderByDescending(v => v.Version).ToList();
            var current = own.FirstOrDefault(v => v.Version == layout.ActiveVersion) ?? own.FirstOrDefault();
            return new InsightLayout(layout.Id, layout.DocumentTypeKey, layout.LayoutKey, layout.Name, current?.Version, current?.Status.ToString() ?? "none", layout.LastMatchedAt);
        }

        var layoutsBySupplier = suppliers.ToDictionary(
            s => s.Id,
            s => layouts.Where(l => (l.Scope == SupplierLayoutScope.Supplier && l.OwnerId == s.Id) || (l.Scope == SupplierLayoutScope.Group && s.GroupId is { } g && l.OwnerId == g)).Select(l => Describe(l.Id)).ToList());

        var perSupplier = documents.Where(d => d.SupplierId is not null).GroupBy(d => d.SupplierId!.Value).Select(g =>
        {
            var count = g.Count();
            var auto = g.Count(d => d.Status == DocumentStatus.AutoApproved);
            var correctionCount = g.Sum(d => corrections.GetValueOrDefault(d.Id));
            var llm = g.Sum(d => cost.GetValueOrDefault(d.Id));
            var open = openNewLayout.GetValueOrDefault(g.Key);
            var sample = g.OrderByDescending(d => d.CreatedAt).First().Id;
            return new SupplierAutomation(
                g.Key,
                supplierNames.GetValueOrDefault(g.Key) ?? g.Key.ToString(),
                count,
                auto,
                Rate(auto, count),
                count == 0 ? 0 : Math.Round((double)correctionCount / count, 2),
                llm,
                open,
                layoutsBySupplier.GetValueOrDefault(g.Key) ?? [],
                sample);
        }).OrderByDescending(s => s.Documents).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();

        var channels = await db.IngestionChannels.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var unknownByChannel = documents.GroupBy(d => d.ChannelId)
            .Select(g => new UnknownRate(g.Key, channels.FirstOrDefault(c => c.Id == g.Key)?.Name ?? g.Key.ToString(), null, g.Count(), g.Count(d => d.Type.IsUnknown), Rate(g.Count(d => d.Type.IsUnknown), g.Count())))
            .OrderByDescending(r => r.Rate).ThenByDescending(r => r.Documents).Take(MaxRows).ToList();
        var rules = await db.SupplierSourceRules.AsNoTracking().Select(r => new { r.Id, r.SupplierId }).ToListAsync(ct);
        var unknownByRule = documents.Where(d => d.SupplierSourceRuleId is not null).GroupBy(d => d.SupplierSourceRuleId!.Value)
            .Select(g =>
            {
                var supplierId = rules.FirstOrDefault(r => r.Id == g.Key)?.SupplierId;
                var name = supplierId is { } sid ? supplierNames.GetValueOrDefault(sid) ?? g.Key.ToString() : g.Key.ToString();
                return new UnknownRate(g.Key, name, supplierId, g.Count(), g.Count(d => d.Type.IsUnknown), Rate(g.Count(d => d.Type.IsUnknown), g.Count()));
            })
            .OrderByDescending(r => r.Rate).ThenByDescending(r => r.Documents).Take(MaxRows).ToList();

        LayoutAttention Attention(Guid layoutId, int waitingCount = 0)
        {
            var layout = layouts.Single(l => l.Id == layoutId);
            var described = Describe(layoutId);
            return new LayoutAttention(layout.Id, layout.OwnerId, supplierNames.GetValueOrDefault(layout.OwnerId), layout.DocumentTypeKey, layout.LayoutKey, described.Version, described.State, layout.LastMatchedAt, waitingCount);
        }

        var degraded = layouts.Where(l => versions.Any(v => v.LayoutId == l.Id && v.Status == SupplierLayoutVersionStatus.Degraded)).Select(l => Attention(l.Id)).Take(MaxRows).ToList();
        var stale = layouts.Where(l => l.ActiveVersion is not null && (l.LastMatchedAt ?? DateTimeOffset.MinValue) < now - StaleAfter).Select(l => Attention(l.Id)).Take(MaxRows).ToList();
        var candidates = versions.Where(v => v.Status == SupplierLayoutVersionStatus.Candidate && waiting.ContainsKey(v.Id))
            .Select(v => Attention(v.LayoutId, waiting[v.Id]) with { Version = v.Version, State = v.Status.ToString() })
            .OrderByDescending(a => a.Waiting).Take(MaxRows).ToList();

        var overridden = (await db.TypeIdentificationRules.AsNoTracking().Where(r => r.Overridden > 0).Select(r => new { r.Id, r.Name, r.DocumentTypeKey, r.Matched, r.Overridden }).ToListAsync(ct))
            .Select(r => new OverriddenRule(r.Id, r.Name, r.DocumentTypeKey, r.Matched, r.Overridden, Rate(r.Overridden, Math.Max(r.Matched, r.Overridden))))
            .OrderByDescending(r => r.Rate).ThenByDescending(r => r.Overridden).Take(MaxRows).ToList();

        var deliveryQuery = db.OutboxDeliveries.AsNoTracking();
        if (!sqlite)
        {
            deliveryQuery = deliveryQuery.Where(d => d.CreatedAt >= startAt && d.CreatedAt < endAt);
        }

        var deliveries = (await deliveryQuery.Select(d => new { d.DestinationId, d.Status, d.CreatedAt }).ToListAsync(ct)).Where(d => d.CreatedAt >= startAt && d.CreatedAt < endAt).ToList();
        var destinations = await db.OutputDestinations.AsNoTracking().Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var health = deliveries.GroupBy(d => d.DestinationId).Select(g =>
        {
            var failed = g.Count(d => d.Status == DeliveryStatus.Failed);
            var dead = g.Count(d => d.Status == DeliveryStatus.DeadLettered);
            return new DestinationHealth(g.Key, destinations.FirstOrDefault(d => d.Id == g.Key)?.Name ?? g.Key.ToString(), g.Count(), failed, dead, Rate(failed + dead, g.Count()));
        }).OrderByDescending(h => h.FailureRate).ThenByDescending(h => h.Total).ToList();

        // Open master-data conflicts: the newest non-dry run of each source (and of the one-off imports) when it left conflicts.
        var sources = await db.MasterDataSources.AsNoTracking().Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        var runs = await db.MasterDataRuns.AsNoTracking().Where(r => !r.DryRun).Select(r => new { r.Id, r.SourceId, r.At, r.Conflicts }).ToListAsync(ct);
        var conflicts = runs.GroupBy(r => r.SourceId)
            .Select(g => g.OrderByDescending(r => r.At).First())
            .Where(r => r.Conflicts > 0)
            .Select(r => new MasterDataConflicts(r.SourceId, r.SourceId is { } sid ? sources.FirstOrDefault(s => s.Id == sid)?.Name ?? sid.ToString() : "suppliers:import", r.Id, r.At, r.Conflicts))
            .OrderByDescending(c => c.Conflicts).ToList();

        var withoutLayouts = perSupplier.Where(s => s.Layouts.Count == 0).Take(MaxRows).ToList();
        return TypedResults.Ok(new AutomationInsights(
            start,
            end,
            documents.Count,
            documents.Count(d => d.Status == DocumentStatus.AutoApproved),
            documents.Count(d => d.Type.IsUnknown),
            [.. perSupplier.Take(MaxRows * 4)],
            withoutLayouts,
            unknownByChannel,
            unknownByRule,
            degraded,
            stale,
            candidates,
            overridden,
            health,
            conflicts));
    }

    private static double Rate(int part, int whole) => whole == 0 ? 0 : Math.Round((double)part / whole, 4);
}
