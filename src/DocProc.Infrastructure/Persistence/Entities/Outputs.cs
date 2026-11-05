namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>The transports of docs/11 §13.3. The first release has <see cref="Http"/>, <see cref="Blob"/> and <see cref="Pull"/> (decision 4 of §24).</summary>
public static class OutputTransports
{
    public const string Http = "http";
    public const string Blob = "blob";
    public const string Pull = "pull";

    /// <summary>P6.</summary>
    public const string Sftp = "sftp";

    /// <summary>P6.</summary>
    public const string Email = "email";

    /// <summary>The transports a destination can be created with in this build.</summary>
    public static readonly IReadOnlyList<string> Available = [Http, Blob, Pull];
}

/// <summary>Whether a destination receives deliveries.</summary>
public enum OutputDestinationStatus
{
    Active,
    Disabled,
}

/// <summary>
/// Where a tenant's outputs go (docs/11 §6.7, §13.3): a transport with its configuration. Secrets never sit in
/// <see cref="ConfigJson"/>: <see cref="SecretRef"/> names the secret in <c>ISecretStore</c> (the channel and BYOK
/// pattern), write-only through the API; <see cref="PreviousSecretRef"/> keeps the secret it replaced while a rotation
/// overlaps (callbacks, P5).
/// </summary>
public sealed class OutputDestination : ITenantScoped, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public int Version { get; set; }

    public required string Name { get; set; }

    /// <summary>One of <see cref="OutputTransports"/>.</summary>
    public required string Transport { get; set; }

    /// <summary>The transport's settings, checked against its schema (a JSON object, never a secret).</summary>
    public string ConfigJson { get; set; } = "{}";

    public string? SecretRef { get; set; }

    public string? PreviousSecretRef { get; set; }

    public DateTimeOffset? SecretSetAt { get; set; }

    /// <summary>
    /// The destination's acknowledgements (docs/11 §13.8, P5): <c>{callback?: {auth, mapping?}, statusPoll?: {urlTemplate,
    /// intervalMinutes, mapping?, maxPolls?}}</c> (<c>AcknowledgementConfig</c>); null when the receiver reports nothing back.
    /// </summary>
    public string? CallbackConfigJson { get; set; }

    /// <summary>The secret store reference of the callback secret (an HMAC key or a bearer token); null until one is set.</summary>
    public string? CallbackSecretRef { get; set; }

    /// <summary>The callback secret it replaced, still accepted until the next rotation (two active secrets, docs/11 §13.8).</summary>
    public string? PreviousCallbackSecretRef { get; set; }

    public OutputDestinationStatus Status { get; set; } = OutputDestinationStatus.Active;

    public DateTimeOffset? LastTestAt { get; set; }

    public string? LastTestResult { get; set; }

    /// <summary>
    /// The last sequence number a <c>pull</c> destination's feed handed out (docs/11 §13.3, P3 part B): every delivery
    /// that enters the feed takes the next one in the transaction that publishes it, so the feed's order is the order
    /// its entries were committed in and a reader's <c>after</c> cursor never skips one committed later.
    /// </summary>
    public long FeedSequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Where an output mapping comes from.</summary>
public enum OutputMappingOrigin
{
    /// <summary>A platform integration template (<c>TenantId</c> NULL).</summary>
    Platform,

    /// <summary>A tenant's copy of a platform template.</summary>
    Copied,

    /// <summary>A tenant's own mapping.</summary>
    Custom,
}

public enum OutputMappingStatus
{
    Active,
    Archived,
}

/// <summary>The life of one mapping version: a draft is edited, a published version never changes.</summary>
public enum OutputMappingVersionState
{
    Draft,
    Published,
    Deprecated,
}

/// <summary>The engines of docs/11 §6.7: <see cref="Jsonata"/> and <see cref="Passthrough"/> in P3; <c>fieldmap</c> (compiled to JSONata, with the visual mapper) and <c>liquid</c> (P6) later.</summary>
public static class OutputEngines
{
    public const string Jsonata = "jsonata";

    /// <summary>The values view itself, unchanged.</summary>
    public const string Passthrough = "passthrough";

    public static readonly IReadOnlyList<string> Available = [Jsonata, Passthrough];
}

