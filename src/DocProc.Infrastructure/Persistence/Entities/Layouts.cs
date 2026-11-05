namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>Whose a layout is (docs/11 §6.6 <c>Scope</c>): one supplier's, or every member's of a supplier group without layouts of its own.</summary>
public enum SupplierLayoutScope
{
    Supplier,
    Group,
}

/// <summary>A layout is Active until a <c>MappingAuthor</c> retires it; nothing retires a layout by itself (docs/11 §8.4).</summary>
public enum SupplierLayoutStatus
{
    Active,
    Retired,
}

/// <summary>
/// The lifecycle of a layout version (docs/11 §6.6, §8.3): <see cref="Draft"/> while it is edited, <see cref="Candidate"/>
/// once saved (identification only feeds <c>pendingLayoutVersionId</c>), <see cref="Shadow"/> (evaluated for its stats,
/// never applied), <see cref="Active"/>, <see cref="Degraded"/> (identifies, but its field mapping drifted and the LLM
/// fills everything) and <see cref="Retired"/>.
/// </summary>
public enum SupplierLayoutVersionStatus
{
    Draft,
    Candidate,
    Shadow,
    Active,
    Degraded,
    Retired,
}

/// <summary>Where a layout version came from (docs/11 §6.6 <c>Origin</c>).</summary>
public enum SupplierLayoutOrigin
{
    Human,
    Review,
    NewLayoutReview,
    LlmProposed,
    Imported,
}

