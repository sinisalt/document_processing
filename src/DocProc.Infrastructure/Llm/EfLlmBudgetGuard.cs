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
/// overspend is not called. Reads in platform scope with the tenant as an explicit predicate, like the policy store.
/// </summary>
public sealed class EfLlmBudgetGuard(ISystemDbContextFactory contexts, TimeProvider clock) : ILlmBudgetGuard
{
    /// <summary>How many recent calls the estimate averages.</summary>
    public const int EstimateSample = 20;

    public async Task<LlmBudgetVerdict> CheckAsync(Guid tenantId, LlmStage stage, Guid? documentId, CancellationToken cancellationToken)
    {
        await using var db = contexts.CreatePlatformScoped();
        var budgets = await db.LlmBudgets.AsNoTracking()
            .Where(b => b.TenantId == tenantId && (b.Stage == null || b.Stage == stage))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        budgets = [.. budgets.Where(b => b.Period != LlmBudgetPeriod.Document || documentId is not null)];
        if (budgets.Count == 0)
        {
            return LlmBudgetVerdict.Allow;
        }

        var estimate = await EstimateAsync(db, tenantId, stage, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        foreach (var budget in budgets.OrderBy(b => b.Period).ThenBy(b => b.Stage is null))
        {
            var spent = await SpentAsync(db, tenantId, budget.ToDomain(), documentId, now, cancellationToken).ConfigureAwait(false);
            if (spent >= budget.LimitUsd || spent + estimate > budget.LimitUsd)
            {
                return new LlmBudgetVerdict(false, budget.ToDomain(), spent, estimate);
            }
        }

        return LlmBudgetVerdict.Allow;
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
