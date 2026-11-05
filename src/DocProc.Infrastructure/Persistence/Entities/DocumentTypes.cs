using DocProc.Application.Configuration;

namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>Whether a type is offered for new documents (docs/11 §6.1).</summary>
public enum DocumentTypeStatus
{
    Active,
    Archived,
}

/// <summary>The life of one version of a type (docs/11 §6.1, §5.7).</summary>
public enum DocumentTypeVersionState
{
    /// <summary>Editable; the only state a version can be changed in.</summary>
    Draft,

    /// <summary>Waiting for a second person (four-eyes, docs/11 §15.2; not used before the governance phase).</summary>
    PendingApproval,

    /// <summary>The version documents of the type resolve to now. Immutable.</summary>
    Published,

    /// <summary>A version that was published and has been replaced; documents pinned to it keep it. Immutable.</summary>
    Deprecated,
}

/// <summary>
/// One document type of a tenant, or a platform template (<see cref="TenantId"/> null), in the type catalogue (docs/11
/// §6.1). <see cref="Key"/> is the <c>Documents.Type</c> value of its documents: the legacy name for the adopted
/// standard types (<c>Invoice</c>), the tenant's own key otherwise (<c>price-list</c>), the template id for a template
/// (<c>std.invoice</c>). Every change is a new <see cref="DocumentTypeVersion"/>; the row itself holds what is not
/// versioned: the key, the origin and which version is published.
/// </summary>
public sealed class DocumentTypeDefinition
{
    public Guid Id { get; set; }

    /// <summary>Null for a platform template, which every tenant can read and none can change.</summary>
    public Guid? TenantId { get; set; }

    public required string Key { get; set; }

    /// <summary>The display name by locale, copied from the published version for lists.</summary>
    public Dictionary<string, string> Name { get; set; } = [];

    public string? Description { get; set; }

    public DefinitionOrigin Origin { get; set; }

    /// <summary>The platform template an adopted or copied type comes from.</summary>
    public Guid? BaseDefinitionId { get; set; }

    /// <summary>The template version an adopted type follows, or a copy was made from.</summary>
    public int? BaseVersion { get; set; }

    public DocumentTypeStatus Status { get; set; } = DocumentTypeStatus.Active;

    /// <summary>The version documents resolve to; null until the first publish.</summary>
    public int? PublishedVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DocumentTypeDefinition? BaseDefinition { get; set; }

    public ICollection<DocumentTypeVersion> Versions { get; } = [];
}

/// <summary>
/// One version of a type (docs/11 §6.1): the definition JSON and, once published, its compiled artifacts (§5.5).
/// Drafts are versions in <see cref="DocumentTypeVersionState.Draft"/>, edited with <see cref="Revision"/> as the
/// If-Match token; published and deprecated versions never change. Temporal, so every change is auditable.
/// </summary>
public sealed class DocumentTypeVersion
{
    public Guid Id { get; set; }

    public Guid DefinitionId { get; set; }

    /// <summary>The definition's tenant, repeated so row-level security can filter this table on its own.</summary>
    public Guid? TenantId { get; set; }

    public int Version { get; set; }

    /// <summary>The definition, as saved (a draft) or as published (normalised by the strict writer).</summary>
    public required string DefinitionJson { get; set; }

    /// <summary>JSON Schema 2020-12 over the values view; null on a draft.</summary>
    public string? CompiledJsonSchema { get; set; }

    /// <summary>The LLM structured-output schema; null on a draft.</summary>
    public string? CompiledLlmSchema { get; set; }

    /// <summary>The schema name sent to the provider (<c>doc_price-list</c>, <c>invoice_extraction</c>).</summary>
    public string? LlmSchemaName { get; set; }

    /// <summary>The schema version sent to the provider (the hash, or <c>1</c> / <c>1+x.&lt;hash8&gt;</c> for the invoice adapter).</summary>
    public string? LlmSchemaVersion { get; set; }

    /// <summary>The first 16 hex digits of the SHA-256 of <see cref="CompiledLlmSchema"/>.</summary>
    public string? LlmSchemaHash { get; set; }

    public string? FormSpecJson { get; set; }

    public string? DescriptorsJson { get; set; }

    /// <summary>The SHA-256 of the RFC 8785 canonical form of the definition; null on a draft.</summary>
    public string? ContentHash { get; set; }

    public DocumentTypeVersionState State { get; set; } = DocumentTypeVersionState.Draft;

    /// <summary>Grows by one with every saved draft change: the ETag a draft edit sends back as If-Match.</summary>
    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public string? PublishedBy { get; set; }

    public string? ChangeNote { get; set; }

    public DocumentTypeDefinition? Definition { get; set; }
}