/// <summary>
/// One design of one document type of a supplier, or of a supplier group (docs/11 §6.6 <c>SupplierLayout</c>): an
/// identification rule plus a field mapping, versioned together in <see cref="SupplierLayoutVersion"/>. Several layouts
/// per supplier and type are Active at the same time (§8.4). <see cref="OwnerId"/> is the supplier's or the group's id,
/// so the layout key is unique per owner and type.
/// </summary>
public sealed class SupplierLayout : ITenantScoped
{
    public const int MaxKeyLength = 64;
    public const int MaxNameLength = 200;
    public const int DefaultPriority = 100;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public SupplierLayoutScope Scope { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? GroupId { get; set; }

    /// <summary>The supplier's id for a supplier layout, the group's for a group layout.</summary>
    public Guid OwnerId { get; set; }

    public required string DocumentTypeKey { get; set; }

    /// <summary>The layout's key (<c>nordwind-invoice-webshop</c>), unique per owner and type.</summary>
    public required string LayoutKey { get; set; }

    public required string Name { get; set; }

    /// <summary>The receiving entity the layout is restricted to; null for every entity.</summary>
    public Guid? ReceivingEntityId { get; set; }

    /// <summary>The item kinds the layout applies to (<c>pdf</c>, <c>image</c>, <c>einvoice-xml</c>); empty for every kind.</summary>
    public List<string> ItemKinds { get; set; } = [];

    /// <summary>Orders two layouts whose rules both hold (docs/11 §8.4 step 4); default 100.</summary>
    public int Priority { get; set; } = DefaultPriority;

    /// <summary>The version applied, by number; null while none is Active.</summary>
    public int? ActiveVersion { get; set; }

    /// <summary>The version run in Shadow, by number.</summary>
    public int? ShadowVersion { get; set; }

    public SupplierLayoutStatus Status { get; set; } = SupplierLayoutStatus.Active;

    public Guid? CreatedFromDocumentId { get; set; }

    /// <summary>When the layout last identified a document (feeds "not seen for 180 days", docs/11 §17).</summary>
    public DateTimeOffset? LastMatchedAt { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Supplier? Supplier { get; set; }
    public SupplierGroup? Group { get; set; }
    public ICollection<SupplierLayoutVersion> Versions { get; } = [];
}

/// <summary>
/// A version of a layout (docs/11 §6.6 <c>SupplierLayoutVersion</c>): its identification rule and field mapping, which
/// are immutable once out of Draft, its lifecycle, its review warm-up and its counters. Documents pin the version they
/// were identified by (<c>Documents.SupplierLayoutVersionId</c>).
/// </summary>
public sealed class SupplierLayoutVersion : ITenantScoped
{
    public const int HashLength = 8;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LayoutId { get; set; }
    public int Version { get; set; }

    /// <summary>The type version the field mapping was written against.</summary>
    public Guid? BuiltAgainstTypeVersionId { get; set; }

    public SupplierLayoutVersionStatus Status { get; set; } = SupplierLayoutVersionStatus.Draft;
    public SupplierLayoutOrigin Origin { get; set; } = SupplierLayoutOrigin.Human;

    /// <summary>The identification rule (docs/11 §8.5, <c>schemas/identification-rule.v1.json</c>); required for every version out of Draft.</summary>
    public required string IdentificationJson { get; set; }

    /// <summary>The first 8 hex characters of the rule's SHA-256.</summary>
    public required string IdentificationHash { get; set; }

    /// <summary>The field mapping (docs/11 §8.1).</summary>
    public required string MappingJson { get; set; }

    /// <summary>The paths the field mapping covers (an array).</summary>
    public string? CoverageJson { get; set; }

    /// <summary>The documents the version was built from (an array of ids).</summary>
    public string? SourceDocumentIdsJson { get; set; }

    /// <summary>The replay and identification test of docs/11 §8.3 (an object).</summary>
    public string? ReplayJson { get; set; }

    /// <summary>Page-1 tokens, page count and item kind, for <c>LayoutSuggester</c> only (an object).</summary>
    public string? SimilarityProfileJson { get; set; }

    /// <summary>
    /// How many documents under this version go to review after it becomes Active (docs/11 §6.6 <c>ReviewWarmup</c>):
    /// set at activation from <c>TenantSettings.LayoutReviewWarmup</c> (decision 2: 5 by default).
    /// </summary>
    public int ReviewWarmup { get; set; } = TenantSettings.DefaultLayoutReviewWarmup;

    /// <summary>How many documents the warm-up has counted so far; at <see cref="ReviewWarmup"/> it is over.</summary>
    public int WarmupSeen { get; set; }

    /// <summary>Documents the rule identified.</summary>
    public int Identified { get; set; }

    /// <summary>Documents the field mapping was applied to.</summary>
    public int Applied { get; set; }

    public int Agreed { get; set; }
    public int Corrected { get; set; }

    /// <summary>Consecutive applications that drifted (docs/11 §8.2); reset by one that did not.</summary>
    public int ConsecutiveFailures { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public SupplierLayout? Layout { get; set; }
}

/// <summary>A tenant fallback rule is Active until a <c>ConfigAdmin</c> disables it (docs/11 §6.6).</summary>
public enum TypeIdentificationRuleStatus
{
    Active,
    Disabled,
}

/// <summary>
/// The lifecycle of a tenant fallback rule version (docs/11 §6.6): <see cref="Draft"/> while edited and tested,
/// <see cref="PendingApproval"/> (reserved for <c>configApprovals.identificationRules</c>, four-eyes in P6),
/// <see cref="Active"/> (evaluated in <c>classify.identification</c>) and <see cref="Retired"/> (replaced by a later
/// version, or its rule disabled). Out of Draft a version never changes.
/// </summary>
public enum TypeIdentificationRuleVersionState
{
    Draft,
    PendingApproval,
    Active,
    Retired,
}

/// <summary>
/// A tenant fallback identification rule (docs/11 §6.6 <c>TypeIdentificationRule</c>, §8.5): a strict rule that
/// decides only the type, for documents whose supplier is unknown at Classify or has no layouts of its own or of its
/// group (and <c>layouts.required</c> is off). Versioned in <see cref="TypeIdentificationRuleVersion"/>; documents pin
/// the version that typed them (<c>Documents.IdentificationRuleVersionId</c>).
/// </summary>
public sealed class TypeIdentificationRule : ITenantScoped
{
    public const int MaxNameLength = 200;
    public const int DefaultPriority = 100;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The type the rule decides (a key of the tenant's types).</summary>
    public required string DocumentTypeKey { get; set; }

    public required string Name { get; set; }

    /// <summary>Orders Active rules of the same type only (the evidence when two hold); never decides between types.</summary>
    public int Priority { get; set; } = DefaultPriority;

    /// <summary>The Active version, by number; null while none is.</summary>
    public int? ActiveVersion { get; set; }

    public TypeIdentificationRuleStatus Status { get; set; } = TypeIdentificationRuleStatus.Active;

    /// <summary>The document the rule was created from in review ("use for all suppliers without own layouts").</summary>
    public Guid? CreatedFromDocumentId { get; set; }

    /// <summary>Documents the rule typed.</summary>
    public int Matched { get; set; }

    /// <summary>Documents it typed whose type a reviewer changed.</summary>
    public int Overridden { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<TypeIdentificationRuleVersion> Versions { get; } = [];
}

/// <summary>A version of a tenant fallback rule (docs/11 §6.6 <c>TypeIdentificationRuleVersion</c>), immutable once out of Draft.</summary>
public sealed class TypeIdentificationRuleVersion : ITenantScoped
{
    public const int HashLength = 8;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RuleId { get; set; }
    public int Version { get; set; }

    public TypeIdentificationRuleVersionState State { get; set; } = TypeIdentificationRuleVersionState.Draft;

    /// <summary>The identification rule (docs/11 §8.5) in tenant scope: no <c>supplierValue</c>.</summary>
    public required string RuleJson { get; set; }

    /// <summary>The first 8 hex characters of the rule's SHA-256.</summary>
    public required string RuleHash { get; set; }

    /// <summary>Suppliers whose documents the rule never types (an array of ids); null for none.</summary>
    public string? ExcludedSupplierIdsJson { get; set; }

    /// <summary>The save-time test against the last 500 approved documents of the tenant (an object); null until tested.</summary>
    public string? TestJson { get; set; }

    /// <summary>A Draft's edit counter, the If-Match of <c>PUT …/versions/{v}</c>.</summary>
    public int Revision { get; set; } = 1;

    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public TypeIdentificationRule? Rule { get; set; }
}
