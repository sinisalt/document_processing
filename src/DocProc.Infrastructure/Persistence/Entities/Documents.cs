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
public sealed class Document : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid IngestionItemId { get; set; }
    public int PageStart { get; set; } = 1;
    public int PageEnd { get; set; } = 1;
    public int PageCount { get; set; } = 1;

    public BlobReference? NormalizedBlob { get; set; }

    /// <summary>Blob keys of the page PNGs, in page order.</summary>
    public List<string> PageImageKeys { get; set; } = [];

    public BlobReference? TextLayerBlob { get; set; }

    public DocumentType Type { get; set; } = DocumentType.Unknown;
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
    public ICollection<DocumentFlagRecord> Flags { get; } = [];
    public ICollection<DocumentEvent> Events { get; } = [];
    public ICollection<ReviewTask> ReviewTasks { get; } = [];
    public ICollection<Correction> Corrections { get; } = [];
}

/// <summary>Persisted <see cref="DocumentFlag"/>.</summary>
public sealed class DocumentFlagRecord : ITenantScoped
{
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

    public Document? Document { get; set; }

    public DocumentFlag ToDomain() => new(Code, Severity, Message, Field, CreatedBy, ResolvedAt);

    public static DocumentFlagRecord FromDomain(Guid tenantId, Guid documentId, DocumentFlag flag, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        DocumentId = documentId,
        Code = flag.Code,
        Severity = flag.Severity,
        Field = flag.Field,
        Message = flag.Message,
        CreatedBy = flag.CreatedBy,
        CreatedAt = at,
        ResolvedAt = flag.ResolvedAt,
    };
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

public sealed class ReviewTask : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }

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
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public required string By { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Reason { get; set; }

    public Document? Document { get; set; }
}
