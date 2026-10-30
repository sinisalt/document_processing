namespace DocProc.Infrastructure.Persistence.Tenancy;

/// <summary>
/// The tenant a unit of work runs for. Set per request from the token or the X-Tenant-Id header (docs/05) and per
/// job in the worker. The DbContext filters every tenant-scoped query by it and SQL Server enforces the same rule
/// through row-level security keyed on SESSION_CONTEXT.
/// </summary>
public interface ITenantContext
{
    /// <summary>The current tenant, or null when running in platform scope.</summary>
    Guid? TenantId { get; }

    /// <summary>Platform operators and cross-tenant jobs see every tenant; nothing is filtered.</summary>
    bool IsPlatformScope { get; }
}

/// <summary>Mutable implementation for hosts and tests; register it scoped.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public bool IsPlatformScope { get; private set; }

    public static TenantContext ForTenant(Guid tenantId) => new() { TenantId = tenantId };

    public static TenantContext Platform() => new() { IsPlatformScope = true };

    /// <summary>No tenant and no platform scope: every tenant-scoped query returns nothing. The safe default.</summary>
    public static TenantContext Anonymous() => new();

    public void SetTenant(Guid tenantId)
    {
        TenantId = tenantId;
        IsPlatformScope = false;
    }

    public void SetPlatformScope()
    {
        TenantId = null;
        IsPlatformScope = true;
    }
}
