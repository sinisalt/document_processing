using DocProc.Domain.Documents;
using DocProc.Domain.Flags;

namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>Tokens and money spent on one document (docs/02 §2 "costs").</summary>
public sealed class DocumentCosts
{
    public int LlmInputTokens { get; set; }
    public int LlmOutputTokens { get; set; }
    public int LlmCacheReadTokens { get; set; }
    public int OcrPages { get; set; }
    public decimal? EstimatedUsd { get; set; }
    public string? LlmProvider { get; set; }
    public string? LlmModel { get; set; }
    public KeyOwner? KeyOwner { get; set; }
}

/// <summary>
/// One logical business document (docs/02 §2). The canonical data lives in <see cref="CanonicalJson"/>; a few
/// values are denormalised into columns for listing, filtering and duplicate detection. Temporal, so every
/// change is auditable.
/// </summary>
public sealed class Document : ITenantScoped, IVersioned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid IngestionItemId { get; set; }
    public int Version { get; set; }
    public int PageStart { get; set; } = 1;
    public int PageEnd { get; set; } = 1;
    public int PageCount { get; set; } = 1;

    public BlobReference? NormalizedBlob { get; set; }

    /// <summary>Blob keys of the page PNGs, in page order.</summary>
    public List<string> PageImageKeys { get; set; } = [];

    public BlobReference? TextLayerBlob { get; set; }

    /// <summary>
    /// The structured e-invoice XML stage 2 found: an attachment inside a PDF/A-3 (Factur-X, ZUGFeRD) stored in the
    /// derived container, or the ingestion item itself when the item is a UBL / CII file. Stage 6a reads it instead
    /// of the pages (docs/03 "Unpack and normalize": embedded structured data first).
    /// </summary>
    public BlobReference? EmbeddedXmlBlob { get; set; }

    public DocumentTypeKey Type { get; set; } = DocumentTypeKey.Unknown;

    /// <summary>
    /// The type version the document was processed with (docs/11 §5.7, §6.2): pinned by the Extract stage from the
    /// tenant's published version of <see cref="Type"/>, back-filled for the documents before the catalogue (the
    /// adopted v1). Null while the type is Unknown, and for a type without a catalogue entry.
    /// </summary>
    public Guid? DocumentTypeVersionId { get; set; }

    public double? TypeConfidence { get; set; }
    public TypeSource? TypeSource { get; set; }

    public Guid? SupplierId { get; set; }
    public double? SupplierMatchConfidence { get; set; }
    public Guid? ExtractorProfileId { get; set; }
    public ExtractionMethod? ExtractionMethod { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Received;
    public string? FailureReason { get; set; }

    /// <summary>The canonical Invoice / PurchaseOrder / DeliveryNote as JSON (see <c>CanonicalDocumentSerializer</c>).</summary>
    public string? CanonicalJson { get; set; }

    public int? CanonicalSchemaVersion { get; set; }

    // Denormalised for lists, search and DUPLICATE_SUSPECTED; always rewritten from CanonicalJson.
    public string? DocumentNumber { get; set; }
    public string? DocumentNumberNormalized { get; set; }
    public DateOnly? IssueDate { get; set; }
    public string? Currency { get; set; }
    public decimal? TotalGross { get; set; }

    public DocumentCosts Costs { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }

    public IngestionItem? IngestionItem { get; set; }
    public Supplier? Supplier { get; set; }
    public ExtractorProfile? ExtractorProfile { get; set; }
    public DocumentTypeVersion? DocumentTypeVersion { get; set; }
    public ICollection<DocumentFlagRecord> Flags { get; } = [];
    public ICollection<DocumentEvent> Events { get; } = [];
    public ICollection<ReviewTask> ReviewTasks { get; } = [];
    public ICollection<Correction> Corrections { get; } = [];
}