/// <summary>
/// An output mapping (docs/11 §6.7): the shape a receiver wants, as versions of a JSONata expression over the values
/// view. Platform integration templates (<c>std.event</c>, <c>generic-ap-json</c>, §13.9) have <see cref="TenantId"/>
/// NULL; every tenant reads them and copies them, none changes them.
/// </summary>
public sealed class OutputMapping
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public required string Key { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>The document type the mapping is written for; null for any type.</summary>
    public string? SourceTypeKey { get; set; }

    public OutputMappingOrigin Origin { get; set; }

    /// <summary>The platform template a copy comes from.</summary>
    public Guid? BaseMappingId { get; set; }

    public int? BaseVersion { get; set; }

    public OutputMappingStatus Status { get; set; } = OutputMappingStatus.Active;

    /// <summary>The version bindings render with (unless they pin one); null until the first publish.</summary>
    public int? PublishedVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<OutputMappingVersion> Versions { get; } = [];
}

/// <summary>One version of an output mapping: the engine, the expression, the content type and the receiver's JSON Schema.</summary>
public sealed class OutputMappingVersion
{
    public Guid Id { get; set; }

    public Guid MappingId { get; set; }

    /// <summary>The mapping's tenant, repeated so row-level security can filter this table on its own.</summary>
    public Guid? TenantId { get; set; }

    public int Version { get; set; }

    /// <summary>One of <see cref="OutputEngines"/>.</summary>
    public required string Engine { get; set; }

    /// <summary>The JSONata expression; empty for <see cref="OutputEngines.Passthrough"/>.</summary>
    public string Expression { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/json";

    /// <summary>The file name of a blob drop (P3 part B), with <c>{{...}}</c> placeholders.</summary>
    public string? FileNameTemplate { get; set; }

    /// <summary>The receiver's JSON Schema; every render is checked against it (docs/11 §13.2).</summary>
    public string? TargetSchemaJson { get; set; }

    /// <summary>The values-view paths the expression reads (a JSON array), from the scan of its syntax tree.</summary>
    public string ReferencedPathsJson { get; set; } = "[]";

    /// <summary>Documents the editor previews the mapping on (a JSON array of ids).</summary>
    public string SampleDocumentIdsJson { get; set; } = "[]";

    public OutputMappingVersionState State { get; set; } = OutputMappingVersionState.Draft;

    /// <summary>Grows with every saved draft change: the If-Match token.</summary>
    public int Revision { get; set; }

    public string? ChangeNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public string? PublishedBy { get; set; }

    public OutputMapping? Mapping { get; set; }
}

/// <summary>The events a binding fires on (docs/11 §6.7).</summary>
public static class OutputEvents
{
    public const string Approved = "document.approved";
    public const string Rejected = "document.rejected";
    public const string Updated = "document.updated";
    public const string NeedsReview = "document.needs_review";

    public static readonly IReadOnlyList<string> All = [Approved, Rejected, Updated, NeedsReview];

    /// <summary>
    /// The events a binding fires on in this build (P3): the settlements the publish stage fans out. <see cref="Updated"/>
    /// is the event a re-approval under <see cref="OnReapproval.Update"/> renders with, not a trigger of its own;
    /// <see cref="NeedsReview"/> comes later.
    /// </summary>
    public static readonly IReadOnlyList<string> Fired = [Approved, Rejected];
}

public enum OutputBindingMode
{
    PerDocument,

    /// <summary>Batches (docs/11 §13.7) come in P6.</summary>
    Batch,
}

/// <summary>What a re-approval after a reprocess sends (docs/11 §13.6).</summary>
public enum OnReapproval
{
    Resend,
    Skip,
    Update,
}

/// <summary>
/// Which documents go where in which shape (docs/11 §6.7, §13.5): events, a scope (type, supplier, group, channel,
/// receiving entity, a JSONata condition), a mapping and a destination. Bindings without an exclusive group all fire;
/// within one the most specific wins.
/// </summary>
public sealed class OutputBinding : ITenantScoped, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public int Version { get; set; }

    public required string Name { get; set; }

    /// <summary>Some of <see cref="OutputEvents"/>.</summary>
    public List<string> Events { get; set; } = [OutputEvents.Approved];

