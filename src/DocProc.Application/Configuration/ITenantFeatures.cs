namespace DocProc.Application.Configuration;

/// <summary>
/// The capabilities of docs/11 that ship behind a per-tenant switch (§3, principle 6). Each one stays off for a tenant
/// until it is moved onto it, so the invoice family behaves as before for everyone else.
/// </summary>
public enum TenantFeature
{
    /// <summary>Document types as data: catalogue, definitions, generated review form (P1).</summary>
    ConfigurableTypes,

    /// <summary>Output mappings, destinations and delivery (P3).</summary>
    Outputs,

    /// <summary>Supplier source rules: routing by mailbox, sender and folder (P4).</summary>
    SourceRouting,

    /// <summary>Supplier layouts: identification rules plus field mappings (P4).</summary>
    SupplierLayouts,

    /// <summary>Tenant-defined types beyond the standard templates, end to end (P5).</summary>
    CustomTypes,

    /// <summary>Structured sources: spreadsheets, custom XML and JSON, e-mail bodies (P6).</summary>
    StructuredSources,

    /// <summary>Configurable steps per phase: pipeline overrides and the <c>http-lookup</c> step (P5, docs/11 §10).</summary>
    ConfigurableSteps,
}

/// <summary>The switches that are on for one tenant.</summary>
public sealed class TenantFeatureSet
{
    private readonly HashSet<TenantFeature> _enabled;

    public TenantFeatureSet(IEnumerable<TenantFeature> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        _enabled = [.. enabled];
    }

    /// <summary>Every switch off: the default for every tenant, and the answer for a tenant that cannot be read.</summary>
    public static TenantFeatureSet None { get; } = new([]);

    public IReadOnlyCollection<TenantFeature> Enabled => _enabled;

    public bool IsEnabled(TenantFeature feature) => _enabled.Contains(feature);
}

/// <summary>
/// The one place feature switches are read (docs/11 §3). Code that branches on a capability asks this service, never
/// the settings row, so the switch can later move (to a plan, a rollout ring) without touching the callers.
/// </summary>
public interface ITenantFeatures
{
    /// <summary>The switches of <paramref name="tenantId"/>; <see cref="TenantFeatureSet.None"/> when the tenant is unknown or not visible in the current scope.</summary>
    Task<TenantFeatureSet> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="feature"/> is on for <paramref name="tenantId"/>.</summary>
    Task<bool> IsEnabledAsync(Guid tenantId, TenantFeature feature, CancellationToken cancellationToken = default);
}
