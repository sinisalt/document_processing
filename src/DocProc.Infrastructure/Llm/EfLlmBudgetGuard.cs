using System.Collections.Concurrent;
using DocProc.Infrastructure.Persistence;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmBudgetGuard"/> over <c>LlmBudgets</c> and the usage rows (<c>LlmUsage</c>, docs/11 §16): a call is
/// refused when the spend of a budget's period (the current UTC day or month, or the document) has reached the budget,
/// or would pass it by the call's estimate. The estimate is the average cost of the tenant's last
/// <see cref="EstimateSample"/> successful calls of the same stage (0 without any), so a stage whose next call would
/// overspend is not called. The calls this process allowed and has not recorded yet count with their estimates
/// (<see cref="ReserveAsync(Guid, LlmStage, Guid?, CancellationToken)"/>). Reads in platform scope with the tenant as an explicit predicate, like the policy store.
/// </summary>
public sealed class EfLlmBudgetGuard(ISystemDbContextFactory contexts, TimeProvider clock) : ILlmBudgetGuard
{
    /// <summary>How many recent calls the estimate averages.</summary>
    public const int EstimateSample = 20;

    /// <summary>The estimates of this process's calls that are allowed and not yet recorded, by tenant.</summary>
    private readonly ConcurrentDictionary<Guid, TenantHolds> _holds = new();

    public async Task<LlmBudgetVerdict> CheckAsync(Guid tenantId, LlmStage stage, Guid? documentId, CancellationToken cancellationToken) =>
        (await DecideAsync(tenantId, stage, documentId, null, _holds.GetValueOrDefault(tenantId), cancellationToken).ConfigureAwait(false)).Verdict;

    /// <summary>
    /// The check and the hold of one call (docs/11 §16, Correction (P5) fix1). The decision is taken under the tenant's
    /// gate, counting the estimates of the calls this process already allowed and has not recorded yet, and an allowed
    /// call's estimate is held until the gateway disposes the reservation (after its usage row is written). So parallel
    /// calls of one process (<c>Pipeline:MaxParallelism</c>) cannot each pass the check against the same spend.
    /// Replicas do not see each other's holds: across N worker replicas a period's budget can still be passed by up to
    /// N - 1 calls, which docs/11 §16 states as the bound.
    /// </summary>
    public Task<LlmBudgetReservation> ReserveAsync(Guid tenantId, LlmStage stage, Guid? documentId, CancellationToken cancellationToken) =>
        ReserveAsync(tenantId, stage, documentId, null, cancellationToken);