    public string? DocumentTypeKey { get; set; }

    public Guid? SupplierId { get; set; }

    /// <summary>A supplier group (P4); kept without a reference until groups exist.</summary>
    public Guid? GroupId { get; set; }

    public Guid? ChannelId { get; set; }

    /// <summary>A receiving entity (P4); kept without a reference until entities exist.</summary>
    public Guid? ReceivingEntityId { get; set; }

    /// <summary>A JSONata boolean over the values view; the binding fires only when it is true.</summary>
    public string? Condition { get; set; }

    public Guid MappingId { get; set; }

    /// <summary>A mapping version the binding keeps to; null follows the published version.</summary>
    public int? PinnedMappingVersion { get; set; }

    public Guid DestinationId { get; set; }

    /// <summary>Multi-call recipes (docs/11 §13.4, P6); null until then.</summary>
    public string? RecipeJson { get; set; }

    public OutputBindingMode Mode { get; set; } = OutputBindingMode.PerDocument;

    /// <summary>The batch window (docs/11 §13.7, P6); null until then.</summary>
    public string? BatchJson { get; set; }

    public string? ExclusiveGroup { get; set; }

    public int Priority { get; set; }

    public OnReapproval OnReapproval { get; set; } = OnReapproval.Resend;

    /// <summary>The mapping an <see cref="OnReapproval.Update"/> renders with.</summary>
    public Guid? UpdateMappingId { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public OutputMapping? Mapping { get; set; }

    public OutputDestination? Destination { get; set; }
}

/// <summary>The life of one delivery (docs/11 §6.7, §13.6).</summary>
public enum DeliveryStatus
{
    Pending,
    Sending,
    Delivered,
    Failed,
    DeadLettered,
    Skipped,
    Cancelled,
}

/// <summary>
/// One document version rendered for one binding (docs/11 §6.7, §13.5): the payload snapshot (in
/// <c>BlobContainers.Exports</c>, under <see cref="PayloadBlobKey"/>, with its SHA-256), what was rendered with what,
/// and the state of sending it. <see cref="IdempotencyKey"/> is <c>{documentId}:{version}:{bindingId}</c> and unique.
/// </summary>
public sealed class OutboxDelivery : ITenantScoped, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public int Version { get; set; }

    public Guid OutboxMessageId { get; set; }

    public Guid DocumentId { get; set; }

    public int DocumentVersion { get; set; }

    public Guid BindingId { get; set; }

    public Guid DestinationId { get; set; }

    public Guid MappingVersionId { get; set; }

    public required string IdempotencyKey { get; set; }

    /// <summary>
    /// The event the payload was rendered for (<c>meta.event</c>): <c>document.approved</c>, <c>document.rejected</c>,
    /// or <c>document.updated</c> for a re-approval under <see cref="OnReapproval.Update"/>, which an http destination
    /// sends with its update method and URL.
    /// </summary>
    public string Event { get; set; } = OutputEvents.Approved;

    /// <summary>The delivery this one sends again (<c>POST /deliveries/{id}:redeliver</c>); null for a fan-out's own.</summary>
    public Guid? RedeliveryOfId { get; set; }

    /// <summary>
    /// The entry's place in a <c>pull</c> destination's feed (<see cref="OutputDestination.FeedSequence"/>), set when
    /// the delivery is published to it; the feed's <c>after</c> cursor. Null for other transports and before then.
    /// </summary>
    public long? FeedSequence { get; set; }

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Recipes (P6): the step reached and the captured variables.</summary>
    public string? RecipeStateJson { get; set; }

    public int? LastStatusCode { get; set; }

    public string? LastError { get; set; }

    /// <summary>The snapshot of the rendered payload in <c>BlobContainers.Exports</c>.</summary>
    public string? PayloadBlobKey { get; set; }

    public string? PayloadSha256 { get; set; }

    public string? ContentType { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public OutboxMessage? OutboxMessage { get; set; }

    public OutputBinding? Binding { get; set; }

    public OutputDestination? Destination { get; set; }

    public OutputMappingVersion? MappingVersion { get; set; }

    public ICollection<DeliveryAttempt> AttemptLog { get; } = [];
}

/// <summary>One attempt at a delivery (docs/11 §6.7): its outcome, with a response excerpt of at most 4 KB.</summary>
public sealed class DeliveryAttempt : ITenantScoped
{
    public const int MaxExcerptLength = 4096;

