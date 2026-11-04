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

    /// <summary>
    /// The tenant's attributes of the supplier (docs/11 §6.4), a JSON object of scalars such as
    /// <c>{"erpVendorId":"V-10042"}</c>: what output mappings read as <c>supplier.attributes</c>. Null until set. The
    /// tenant's attribute schema and the per-entity attributes come with P4.
    /// </summary>
    public string? AttributesJson { get; set; }

    /// <summary>
    /// The supplier's settings (docs/11 §6.4 <c>SettingsJson</c>): types, locale, review policy, defaults,
    /// <c>layouts.required</c> and <c>requireSenderAuth</c>, a JSON object read as <c>SupplierSettings</c>
    /// (DocProc.Application.Suppliers); null until set, which reads as every setting inherited.
    /// </summary>
    public string? SettingsJson { get; set; }

    /// <summary>The supplier group whose attributes and settings the supplier inherits (supplier overrides group).</summary>
    public Guid? GroupId { get; set; }

    public bool AlwaysReview { get; set; }
    public Guid? GlobalSupplierId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Tenant? Tenant { get; set; }
    public GlobalSupplier? GlobalSupplier { get; set; }
    public SupplierGroup? Group { get; set; }
}

/// <summary>
/// Suppliers that share attributes and settings (docs/11 §6.4 <c>SupplierGroup</c>): the branches of one supplier
/// group, or many suppliers with one ERP convention. A supplier inherits its group's attributes and settings; its own
/// override them.
/// </summary>
public sealed class SupplierGroup : ITenantScoped
{
    public const int MaxNameLength = 200;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }

    /// <summary>The attributes every member inherits, a JSON object of scalars; null for none.</summary>
    public string? AttributesJson { get; set; }

    /// <summary>The settings every member inherits (the <c>SupplierSettings</c> shape); null for none.</summary>
    public string? SettingsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The tenant's own company or branch a document is addressed to (docs/11 §6.4 <c>ReceivingEntity</c>): resolved from
/// a source rule (one mailbox per company), the channel default, or the customer VAT id printed on the document. The
/// values view's <c>entity</c> block reads it.
/// </summary>
public sealed class ReceivingEntity : ITenantScoped
{
    public const int MaxCodeLength = 64;
    public const int MaxNameLength = 300;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The company code the tenant's systems know it by (<c>1000</c>), unique per tenant.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    /// <summary>The entity's VAT ids, compacted; the first is the one the values view shows.</summary>
    public List<string> VatIds { get; set; } = [];

    /// <summary>Address lines as JSON (an object or an array); rarely queried.</summary>
    public string? AddressesJson { get; set; }

    /// <summary>The entity's own attributes, a JSON object of scalars; null for none.</summary>
    public string? AttributesJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A supplier's attributes in one receiving entity (docs/11 §6.4 <c>SupplierEntityAttributes</c>): the vendor number in
/// company 1000 can differ from company 2000. The values view merges them over the supplier's attributes.
/// </summary>
public sealed class SupplierEntityAttributes : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid ReceivingEntityId { get; set; }

    /// <summary>A JSON object of scalars.</summary>
    public required string AttributesJson { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Supplier? Supplier { get; set; }
    public ReceivingEntity? ReceivingEntity { get; set; }
}

/// <summary>How a source rule's supplier relates to content evidence (docs/11 §6.5, §7.4, decision 1 of §24).</summary>
public enum SourceRuleMode
{
    /// <summary>Authoritative: content evidence for another supplier keeps the rule's supplier and raises a blocking <c>SUPPLIER_CONFLICT</c>.</summary>
    Exclusive,

    /// <summary>A default: VAT or IBAN evidence for another supplier replaces it.</summary>
    DefaultWhenNoContentMatch,
}

/// <summary>
/// A set of conditions on the source of an ingestion that names the supplier (docs/11 §6.5): all of them must hold.
/// The condition values are stored normalised (addresses and domains lower-cased, a folder prefix with <c>/</c>
/// separators and a trailing <c>/</c>), so the resolver looks candidates up by equality on the indexed columns.
/// <see cref="ConditionHash"/> is the SHA-256 of the normalised conditions; it is unique per tenant, so two rules
/// (of one supplier or of two) cannot claim identical conditions.
/// </summary>
public sealed class SupplierSourceRule : ITenantScoped
{
    public const int MaxAddressLength = 320;
    public const int MaxDomainLength = 253;
    public const int MaxFolderPrefixLength = 1024;
    public const int MaxRegexLength = 400;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SupplierId { get; set; }

    /// <summary>Condition: the ingestion came through this channel.</summary>
    public Guid? ChannelId { get; set; }

    /// <summary>Condition: the mailbox or one of the recipients is this address (an alias such as <c>nordwind@tenant-a.local</c>).</summary>
    public string? Recipient { get; set; }

    /// <summary>Condition: the sender is exactly this address.</summary>
    public string? Sender { get; set; }

    /// <summary>Condition: the sender's domain is this domain.</summary>
    public string? SenderDomain { get; set; }

    /// <summary>Condition: the folder path (relative to the channel root) starts with this prefix.</summary>
    public string? FolderPrefix { get; set; }

    /// <summary>Condition: the subject matches (non-backtracking, 100 ms).</summary>
    public string? SubjectRegex { get; set; }

    /// <summary>Condition: the file name of the item matches (non-backtracking, 100 ms).</summary>
    public string? FileNameRegex { get; set; }

    public SourceRuleMode Mode { get; set; } = SourceRuleMode.Exclusive;

    /// <summary>Whether a sender condition needs an authenticated sender (DMARC pass, or SPF and aligned DKIM); default true for sender rules (decision 1).</summary>
    public bool RequireSenderAuth { get; set; } = true;

    /// <summary>Tie-break after specificity; higher wins.</summary>
    public int Priority { get; set; }

    /// <summary>A type the rule also decides (<c>TypeSource=Supplier</c>), so Classify and Split leave it.</summary>
    public string? PinTypeKey { get; set; }

    public Guid? ReceivingEntityId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>The document a reviewer remembered the rule from (<c>:assign-supplier</c> with <c>remember</c>).</summary>
    public Guid? CreatedFromDocumentId { get; set; }

    /// <summary>Hex SHA-256 of the normalised conditions (see the class summary).</summary>
    public required string ConditionHash { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Supplier? Supplier { get; set; }
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
