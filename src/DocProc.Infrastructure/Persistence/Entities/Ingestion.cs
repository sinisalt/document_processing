namespace DocProc.Infrastructure.Persistence.Entities;

public enum IngestionStatus
{
    Received,
    Unpacked,
    Processed,
    Failed,
}

/// <summary>One email, one dropped file or one API call (docs/02 §2).</summary>
public sealed class Ingestion : ITenantScoped, IVersioned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ChannelId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? SourceMessageId { get; set; }
    public string? SourceFrom { get; set; }
    public string? SourceSubject { get; set; }
    public string? SourceFileName { get; set; }

    /// <summary>The original .eml or file, immutable.</summary>
    public required BlobReference RawBlob { get; set; }

    public IngestionStatus Status { get; set; } = IngestionStatus.Received;
    public string? LastError { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Tenant? Tenant { get; set; }
    public IngestionChannel? Channel { get; set; }
    public ICollection<IngestionItem> Items { get; } = [];
}

public enum ItemKind
{
    Pdf,
    Image,
    Xml,
    Office,
    Text,
    Archive,
    Other,
}

public enum ItemDisposition
{
    Candidate,
    Ignored,
    Unpacked,
    Duplicate,
}

/// <summary>One attachment or body part.</summary>
public sealed class IngestionItem : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid IngestionId { get; set; }

    /// <summary>Set when this item was extracted from an archive.</summary>
    public Guid? ParentItemId { get; set; }

    public required string FileName { get; set; }
    public required string MimeType { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Hex SHA-256; with the tenant id it identifies exact duplicates.</summary>
    public required string Sha256 { get; set; }

    public required BlobReference Blob { get; set; }
    public ItemKind Kind { get; set; } = ItemKind.Other;
    public ItemDisposition Disposition { get; set; } = ItemDisposition.Candidate;
    public string? DispositionReason { get; set; }

    /// <summary>For duplicates: the earlier item with the same hash.</summary>
    public Guid? DuplicateOfItemId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Ingestion? Ingestion { get; set; }
    public ICollection<Document> Documents { get; } = [];
}
