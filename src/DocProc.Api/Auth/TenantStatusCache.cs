using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocProc.Api.Auth;

/// <summary>
/// The status of a tenant for the suspension check in <see cref="TenantResolutionMiddleware"/>: one platform-scoped
/// read per tenant per <see cref="Ttl"/>, so suspending a tenant locks its users out within half a minute without a
/// database round trip on every request. An unknown tenant is not cached, so a tenant created a moment ago is seen
/// at once. Entries are stamped with the application <see cref="TimeProvider"/> (the clock everything else runs on,
/// and the one tests control); the memory cache's own expiration is only the backstop that frees the memory.
/// A host without persistence (only the OpenAPI export runs that way) has no tenant table to consult and treats every
/// tenant as active; no tenant data is reachable in that host anyway.
/// </summary>
public sealed class TenantStatusCache(IServiceProvider services, IMemoryCache cache, TimeProvider clock)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ISystemDbContextFactory? _contexts = services.GetService<ISystemDbContextFactory>();

    /// <summary>The tenant's status, or null when no tenant with that id exists.</summary>
    public async Task<TenantStatus?> GetStatusAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_contexts is not { } contexts)
        {
            return TenantStatus.Active;
        }

        var key = new Key(tenantId);
        var now = clock.GetUtcNow();
        if (cache.TryGetValue(key, out Entry? cached) && cached is not null && cached.FreshUntil > now)
        {
            return cached.Status;
        }

        await using var db = contexts.CreatePlatformScoped();
        var status = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => (TenantStatus?)t.Status)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (status is { } known)
        {
            cache.Set(key, new Entry(known, now + Ttl), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl });
        }

        return status;
    }

    /// <summary>Forgets a tenant so the next request reads the row again (for tests and the status endpoints).</summary>
    public void Invalidate(Guid tenantId) => cache.Remove(new Key(tenantId));

    private sealed record Key(Guid TenantId);

    private sealed record Entry(TenantStatus Status, DateTimeOffset FreshUntil);
}

public static class TenantStatusCacheServiceCollectionExtensions
{
    /// <summary>Registers the memory cache and the <see cref="TenantStatusCache"/> the tenant middleware enforces suspension with.</summary>
    public static IServiceCollection AddTenantStatusCache(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMemoryCache();
        services.TryAddSingleton<TenantStatusCache>();
        return services;
    }
}
