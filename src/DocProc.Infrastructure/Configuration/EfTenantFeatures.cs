using DocProc.Application.Configuration;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Configuration;

/// <summary>
/// <see cref="ITenantFeatures"/> over <see cref="TenantSettings.Features"/>. Scoped like the context it reads through,
/// so the tenant scope's query filter applies (a tenant sees only its own switches), and each tenant is read once per
/// scope: a request or a pipeline step sees one consistent set.
/// </summary>
public sealed class EfTenantFeatures(DocProcDbContext db) : ITenantFeatures
{
    private readonly Dictionary<Guid, TenantFeatureSet> _cache = [];

    public async Task<TenantFeatureSet> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var tenant = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var set = From(tenant?.Settings.Features);
        _cache[tenantId] = set;
        return set;
    }

    public async Task<bool> IsEnabledAsync(Guid tenantId, TenantFeature feature, CancellationToken cancellationToken = default) =>
        (await GetAsync(tenantId, cancellationToken).ConfigureAwait(false)).IsEnabled(feature);

    /// <summary>The set the stored flags describe; null (settings stored before the flags existed) is all off.</summary>
    public static TenantFeatureSet From(TenantFeatureFlags? flags)
    {
        if (flags is null)
        {
            return TenantFeatureSet.None;
        }

        var enabled = new List<TenantFeature>();
        Add(flags.ConfigurableTypes, TenantFeature.ConfigurableTypes);
        Add(flags.Outputs, TenantFeature.Outputs);
        Add(flags.SourceRouting, TenantFeature.SourceRouting);
        Add(flags.SupplierLayouts, TenantFeature.SupplierLayouts);
        Add(flags.CustomTypes, TenantFeature.CustomTypes);
        Add(flags.StructuredSources, TenantFeature.StructuredSources);
        return enabled.Count == 0 ? TenantFeatureSet.None : new TenantFeatureSet(enabled);

        void Add(bool on, TenantFeature feature)
        {
            if (on)
            {
                enabled.Add(feature);
            }
        }
    }
}
