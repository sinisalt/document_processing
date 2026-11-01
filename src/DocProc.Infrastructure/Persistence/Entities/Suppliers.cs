namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>Cross-tenant vendor registry; carries extractor profiles so one tenant's learning helps another (docs/04).</summary>
public sealed class GlobalSupplier
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public List<string> VatIds { get; set; } = [];
    public List<string> EmailDomains { get; set; } = [];
    public string? CountryCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ExtractorProfile> Profiles { get; } = [];
}

/// <summary>A vendor as one tenant knows it. The same real-world company exists once per tenant.</summary>
public sealed class Supplier : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }

    /// <summary>Upper-cased, diacritics folded; used for fuzzy matching.</summary>
    public required string NormalizedName { get; set; }

    public string? VatId { get; set; }
    public string? TaxId { get; set; }
    public List<string> Ibans { get; set; } = [];
    public List<string> EmailDomains { get; set; } = [];
    public List<string> Aliases { get; set; } = [];

    /// <summary>Address lines as JSON; rarely queried.</summary>
    public string? AddressesJson { get; set; }

    public string? DefaultCurrency { get; set; }
    public bool AlwaysReview { get; set; }
    public Guid? GlobalSupplierId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Tenant? Tenant { get; set; }
    public GlobalSupplier? GlobalSupplier { get; set; }
}

public enum ProfileStatus
{
    Candidate,
    Active,
    Degraded,
    Retired,
}

public enum ProfileAuthor
{
    Llm,
    Human,
}

/// <summary>A "known format": versioned rules executed by the deterministic engine (docs/04). Temporal, so every version is auditable.</summary>
public sealed class ExtractorProfile
{
    public Guid Id { get; set; }
    public Guid? GlobalSupplierId { get; set; }

    /// <summary>Set when the profile is private to one tenant (sharing opted out).</summary>
    public Guid? SupplierId { get; set; }

    public Guid? TenantId { get; set; }
    public int Version { get; set; } = 1;
    public required string DocumentType { get; set; }
    public ProfileStatus Status { get; set; } = ProfileStatus.Candidate;

    /// <summary>Sender domains, VAT ids, anchor tokens and positions, page size (docs/04 "Fingerprinting").</summary>
    public string FingerprintJson { get; set; } = "{}";

    /// <summary>The field and line-item strategies; schema-versioned.</summary>
    public string RulesJson { get; set; } = "{}";

    public int RulesSchemaVersion { get; set; } = 1;
    public int Attempts { get; set; }
    public int Successes { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public double? SuccessRate30d { get; set; }

    /// <summary>Sample documents the profile was synthesised from; visible to platform operators only.</summary>
    public List<Guid> CreatedFromDocumentIds { get; set; } = [];

    public ProfileAuthor CreatedBy { get; set; } = ProfileAuthor.Llm;
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public GlobalSupplier? GlobalSupplier { get; set; }
    public Supplier? Supplier { get; set; }
}
