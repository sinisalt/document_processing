using System.Text.Json;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Documents;

/// <summary>
/// One row of the inbox (docs/05 "Inbox"): the denormalised columns plus flag counts, never the canonical JSON.
/// <c>TenantId</c> / <c>TenantName</c> name the owning tenant, so a platform-scope list (the admin console's
/// cross-tenant documents list and global review queue) can show whose row it is. <c>ValidFrom</c>, <c>ValidUntil</c> and
/// <c>LineCount</c> are the denormalised roles of docs/11 §6.2 (a price list shows its validity and row count instead of a
/// total); <c>ReviewPriority</c> and <c>ReviewDueAt</c> are the open review task's priority and due time, null without one.
/// <c>Fields</c> holds the indexed field values the list request asked for with <c>fields=</c> (docs/11 §19, the inbox
/// columns of indexed custom fields), keyed by definition path, as <c>DocumentFieldIndex</c> stores them (identifiers and
/// strings normalised, numbers invariant, dates ISO); null when the request named none.
/// </summary>
public sealed record DocumentSummary(
    Guid Id,
    Guid TenantId,
    string? TenantName,
    DocumentStatus Status,
    DocumentTypeKey Type,
    double? TypeConfidence,
    Guid? SupplierId,
    string? SupplierName,
    string? DocumentNumber,
    DateOnly? IssueDate,
    string? Currency,
    decimal? TotalGross,
    ExtractionMethod? ExtractionMethod,
    int PageCount,
    int BlockingFlagCount,
    int WarningFlagCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ApprovedAt,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    int? LineCount,
    ReviewPriority? ReviewPriority,
    DateTimeOffset? ReviewDueAt)
{
    public IReadOnlyDictionary<string, string>? Fields { get; init; }
}

/// <summary>
/// The type version a document is shown with (docs/11 §18 <c>DocumentDetail.definition</c>): its key, version and
/// catalogue row, the display name by locale and the compiled form spec the review form renders (docs/11 §5.5, artifact
/// 3). Sent for definition-driven documents and for every document of a tenant with <c>configurableTypes</c> on.
/// </summary>
public sealed record DocumentDefinitionRef(string Key, int Version, Guid? VersionId, IReadOnlyDictionary<string, string> Name, JsonElement FormSpec);

/// <summary>
/// One flag of a document. <c>Message</c> is English for API clients; <c>Params</c> holds the same facts as invariant
/// strings (field paths, ISO dates, amounts and ratios with a dot, <c>reason</c> naming the message variant) so a UI can
/// phrase the flag in its own language from <c>Code</c> (docs/02 §5 "Flag parameters"). Empty for a flag without
/// parameters, and for flags stored before parameters existed: show <c>Message</c> then.
/// </summary>
public sealed record DocumentFlagDto(
    Guid Id,
    string Code,
    FlagSeverity Severity,
    string? Field,
    string Message,
    IReadOnlyDictionary<string, string> Params,
    FlagOrigin CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy);

public sealed record DocumentCostsDto(
    int LlmInputTokens,
    int LlmOutputTokens,
    int LlmCacheReadTokens,
    int OcrPages,
    decimal? EstimatedUsd,
    string? LlmProvider,
    string? LlmModel,
    KeyOwner? KeyOwner);

/// <summary>Where the document came from: the ingestion and the attachment it was split from.</summary>
public sealed record DocumentIngestionRef(Guid IngestionId, Guid ItemId, string FileName, string? SourceFrom, DateTimeOffset ReceivedAt);

