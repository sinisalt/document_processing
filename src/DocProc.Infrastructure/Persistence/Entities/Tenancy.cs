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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public Tenant? Tenant { get; set; }
}