    /// <summary>
    /// <see cref="ReserveAsync(Guid, LlmStage, Guid?, CancellationToken)"/> for a call of <paramref name="unpricedModel"/>,
    /// a model the price table has no price for: its cost would be recorded as unknown and count as 0, so while a budget
    /// applies to the call it is refused (fails closed, docs/11 §16, Correction (P5) fix 4). Without a budget it is
    /// allowed as before.
    /// </summary>
    public async Task<LlmBudgetReservation> ReserveAsync(Guid tenantId, LlmStage stage, Guid? documentId, string? unpricedModel, CancellationToken cancellationToken)
    {
        var holds = _holds.GetOrAdd(tenantId, _ => new TenantHolds());
        await holds.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (verdict, estimate, budgeted) = await DecideAsync(tenantId, stage, documentId, unpricedModel, holds, cancellationToken).ConfigureAwait(false);
            if (!verdict.Allowed || !budgeted)
            {
                return new LlmBudgetReservation(verdict, null);
            }

            var hold = new Hold(stage, documentId, estimate);
            lock (holds.Items)
            {
                holds.Items.Add(hold);
            }

            return new LlmBudgetReservation(verdict, () =>
            {
                lock (holds.Items)
                {
                    holds.Items.Remove(hold);
                }
            });
        }
        finally
        {
            holds.Gate.Release();
        }
    }

    /// <summary>How many estimates this process holds for <paramref name="tenantId"/> (for tests).</summary>
    public int HeldCount(Guid tenantId)
    {
        if (!_holds.TryGetValue(tenantId, out var holds))
        {
            return 0;
        }

        lock (holds.Items)
        {
            return holds.Items.Count;
        }
    }

    private async Task<(LlmBudgetVerdict Verdict, decimal Estimate, bool Budgeted)> DecideAsync(Guid tenantId, LlmStage stage, Guid? documentId, string? unpricedModel, TenantHolds? holds, CancellationToken cancellationToken)
    {
        await using var db = contexts.CreatePlatformScoped();
        var budgets = await db.LlmBudgets.AsNoTracking()
            .Where(b => b.TenantId == tenantId && (b.Stage == null || b.Stage == stage))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        budgets = [.. budgets.Where(b => b.Period != LlmBudgetPeriod.Document || documentId is not null)];
        if (budgets.Count == 0)
        {
            return (LlmBudgetVerdict.Allow, 0m, false);
        }

        if (unpricedModel is not null)
        {
            var applies = budgets.OrderBy(b => b.Period).ThenBy(b => b.Stage is null).First().ToDomain();
            return (LlmBudgetVerdict.Unpriced(applies, unpricedModel), 0m, true);
        }

        var estimate = await EstimateAsync(db, tenantId, stage, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        List<Hold> held;
        if (holds is null)
        {
            held = [];
        }
        else
        {
            lock (holds.Items)
            {
                held = [.. holds.Items];
            }
        }

        foreach (var budget in budgets.OrderBy(b => b.Period).ThenBy(b => b.Stage is null))
        {
            var limit = budget.ToDomain();
            var spent = await SpentAsync(db, tenantId, limit, documentId, now, cancellationToken).ConfigureAwait(false)
                        + held.Where(h => (limit.Stage is null || h.Stage == limit.Stage) && (limit.Period != LlmBudgetPeriod.Document || h.DocumentId == documentId)).Sum(h => h.Amount);
            if (spent >= budget.LimitUsd || spent + estimate > budget.LimitUsd)
            {
                return (new LlmBudgetVerdict(false, limit, spent, estimate), estimate, true);
            }
        }

        return (LlmBudgetVerdict.Allow, estimate, true);
    }

    private sealed class TenantHolds
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public List<Hold> Items { get; } = [];
    }

    private sealed class Hold(LlmStage stage, Guid? documentId, decimal amount)
    {
        public LlmStage Stage { get; } = stage;

        public Guid? DocumentId { get; } = documentId;

        public decimal Amount { get; } = amount;
    }

    /// <summary>The start of <paramref name="period"/> that contains <paramref name="now"/> (UTC); null for a document budget.</summary>
    public static DateTimeOffset? PeriodStart(LlmBudgetPeriod period, DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return period switch
        {
            LlmBudgetPeriod.Day => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero),
            LlmBudgetPeriod.Month => new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero),
            _ => null,
        };
    }

    /// <summary>
    /// What the tenant spent in the budget's current period (for a document budget: on <paramref name="documentId"/>),
    /// for the budget's stage or every stage. Summed in memory, as <see cref="EfLlmPolicyStore"/> does, because SQLite
    /// cannot sum decimals or compare the development mode's timestamps; SQL Server filters server-side.
    /// </summary>
    public static async Task<decimal> SpentAsync(DocProcDbContext db, Guid tenantId, LlmBudget budget, Guid? documentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(budget);
        var usage = db.LlmUsage.AsNoTracking().Where(u => u.TenantId == tenantId);
        if (budget.Stage is { } stage)
        {
            usage = usage.Where(u => u.Stage == stage);
        }

        if (budget.Period == LlmBudgetPeriod.Document)
        {
            if (documentId is not { } id)
            {
                return 0m;
            }

            usage = usage.Where(u => u.DocumentId == id);
            var perDocument = await usage.Select(u => u.EstimatedCostUsd).ToListAsync(cancellationToken).ConfigureAwait(false);
            return perDocument.Sum(c => c ?? 0m);
        }

        var since = PeriodStart(budget.Period, now)!.Value;
        if (db.Database.IsSqlite())
        {
            var rows = await usage.Select(u => new { u.At, u.EstimatedCostUsd }).ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Where(r => r.At >= since).Sum(r => r.EstimatedCostUsd ?? 0m);
        }

        var costs = await usage.Where(u => u.At >= since).Select(u => u.EstimatedCostUsd).ToListAsync(cancellationToken).ConfigureAwait(false);
        return costs.Sum(c => c ?? 0m);
    }

    private static async Task<decimal> EstimateAsync(DocProcDbContext db, Guid tenantId, LlmStage stage, CancellationToken cancellationToken)
    {
        var recent = await db.LlmUsage.AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.Stage == stage && u.Error == null && u.EstimatedCostUsd != null)
            .OrderByDescending(u => u.Id)
            .Take(EstimateSample)
            .Select(u => u.EstimatedCostUsd)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return recent.Count == 0 ? 0m : recent.Sum(c => c ?? 0m) / recent.Count;
    }
}