    public long Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DeliveryId { get; set; }

    /// <summary>The recipe step (P6); null for a single call.</summary>
    public string? Step { get; set; }

    public DateTimeOffset At { get; set; }

    public int DurationMs { get; set; }

    public int? StatusCode { get; set; }

    /// <summary><c>delivered</c>, <c>retryable</c> or <c>permanent</c>, with the reason (docs/11 §13.6).</summary>
    public string? ErrorClass { get; set; }

    public string? ResponseExcerpt { get; set; }

    public OutboxDelivery? Delivery { get; set; }
}

public enum ExternalRefStatus
{
    Pending,
    Accepted,
    Posted,
    Rejected,
}

/// <summary>The receiving system's id of a delivered document (docs/11 §6.7, §13.3 <c>responseMapping</c>, §13.8).</summary>
public sealed class DocumentExternalRef : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    public Guid DestinationId { get; set; }

    public required string ExternalId { get; set; }

    public ExternalRefStatus Status { get; set; } = ExternalRefStatus.Pending;

    public string? Message { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the destination's status poll asks next (docs/11 §13.8); null when it does not poll or the status is final.</summary>
    public DateTimeOffset? NextPollAt { get; set; }

    /// <summary>How often the status was polled; polling stops at the destination's <c>maxPolls</c>.</summary>
    public int Polls { get; set; }

    /// <summary>
    /// The signed timestamp (<c>X-DocProc-Timestamp</c>, the receiver's clock) of the last signed callback applied to this
    /// reference; a signed callback older than it, or this one again, is not applied (docs/11 §13.8, Correction (P5) fix1).
    /// </summary>
    public DateTimeOffset? LastCallbackAt { get; set; }

    /// <summary>
    /// The signatures (hex HMAC, at most <see cref="CallbackSignatureLength"/> characters each, separated by a space) of
    /// every signed callback applied at <see cref="LastCallbackAt"/>, oldest first: a callback signed in that same second
    /// is applied only when its signature is not among them (docs/11 §13.8, Correction (P5) fix 2), and at most
    /// <see cref="MaxCallbackSignatures"/> are applied in one second.
    /// </summary>
    public string? LastCallbackSignatures { get; set; }

    /// <summary>The characters of a signature kept (a hex HMAC-SHA256 is 64).</summary>
    public const int CallbackSignatureLength = 64;

    /// <summary>The most signed callbacks applied to one reference in one second of their timestamps.</summary>
    public const int MaxCallbackSignatures = 16;

    /// <summary>The length of <see cref="LastCallbackSignatures"/>.</summary>
    public const int CallbackSignaturesLength = MaxCallbackSignatures * (CallbackSignatureLength + 1);
}

public enum LookupTableSource
{
    Manual,
    Import,
    Sync,
}

/// <summary>
/// A lookup table (docs/11 §6.1): codes from one system to another (<c>erp-uom</c>, <c>erp-credit-reason</c>), read
/// by <c>$table(key, value, default?)</c> in output mappings. Platform code lists have <see cref="TenantId"/> NULL.
/// </summary>
public sealed class LookupTable
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public required string Key { get; set; }

    public string? Description { get; set; }

    public bool CaseSensitive { get; set; }

    public LookupTableSource Source { get; set; } = LookupTableSource.Manual;

    /// <summary>Grows with every change of the table or its entries: the If-Match token of the entries.</summary>
    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<LookupEntry> Entries { get; } = [];
}

/// <summary>One entry of a lookup table, optionally valid from and to a date.</summary>
public sealed class LookupEntry
{
    public long Id { get; set; }

    public Guid TableId { get; set; }

    /// <summary>The table's tenant, repeated so row-level security can filter this table on its own.</summary>
    public Guid? TenantId { get; set; }

    public required string From { get; set; }

    public required string To { get; set; }

    /// <summary>Labels by locale (a JSON object), for pickers.</summary>
    public string? LabelsJson { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public LookupTable? Table { get; set; }
}