/// <summary>Persisted <see cref="DocumentFlag"/>.</summary>
public sealed class DocumentFlagRecord : ITenantScoped
{
    /// <summary>The column sizes of <see cref="Message"/> and <see cref="Field"/> (DocumentFlagConfiguration).</summary>
    public const int MaxMessageLength = 1000;
    public const int MaxFieldLength = 200;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public required string Code { get; set; }
    public FlagSeverity Severity { get; set; }
    public string? Field { get; set; }
    public required string Message { get; set; }
    public FlagOrigin CreatedBy { get; set; } = FlagOrigin.Pipeline;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }

    /// <summary>
    /// <see cref="DocumentFlag.Params"/> as a json column; null for a flag without parameters (and for every flag
    /// written before the column existed, which a UI then shows with its <see cref="Message"/>).
    /// </summary>
    public Dictionary<string, string>? Params { get; set; }

    public Document? Document { get; set; }

    public DocumentFlag ToDomain() => new(Code, Severity, Message, Field, CreatedBy, ResolvedAt, Params);

    /// <summary>
    /// The record of a flag, with the message and field cut to their columns: messages quote model output (an
    /// unreadable region's reason, an invalid currency), which has no length bound, and an over-long value would fail
    /// the stage's save on SQL Server.
    /// </summary>
    public static DocumentFlagRecord FromDomain(Guid tenantId, Guid documentId, DocumentFlag flag, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        DocumentId = documentId,
        Code = flag.Code,
        Severity = flag.Severity,
        Field = Truncate(flag.Field, MaxFieldLength),
        Message = Truncate(flag.Message, MaxMessageLength)!,
        CreatedBy = flag.CreatedBy,
        CreatedAt = at,
        ResolvedAt = flag.ResolvedAt,
        Params = flag.Params is { Count: > 0 } parameters
            ? parameters.ToDictionary(p => p.Key, p => Truncate(p.Value, MaxParamLength)!, StringComparer.Ordinal)
            : null,
    };

    /// <summary>A parameter value is cut like the message: some quote model output (an unreadable region's reason).</summary>
    public const int MaxParamLength = MaxMessageLength;

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}

/// <summary>Append-only timeline (docs/03 "Document state machine"): state transitions, stage outcomes, human actions.</summary>
public sealed class DocumentEvent : ITenantScoped
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public required string EventType { get; set; }
    public DocumentStatus? FromStatus { get; set; }
    public DocumentStatus? ToStatus { get; set; }
    public string? Actor { get; set; }
    public string? DetailsJson { get; set; }
    public DateTimeOffset At { get; set; }

    public Document? Document { get; set; }
}

public enum ReviewTaskStatus
{
    Open,
    InProgress,
    Done,
    Skipped,
}

public sealed class ReviewTask : ITenantScoped, IVersioned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public int Version { get; set; }

    /// <summary>Flag summary shown in the queue.</summary>
    public required string Reason { get; set; }

    public string? AssignedTo { get; set; }
    public ReviewTaskStatus Status { get; set; } = ReviewTaskStatus.Open;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SlaDueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public Document? Document { get; set; }
}

/// <summary>Append-only human correction; feeds profile learning and evals (docs/02 §6).</summary>
public sealed class Correction : ITenantScoped
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public required string FieldPath { get; set; }

    /// <summary>The correction operation (docs/11 §6.2): <c>set</c> today; <c>append</c>, <c>remove</c>, <c>move</c> and the review actions later.</summary>
    public string Op { get; set; } = "set";

    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }

    /// <summary>
    /// Where the reviewer took the value from, as a JSON object (<c>{method, page, bBox, rawText, words, path, cell}</c>,
    /// docs/11 §6.2); null when the correction named no source, and for every row written before the column existed.
    /// </summary>
    public string? SourceJson { get; set; }

    /// <summary>The type version of the document when the correction was made (docs/11 §6.2); null for older rows without one.</summary>
    public Guid? DocumentTypeVersionId { get; set; }

    public required string By { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Reason { get; set; }

    public Document? Document { get; set; }
}

/// <summary>
/// Outbox row of docs/05 "Events out" and docs/03 stage 9: one row per published document event, written in the
/// same transaction as the decision that settles the document (approve, reject, auto-approval; docs/11 §13.5), so a
/// lost queue message can never lose the event. <see cref="PayloadJson"/> is the serialised <c>DocumentEventMessage</c>.
/// </summary>
public sealed class OutboxMessage : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public required string EventType { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Deprecated (docs/11 §6.2): never set; deliveries will track their own state.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>
    /// When the publish stage handed the row on (stage 9; in P0 there are no destinations yet, so this is the publish
    /// itself). Null means the decision committed but the publish has not run: the publish message was lost or is still
    /// queued, and <c>PublishSweeper</c> picks the row up. A concurrency token, so two publishers cannot both claim it.
    /// Rows written before the column existed were published when they were written and carry their CreatedAt.
    /// </summary>
    public DateTimeOffset? FannedOutAt { get; set; }

    public Document? Document { get; set; }
}
