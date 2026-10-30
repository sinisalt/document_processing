using DocProc.Infrastructure.Persistence;
using DocProc.Llm.Gateway;
using DocProc.Llm.Providers;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmPolicyStore"/> over the persisted policy and provider rows (docs/08 "Provider configuration model").
/// Reads in platform scope so the gateway singleton can serve any tenant from any scope; the tenant id is an explicit
/// predicate on every query. The month-to-date spend is the sum of the tenant's usage rows since the first of the
/// current UTC month, which the gateway compares with the resolved configuration's cap.
/// </summary>
public sealed class EfLlmPolicyStore(ISystemDbContextFactory contexts, TimeProvider clock) : ILlmPolicyStore
{
    public async Task<TenantProviderSetup> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = contexts.CreatePlatformScoped();

        var policy = await db.TenantLlmPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);

        var tenantConfigs = await db.LlmProviderConfigs.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var platformConfigs = await db.LlmProviderConfigs.AsNoTracking()
            .Where(c => c.Scope == ConfigScope.Platform && c.TenantId == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // An active platform configuration wins; otherwise the newest one is handed over so the resolver can report why it is unusable.
        var platformDefault = platformConfigs
            .OrderByDescending(c => c.Status == ProviderConfigStatus.Active)
            .ThenByDescending(c => c.UpdatedAt)
            .FirstOrDefault();

        var now = clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var spent = await SpentSinceAsync(db, tenantId, monthStart, cancellationToken).ConfigureAwait(false);

        return new TenantProviderSetup(
            policy?.ToDomain(),
            tenantConfigs.ToDictionary(c => c.Id, c => c.ToDomain()),
            platformDefault?.ToDomain(),
            spent);
    }

    /// <summary>
    /// The rows are summed in memory because SQLite cannot aggregate decimals, and in the development mode it cannot
    /// compare DateTimeOffset columns either (docs/09), so there the month filter runs on the client as well. A tenant's
    /// month is a bounded number of calls; SQL Server filters server-side.
    /// </summary>
    private static async Task<decimal> SpentSinceAsync(DocProcDbContext db, Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var usage = db.LlmUsage.AsNoTracking().Where(u => u.TenantId == tenantId);
        if (db.Database.IsSqlite())
        {
            var rows = await usage.Select(u => new { u.At, u.EstimatedCostUsd }).ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Where(r => r.At >= since).Sum(r => r.EstimatedCostUsd ?? 0m);
        }

        var costs = await usage.Where(u => u.At >= since).Select(u => u.EstimatedCostUsd).ToListAsync(cancellationToken).ConfigureAwait(false);
        return costs.Sum(c => c ?? 0m);
    }
}