/// <summary>
/// The review screen's payload (docs/05 "canonical data + flags + provenance"): every summary column plus the
/// canonical model exactly as stored (<c>Canonical</c>: the Invoice / PurchaseOrder / DeliveryNote JSON, null until
/// extraction has run), so every <c>FieldValue</c> keeps its provenance and history verbatim. <c>PageImageUrls</c> are
/// API-relative, in page order (<c>/api/v1/documents/{id}/pages/{n}.png</c>). <c>TenantId</c> / <c>TenantName</c> name
/// the owning tenant like the summary's, so a platform-scope detail page can say whose document it is.
/// <c>CanonicalSchemaVersion</c> 2 marks a definition-driven tree (docs/11 §5.6); <c>TypeName</c> (English, else the first
/// locale), <c>TypeVersion</c> and <c>Definition</c> describe the type version it is shown with when the API resolved one.
/// <c>AppliedConfig</c> is what the pipeline resolved for the document (docs/11 §6.2, §18 <c>AppliedConfigJson</c>): the
/// type version and its key, the template, and after an LLM extraction the schema it was sent; null before Extract.
/// </summary>
public sealed record DocumentDetail(
    Guid Id,
    Guid TenantId,
    string? TenantName,
    DocumentStatus Status,
    DocumentTypeKey Type,
    double? TypeConfidence,
    Guid? SupplierId,
    string? SupplierName,
    string? DocumentNumber,
    DateOnly? IssueDate,
    string? Currency,
    decimal? TotalGross,
    ExtractionMethod? ExtractionMethod,
    int PageCount,
    int BlockingFlagCount,
    int WarningFlagCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ApprovedAt,
    string? ApprovedBy,
    string? FailureReason,
    JsonElement? Canonical,
    int? CanonicalSchemaVersion,
    IReadOnlyList<DocumentFlagDto> Flags,
    DocumentCostsDto Costs,
    DocumentIngestionRef? Ingestion,
    IReadOnlyList<string> PageImageUrls,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    int? LineCount,
    string? TypeName,
    int? TypeVersion,
    DocumentDefinitionRef? Definition,
    JsonElement? AppliedConfig)
{
    /// <summary>How the supplier was found (docs/11 §6.2): <c>Human</c> when a reviewer assigned it; null without one or before it was recorded.</summary>
    public SupplierMatchMethod? SupplierMatchMethod { get; init; }

    /// <summary>How sure the supplier match is (docs/11 §7.2: 0.99 for an Exclusive source rule, 0.7 for a default; 1.0 when a reviewer assigned it).</summary>
    public double? SupplierMatchConfidence { get; init; }

    /// <summary>The source rule that routed the document to its supplier (<c>SupplierMatchMethod</c> <c>SourceRule</c>, docs/11 §7.2).</summary>
    public Guid? SupplierSourceRuleId { get; init; }

    /// <summary>The tenant's company the document is addressed to (docs/11 §6.4), from a source rule or the channel default.</summary>
    public Guid? ReceivingEntityId { get; init; }

    /// <summary>The document a reviewer joined this one into (<c>:join</c>, docs/11 §12.7); set on a <c>Cancelled</c> part.</summary>
    public Guid? JoinedIntoDocumentId { get; init; }

    /// <summary>
    /// Why <c>:approve</c> would refuse the document now (docs/11 §11 "Approve is refused when a document has no definition
    /// or no canonical data"), so the review screen can say so before the click; null when nothing stands in the way.
    /// </summary>
    public ApprovalBlock? ApprovalBlocked { get; init; }

    /// <summary>
    /// Whether the document's tenant has <c>configurableTypes</c> on (docs/11 §3 principle 6): the review screen offers the
    /// review tools of P2 (the Map this document panel, set type, assign or create the supplier, split and join) only
    /// then, so a tenant that is not on the feature reviews as before.
    /// </summary>
    public bool ConfigurableTypes { get; init; }

    /// <summary>
    /// Whether the document's tenant has <c>outputs</c> on (docs/11 §3 principle 6, P3): the review screen shows the
    /// Deliveries tab (<c>GET /deliveries?documentId=</c>) only then, so a tenant that is not on the feature sees the
    /// screen as before.
    /// </summary>
    public bool Outputs { get; init; }
}

/// <summary>
/// What keeps a document from being approved: <c>code</c> is <c>TYPE_REQUIRED</c> (the type is Unknown),
/// <c>NO_DEFINITION</c> (the type has no published definition the tenant can use) or <c>NO_DATA</c> (nothing was
/// captured: the document has no canonical data); <c>message</c> says it in English.
/// </summary>
public sealed record ApprovalBlock(string Code, string Message);

