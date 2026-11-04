namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>
/// An approved document kept as a regression case (docs/11 §6.9 <c>GoldenDocument</c>, §15.3): the values it was approved
/// with (a snapshot of its values view) and, when outputs were rendered for it, each binding's payload hash. The field
/// replay of a layout version adds the golden documents of its supplier and type to its replay set (§8.3), and a
/// regression run compares a candidate configuration's values with the snapshot.
/// </summary>
public sealed class GoldenDocument : ITenantScoped
{
    public const int MaxNoteLength = 500;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }

    /// <summary>The document's type when it was marked.</summary>
    public required string TypeKey { get; set; }

    public Guid? SupplierId { get; set; }

    /// <summary>The layout version that identified the document, if one did.</summary>
    public Guid? SupplierLayoutVersionId { get; set; }

    /// <summary>The values the document was approved with: the stored tree's value per field path (an object).</summary>
    public required string ExpectedValuesJson { get; set; }

    /// <summary><c>{bindingId: payloadSha256}</c> of the deliveries rendered for it (an object), or null.</summary>
    public string? ExpectedOutputsJson { get; set; }

    public string? Note { get; set; }
    public string? AddedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    public Document? Document { get; set; }
}

/// <summary>What a <see cref="RegressionRun"/> ran.</summary>
public enum RegressionRunKind
{
    /// <summary>One document through a draft configuration, without side effects (docs/11 §15.3 "end-to-end sandbox").</summary>
    Sandbox,

    /// <summary>The golden documents of a supplier and type through a candidate layout version (docs/11 §15.3).</summary>
    Regression,
}

/// <summary>
/// A sandbox or regression run (docs/11 §6.9 <c>RegressionRun</c>, §15.3): what was run (<see cref="Trigger"/>, the
/// candidate configuration), and the trace or report it produced. Nothing a run does is written anywhere else.
/// </summary>
public sealed class RegressionRun : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public RegressionRunKind Kind { get; set; }

    /// <summary><c>manual</c>, <c>layoutVersion</c> or <c>sandbox</c> (§6.9 <c>Trigger</c>).</summary>
    public required string Trigger { get; set; }

    /// <summary>The document a sandbox run ran on; null for a regression run.</summary>
    public Guid? DocumentId { get; set; }

    /// <summary>The configuration under test (an object): a layout version, or a draft rule and mapping.</summary>
    public required string CandidateConfigJson { get; set; }

    /// <summary>The trace or report (an object).</summary>
    public required string ResultJson { get; set; }

    /// <summary>Whether every check of the run passed.</summary>
    public bool Passed { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Something a tenant's members holding a permission should act on (docs/11 §8.4 step 5, §12.10 step 6: "the tenant's
/// <c>MappingAuthor</c>s are notified"): documents waiting for a Candidate layout's activation. One row per subject
/// (<see cref="DedupeKey"/>), counted up while it is open and resolved when the subject is dealt with.
/// </summary>
public sealed class Notification : ITenantScoped
{
    public const int MaxTitleLength = 200;
    public const int MaxMessageLength = 1000;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The permission whose holders see it (<c>MappingAuthor</c>).</summary>
    public required string Audience { get; set; }

    /// <summary><c>layout.candidate_waiting</c>.</summary>
    public required string Kind { get; set; }

    public required string Title { get; set; }
    public required string Message { get; set; }

    /// <summary>What it is about (<c>SupplierLayoutVersion</c>) and its id.</summary>
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }

    /// <summary>One notification per subject (<c>layout-candidate:{versionId}</c>), unique per tenant.</summary>
    public required string DedupeKey { get; set; }

    /// <summary>How often it was raised (documents waiting).</summary>
    public int Count { get; set; } = 1;

    /// <summary>Details (an object): the layout key, supplier, documents.</summary>
    public string? PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}
