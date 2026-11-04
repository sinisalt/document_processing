namespace DocProc.Infrastructure.Persistence.Entities;

public enum TenantStatus
{
    Active,
    Suspended,
    Closed,
}

public enum TenantRole
{
    TenantAdmin,
    Reviewer,
    Viewer,
}

/// <summary>Review policy and retention; stored as a JSON column so it can grow without migrations.</summary>
public sealed class TenantSettings
{
    /// <summary>Fields below this confidence raise LOW_CONFIDENCE and block auto-approval.</summary>
    public double AutoApproveConfidenceThreshold { get; set; } = 0.8;

    /// <summary>Suppliers whose documents always go to a human.</summary>
    public List<Guid> AlwaysReviewSupplierIds { get; set; } = [];

    /// <summary>Tax-record retention; per-country defaults (Germany 8, Croatia 11, France 10).</summary>
    public int RetentionYears { get; set; } = 10;

    /// <summary>Share extractor profiles learned from this tenant's documents through the global supplier registry.</summary>
    public bool ShareExtractorProfiles { get; set; } = true;

    /// <summary>
    /// The per-tenant switches of docs/11 §3 (principle 6): every configurable-types capability ships behind one and
    /// stays off until a tenant is moved onto it. Read through <c>ITenantFeatures</c>, never directly.
    /// <para>
    /// Every stored settings object carries it: new tenants through this initialiser, existing ones through the
    /// <c>P0PersistenceSeams</c> back-fill. That matters for writes: when the stored JSON lacks the object, EF leaves
    /// this initialised instance in place untracked, so changes to it are silently not saved, and assigning a new
    /// instance fails on SQL Server (the partial JSON update addresses a path that does not exist). Null is still read
    /// as all off, for safety.
    /// </para>
    /// </summary>
    public TenantFeatureFlags? Features { get; set; } = new();

    /// <summary>
    /// The tenant's supplier attribute schema (docs/11 §6.4 <c>SupplierAttributeSchema</c>): a JSON Schema object of the
    /// typed subset <c>SupplierAttributeSchema</c> (DocProc.Application.Suppliers) reads, which supplier, group and
    /// per-entity attributes are validated against when they are written. Null for none: attributes are free scalars,
    /// as in P3.
    /// </summary>
    public string? SupplierAttributeSchema { get; set; }

    /// <summary>
    /// The tenant default of a supplier's <c>layouts.required: inherit</c> (docs/11 §6.4
    /// <c>TenantSettings.Layouts.RequiredByDefault</c>, stored flat): whether a supplier with no layout yet counts as
    /// layout-managed. Default false.
    /// </summary>
    public bool LayoutsRequiredByDefault { get; set; }

    /// <summary>
    /// Forwarder domains trusted for every mailbox channel of the tenant (docs/11 §7.1), next to a channel's own
    /// <c>trustedForwarderDomains</c>: a forward from such a domain is unwrapped to its original sender. Null or empty for none.
    /// </summary>
    public List<string>? TrustedForwarderDomains { get; set; }
}

/// <summary>Per-tenant feature flags (docs/11 §3); all off by default, so a tenant behaves as before until switched.</summary>
public sealed class TenantFeatureFlags
{
    /// <summary>Document types as data: the type catalogue, definitions and the generated review form (P1).</summary>
    public bool ConfigurableTypes { get; set; }

    /// <summary>Output mappings, destinations and delivery (P3).</summary>
    public bool Outputs { get; set; }

    /// <summary>Supplier source rules: routing by mailbox, sender and folder (P4).</summary>
    public bool SourceRouting { get; set; }

    /// <summary>Supplier layouts with identification rules and field mappings (P4).</summary>
    public bool SupplierLayouts { get; set; }

    /// <summary>Tenant-defined types beyond the standard templates, end to end (P5).</summary>
    public bool CustomTypes { get; set; }

    /// <summary>Structured sources: spreadsheets, custom XML and JSON, e-mail bodies (P6).</summary>
    public bool StructuredSources { get; set; }
}

/// <summary>A customer: one restaurant or a restaurant group.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }

    /// <summary>Legal name as it appears on invoices addressed to the tenant; used for the CUSTOMER_MISMATCH check.</summary>
    public required string LegalName { get; set; }

    public string? VatId { get; set; }
    public List<string> NameAliases { get; set; } = [];
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public required string DefaultCurrency { get; set; }
    public required string CountryCode { get; set; }
    public required string TimeZone { get; set; }
    public TenantSettings Settings { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<TenantMember> Members { get; } = [];
    public ICollection<IngestionChannel> Channels { get; } = [];
}

public sealed class TenantMember : ITenantScoped
{
    public Guid TenantId { get; set; }

    /// <summary>Subject from the identity provider (OIDC <c>sub</c>).</summary>
    public required string UserId { get; set; }

    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public TenantRole Role { get; set; } = TenantRole.Reviewer;
    public DateTimeOffset CreatedAt { get; set; }

    public Tenant? Tenant { get; set; }
}

public enum ApiKeyScope
{
    Ingest,
    Read,

    /// <summary><c>deliveries:read</c> (docs/11 §13.3): a pull destination's feed (P3).</summary>
    DeliveriesRead,
}

/// <summary>The names an API key's scopes have in the API (<see cref="Application.Security.ApiKeyScopes"/>).</summary>
public static class ApiKeyScopeNames
{
    public static string Name(ApiKeyScope scope) => scope switch
    {
        ApiKeyScope.Ingest => Application.Security.ApiKeyScopes.Ingest,
        ApiKeyScope.Read => Application.Security.ApiKeyScopes.Read,
        ApiKeyScope.DeliveriesRead => Application.Security.ApiKeyScopes.DeliveriesRead,
        _ => scope.ToString().ToLowerInvariant(),
    };

    public static ApiKeyScope Parse(string name)
    {
        foreach (var scope in Enum.GetValues<ApiKeyScope>())
        {
            if (string.Equals(Name(scope), name, StringComparison.OrdinalIgnoreCase))
            {
                return scope;
            }
        }

        throw new ArgumentException($"Unknown API key scope '{name}'.", nameof(name));
    }
}

/// <summary>Machine credential for ingest/read; only the hash is stored (docs/01 "Identity").</summary>
public sealed class ApiKey : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }

    /// <summary>First characters of the key, shown in the UI to identify it.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the full key.</summary>
    public required string Hash { get; set; }

    public List<ApiKeyScope> Scopes { get; set; } = [];

    /// <summary>The one destination a <see cref="ApiKeyScope.DeliveriesRead"/> key may read the feed of; null for every pull destination of the tenant.</summary>
    public Guid? DestinationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public Tenant? Tenant { get; set; }
}