/// <summary>
/// <c>POST /documents/{id}:set-type</c> (docs/11 §12.1 step 3): the type key, optionally a published version of it
/// (the tenant's published version otherwise), and <c>prefill</c>: <c>none</c> (the default; the document gets the
/// empty tree of the type version, every field Missing, at once) or <c>auto</c> (the document goes back through the
/// Extract stage for that type, which fills what it can; 202).
/// </summary>
public sealed record SetDocumentTypeRequest(string TypeKey, int? Version = null, string? Prefill = null);

/// <summary>
/// <c>POST /documents/{id}:assign-supplier</c> (docs/11 §12.1): a supplier of the document's tenant, and optionally
/// <see cref="Remember"/>: a source rule made from the document's own source (docs/11 §18, needs <c>sourceRouting</c>).
/// </summary>
public sealed record AssignSupplierRequest(Guid SupplierId)
{
    public AssignSupplierRemember? Remember { get; init; }
}

/// <summary>
/// Remember the assignment as a source rule (docs/11 §12.1): <see cref="Conditions"/> names the facts of the document's
/// ingestion the rule matches on, any of <c>sender</c>, <c>senderDomain</c>, <c>recipient</c> (the mailbox, else the
/// first recipient), <c>folder</c> (the folder path within its channel) and <c>channel</c>; <see cref="Mode"/> is
/// <c>Exclusive</c> (the default) or <c>DefaultWhenNoContentMatch</c>; <see cref="PinType"/> also pins the document's type.
/// A reviewer's rule is saved disabled, a proposal a config admin enables; a config admin's is enabled at once.
/// </summary>
public sealed record AssignSupplierRemember(IReadOnlyList<string> Conditions, string? Mode = null, bool? PinType = null, bool? RequireSenderAuth = null);

/// <summary>What <c>POST /documents/{id}:create-supplier</c> answers: the new supplier and the document it was assigned to.</summary>
public sealed record SupplierFromReviewResponse(Endpoints.SupplierResponse Supplier, DocumentDetail Document);

/// <summary>
/// One part of <c>POST /documents/{id}:split</c> (docs/11 §12.7): its pages, 1-based within the document and
/// consecutive, and optionally the type a reviewer already knows it has (the part then skips classification).
/// </summary>
public sealed record SplitPartRequest(IReadOnlyList<int> Pages, string? TypeKey = null);

/// <summary>The parts of a split, in page order; together they cover every page of the document exactly once.</summary>
public sealed record SplitDocumentRequest(IReadOnlyList<SplitPartRequest> Parts);

/// <summary>A part a split produced: the first keeps the document's id, the others are new documents of the same attachment.</summary>
public sealed record SplitPartResult(Guid DocumentId, IReadOnlyList<int> Pages, string? TypeKey, DocumentStatus Status);

public sealed record SplitDocumentResponse(IReadOnlyList<SplitPartResult> Parts);

/// <summary>
/// One document of an attachment (<c>GET /documents/{id}/parts</c>, docs/11 §12.7): its pages within the attachment
/// (1-based, inclusive), status, type and number; a part cancelled by a join names the document it was joined into.
/// </summary>
public sealed record DocumentPartDto(Guid DocumentId, int PageStart, int PageEnd, int PageCount, DocumentStatus Status, string Type, string? DocumentNumber, Guid? JoinedIntoDocumentId);

/// <summary><c>POST /documents/{id}:join</c> (docs/11 §12.7): the other parts of the same attachment to join into this one.</summary>
public sealed record JoinDocumentsRequest(IReadOnlyList<Guid> DocumentIds);

public sealed record DocumentEventDto(
    long Id,
    string EventType,
    DocumentStatus? FromStatus,
    DocumentStatus? ToStatus,
    string? Actor,
    JsonElement? Details,
    DateTimeOffset At);

