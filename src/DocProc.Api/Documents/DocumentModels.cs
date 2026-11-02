using System.Text.Json;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Documents;

/// <summary>
/// One row of the inbox (docs/05 "Inbox"): the denormalised columns plus flag counts, never the canonical JSON.
/// <c>TenantId</c> / <c>TenantName</c> name the owning tenant, so a platform-scope list (the admin console's
/// cross-tenant documents list and global review queue) can show whose row it is.
/// </summary>
public sealed record DocumentSummary(
    Guid Id,
    Guid TenantId,
    string? TenantName,
    DocumentStatus Status,
    DocumentType Type,
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
    DateTimeOffset? ApprovedAt);

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
/// </summary>
public sealed record DocumentDetail(
    Guid Id,
    Guid TenantId,
    string? TenantName,
    DocumentStatus Status,
    DocumentType Type,
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
    IReadOnlyList<string> PageImageUrls);

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

/// <summary>Which stage to rerun; the document is reset to the state before it (docs/03 "any stage can be re-run").</summary>
public sealed record ReprocessDocumentRequest(string? FromStage);

/// <summary>
/// One human correction (docs/05 "PATCH /documents/{id}/fields"). <see cref="Path"/> is a canonical field path as
/// <c>Invoice.EnumerateAllFields</c> produces it (<c>invoiceNumber</c>, <c>lines[2].quantity</c>, <c>supplier</c>) or a
/// property inside such a field's value (<c>supplier.vatId</c>); <see cref="Value"/> is the new value in the JSON
/// shape of the field (a number for an amount with the document currency, a string for an identifier) or null to
/// clear it. <c>op</c> is the correction operation; only <c>set</c> (the default) exists so far, the others of
/// docs/11 §6.2 (<c>append</c>, <c>remove</c>, <c>move</c>, …) are refused until their phase. <c>source</c>
/// optionally says where on the page or in the file the reviewer took the value from; it is stored with the field's
/// provenance and on the Correction row.
/// </summary>
public sealed record FieldCorrection(string Path, JsonElement? Value, string? Reason, string? Op = null, CorrectionSource? Source = null);

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
    DocumentSummary? Document);