public sealed record ApproveDocumentRequest(string? Comment);

public sealed record RejectDocumentRequest(string Reason);

/// <summary>
/// Which stage to rerun; the document is reset to the state before it (docs/03 "any stage can be re-run").
/// <c>configMode</c> (docs/11 §18): <c>pinned</c> (the default) reruns with the type version the document is pinned to;
/// <c>current</c> drops the pin, so the rerun takes the tenant's published version of the type now (for example a fork
/// with a custom field published after the document arrived).
/// </summary>
public sealed record ReprocessDocumentRequest(string? FromStage, string? ConfigMode = null);

/// <summary>
/// One human correction (docs/05 "PATCH /documents/{id}/fields"). <see cref="Path"/> is a canonical field path as
/// <c>Invoice.EnumerateAllFields</c> produces it (<c>invoiceNumber</c>, <c>lines[2].quantity</c>, <c>supplier</c>) or a
/// property inside such a field's value (<c>supplier.vatId</c>); <see cref="Value"/> is the new value in the JSON
/// shape of the field (a number for an amount with the document currency, a string for an identifier) or null to
/// clear it. <c>op</c> is the correction operation: <c>set</c> (the default); <c>move</c> (docs/11 §12.3 "match
/// fields", every document with data): the value of the field <c>from</c> names goes into <see cref="Path"/>, converted
/// to that field's kind, with its place on the page, and <c>from</c> is left without a value; both fields keep their
/// previous state in their history, and the move is one Correction row (op <c>move</c>, the source path in its
/// <c>SourceJson.from</c>). For a definition-driven document (<c>canonicalSchemaVersion</c> 2) also <c>append</c> (a
/// row of a repeating group, with an optional object of row values, or a list entry) and <c>remove</c> (a row, a list
/// entry or a bag key). <c>setType</c>, <c>split</c> and <c>join</c> are review actions of their own
/// (<c>:set-type</c>, <c>:split</c>, <c>:join</c>), not corrections. <c>source</c> optionally says where on the page
/// or in the file the reviewer took the value from; it is stored with the field's provenance and on the Correction row.
/// </summary>
public sealed record FieldCorrection(string Path, JsonElement? Value, string? Reason, string? Op = null, CorrectionSource? Source = null, string? From = null);

/// <summary>
/// Where a corrected value came from (docs/11 §6.2 <c>Correction.SourceJson</c>). Every part is optional.
/// <c>method</c> names how the reviewer picked it (for example <c>word</c>, <c>region</c>, <c>ocr</c>); <c>page</c> is
/// 1-based; <c>bBox</c> is in the text layer's 1000 × 1000 page units; <c>rawText</c> is the text as printed;
/// <c>words</c> lists the picked words as <c>[page, wordIndex]</c> pairs; <c>path</c> and <c>cell</c> locate a value
/// in a structured file (an XML element path, a spreadsheet cell such as <c>B7</c>).
/// </summary>
public sealed record CorrectionSource(
    string? Method = null,
    int? Page = null,
    SourceBox? BBox = null,
    string? RawText = null,
    IReadOnlyList<IReadOnlyList<int>>? Words = null,
    string? Path = null,
    string? Cell = null);

/// <summary>An axis-aligned box in page units (0 to 1000 on both axes, origin top left), as the canonical JSON stores boxes.</summary>
public sealed record SourceBox(double X0, double Y0, double X1, double Y1);

public sealed record CorrectFieldsRequest(IReadOnlyList<FieldCorrection> Corrections);

/// <summary>A review-queue entry; <c>TenantId</c> / <c>TenantName</c> name the owning tenant (see <see cref="DocumentSummary"/>).</summary>
public sealed record ReviewTaskDto(
    Guid Id,
    Guid TenantId,
    string? TenantName,
    Guid DocumentId,
    string Reason,
    string? AssignedTo,
    ReviewTaskStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SlaDueAt,
    DateTimeOffset? CompletedAt,
    DocumentSummary? Document,
    ReviewPriority Priority);
