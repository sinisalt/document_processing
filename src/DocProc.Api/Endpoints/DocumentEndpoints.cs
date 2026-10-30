using System.Security.Claims;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace DocProc.Api.Endpoints;

/// <summary>
/// The document resource of docs/05: inbox listing, the review payload, the viewer's binaries, the timeline, and the
/// human decisions (approve, reject, reprocess, correct). Tenant scope is the resolved <c>ITenantContext</c>; the GETs
/// work in platform scope for operators, and the actions act on whatever document the scope can see. Every state
/// change is a conditional <c>UPDATE … WHERE Status = &lt;the status the caller saw&gt;</c>, so two reviewers deciding
/// the same document at once produce exactly one decision, one event and one publish message; the loser gets 409.
/// </summary>
public static class DocumentEndpoints
{
    /// <summary>Stages a reviewer may rerun with <c>:reprocess</c>, and the state the document is reset to for each (the output of the stage before it).</summary>
    private static readonly IReadOnlyDictionary<PipelineStage, DocumentStatus> ReprocessStart = new Dictionary<PipelineStage, DocumentStatus>
    {
        [PipelineStage.Normalize] = DocumentStatus.Received,
        [PipelineStage.Classify] = DocumentStatus.Split,
        [PipelineStage.Extract] = DocumentStatus.Matched,
        [PipelineStage.Validate] = DocumentStatus.Extracted,
    };

    /// <summary>The pipeline's progress states in order; a reprocess may only reset to a state the document has actually been in.</summary>
    private static readonly DocumentStatus[] PipelineOrder =
    [
        DocumentStatus.Received,
        DocumentStatus.Normalized,
        DocumentStatus.Split,
        DocumentStatus.Classified,
        DocumentStatus.Matched,
        DocumentStatus.Extracted,
        DocumentStatus.Validated,
    ];

    /// <summary>States in which a human may correct fields: the pipeline is not writing the canonical JSON and nothing has been published.</summary>
    private static readonly DocumentStatus[] Correctable = [DocumentStatus.NeedsReview, DocumentStatus.Validated, DocumentStatus.Failed];

    public static RouteGroupBuilder MapDocuments(this RouteGroupBuilder group)
    {
        var documents = group.MapGroup("/documents").WithTags("Documents");

        documents.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("ListDocuments")
            .WithSummary("Inbox: documents newest first with status, type, supplier, flag, issue-date and text filters; cursor-paginated.");

        documents.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDocument")
            .WithSummary("The review payload: summary, the canonical model with field provenance, flags, costs and origin.");

        documents.MapGet("/{id:guid}/pages/{n:int}.png", GetPageAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDocumentPage")
            .WithSummary("Rendered page image (PNG, 1-based page number) for the viewer; ETag is the content hash.")
            .Produces(StatusCodes.Status200OK, contentType: "image/png");

        documents.MapGet("/{id:guid}/original", GetOriginalAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDocumentOriginal")
            .WithSummary("The file the document was ingested from. PDFs and images are served inline with their content type; anything else is an attachment of application/octet-stream.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");

        documents.MapGet("/{id:guid}/text", GetTextAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDocumentText")
            .WithSummary("The positioned text layer (words with boxes per page) for the highlight overlay.")
            .Produces(StatusCodes.Status200OK, contentType: "application/json");

        documents.MapGet("/{id:guid}/events", GetEventsAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDocumentEvents")
            .WithSummary("Timeline: state transitions, stage outcomes and human actions, oldest first.");

        documents.MapPost("/{id:guid}:approve", ApproveAsync)
            .RequireAuthorization(Policies.Review)
            .WithName("ApproveDocument")
            .WithSummary("Approves a document waiting for review (or validated and not yet routed), closes its review tasks and queues publishing.");

        documents.MapPost("/{id:guid}:reject", RejectAsync)
            .RequireAuthorization(Policies.Review)
            .WithName("RejectDocument")
            .WithSummary("Rejects a document with a reason, closes its review tasks and queues publishing of the rejection.");

        documents.MapPost("/{id:guid}:reprocess", ReprocessAsync)
            .RequireAuthorization(Policies.Review)
            .WithName("ReprocessDocument")
            .WithSummary("Sends a failed, rejected, validated or review-pending document back through the pipeline from a stage it has reached (Normalize, Classify, Extract or Validate; default Extract).");

        documents.MapPatch("/{id:guid}/fields", CorrectFieldsAsync)
            .RequireAuthorization(Policies.Review)
            .WithName("CorrectDocumentFields")
            .WithSummary("Applies reviewer corrections by canonical field path to a document that needs review, is validated or has failed; each keeps the previous value in the field's history and is recorded as a Correction.");

        return group;
    }

    private static async Task<Results<Ok<Page<DocumentSummary>>, ValidationProblem>> ListAsync(
        string? status,
        string? type,
        Guid? supplierId,
        string? flag,
        DateOnly? from,
        DateOnly? to,
        string? q,
        string? cursor,
        int? limit,
        [FromServices] DocProcDbContext db,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var statuses = ParseList<DocumentStatus>(status, "status", errors);
        var types = ParseList<DocumentType>(type, "type", errors);
        PageCursor? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (PageCursor.TryDecode(cursor, out var decoded))
            {
                after = decoded;
            }
            else
            {
                errors["cursor"] = ["The cursor is not one this API issued."];
            }
        }

        if (from is not null && to is not null && to < from)
        {
            errors["to"] = ["Must not be before 'from'."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var query = db.Documents.AsNoTracking();
        if (statuses.Count > 0)
        {
            query = query.Where(d => statuses.Contains(d.Status));
        }

        if (types.Count > 0)
        {
            query = query.Where(d => types.Contains(d.Type));
        }

        if (supplierId is { } supplier)
        {
            query = query.Where(d => d.SupplierId == supplier);
        }

        if (!string.IsNullOrWhiteSpace(flag))
        {
            var code = flag.Trim().ToUpperInvariant();
            query = query.Where(d => d.Flags.Any(f => f.Code == code && f.ResolvedAt == null));
        }

        if (from is { } fromDate)
        {
            query = query.Where(d => d.IssueDate >= fromDate);
        }

        if (to is { } toDate)
        {
            query = query.Where(d => d.IssueDate <= toDate);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + EscapeLike(q.Trim()) + "%";
            query = query.Where(d => EF.Functions.Like(d.DocumentNumber!, pattern, "\\") || (d.Supplier != null && EF.Functions.Like(d.Supplier.Name, pattern, "\\")));
        }

        if (after is { } c)
        {
            query = query.Where(d => d.CreatedAt < c.At || (d.CreatedAt == c.At && d.Id.CompareTo(c.Id) < 0));
        }

        var size = PageCursor.ClampLimit(limit);
        var rows = await DocumentQueries.Rows(query
                .OrderByDescending(d => d.CreatedAt)
                .ThenByDescending(d => d.Id)
                .Take(size + 1))
            .ToListAsync(ct);

        var hasMore = rows.Count > size;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(DocumentQueries.ToSummary).ToList();
        var next = hasMore ? new PageCursor(items[^1].CreatedAt, items[^1].Id).Encode() : null;
        return TypedResults.Ok(new Page<DocumentSummary>(items, next));
    }

    private static async Task<Results<Ok<DocumentDetail>, ProblemHttpResult>> GetAsync(Guid id, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        var row = await DocumentQueries.FindForDetailAsync(db, id, track: false, ct);
        return row is null ? NotFound(id) : TypedResults.Ok(DocumentQueries.ToDetail(row));
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> GetPageAsync(Guid id, int n, HttpContext context, [FromServices] DocProcDbContext db, IContainerBlobStore blobs, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => new { d.Id, d.PageImageKeys }).SingleOrDefaultAsync(ct);
        if (document is null)
        {
            return NotFound(id);
        }

        if (n < 1 || n > document.PageImageKeys.Count)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Page not found", detail: $"Document {id} has {document.PageImageKeys.Count} rendered page(s); page {n} does not exist.");
        }

        var reference = await blobs.FindAsync(BlobContainers.Derived, document.PageImageKeys[n - 1], ct);
        if (reference is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Page image missing", detail: $"The rendered image of page {n} is not in the derived store.");
        }

        try
        {
            var stream = await blobs.OpenReadAsync(reference, ct);
            NoSniff(context);
            return TypedResults.Stream(stream, "image/png", entityTag: new EntityTagHeaderValue($"\"{reference.Sha256}\""));
        }
        catch (BlobNotFoundException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Page image missing", detail: $"The rendered image of page {n} is not in the derived store.");
        }
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> GetOriginalAsync(Guid id, HttpContext context, [FromServices] DocProcDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var item = await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => d.IngestionItem).SingleOrDefaultAsync(ct);
        if (item is null)
        {
            return NotFound(id);
        }

        var reference = new BlobRef(item.Blob.Container, item.Blob.Key, item.Blob.Sha256, item.Blob.SizeBytes, item.Blob.ContentType);
        try
        {
            var stream = await blobs.OpenReadAsync(reference, ct);

            // The content type is whatever the uploader claimed. Only formats the viewer renders itself are served
            // inline under that type; everything else (HTML, SVG, Office, archives) downloads as opaque bytes, so a
            // crafted upload can never run as a page of this origin.
            var inline = IsInlineSafe(item.MimeType);
            var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
            disposition.SetHttpFileName(item.FileName);
            context.Response.Headers.ContentDisposition = disposition.ToString();
            NoSniff(context);
            return TypedResults.Stream(stream, inline ? item.MimeType : ItemKinds.DefaultContentType, entityTag: new EntityTagHeaderValue($"\"{reference.Sha256}\""));
        }
        catch (BlobNotFoundException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Original missing", detail: "The ingested file is no longer in the raw store.");
        }
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> GetTextAsync(Guid id, HttpContext context, [FromServices] DocProcDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => new { d.Id, d.TextLayerBlob }).SingleOrDefaultAsync(ct);
        if (document is null)
        {
            return NotFound(id);
        }

        if (document.TextLayerBlob is not { } blob)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No text layer", detail: $"Document {id} has no text layer yet.");
        }

        try
        {
            var stream = await blobs.OpenReadAsync(new BlobRef(blob.Container, blob.Key, blob.Sha256, blob.SizeBytes, blob.ContentType), ct);
            NoSniff(context);
            return TypedResults.Stream(stream, "application/json", entityTag: new EntityTagHeaderValue($"\"{blob.Sha256}\""));
        }
        catch (BlobNotFoundException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No text layer", detail: "The text layer is not in the derived store.");
        }
    }

    private static async Task<Results<Ok<IReadOnlyList<DocumentEventDto>>, ProblemHttpResult>> GetEventsAsync(Guid id, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (!await db.Documents.AsNoTracking().AnyAsync(d => d.Id == id, ct))
        {
            return NotFound(id);
        }

        var events = await db.DocumentEvents.AsNoTracking()
            .Where(e => e.DocumentId == id)
            .OrderBy(e => e.At)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        IReadOnlyList<DocumentEventDto> items = events
            .Select(e => new DocumentEventDto(e.Id, e.EventType, e.FromStatus, e.ToStatus, e.Actor, DocumentQueries.ParseJson(e.DetailsJson), e.At))
            .ToList();
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Ok<DocumentDetail>, ProblemHttpResult>> ApproveAsync(
        Guid id,
        ApproveDocumentRequest? request,
        [FromServices] DocProcDbContext db,
        IPipelineQueue queue,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var row = await DocumentQueries.FindForDetailAsync(db, id, track: false, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var document = row.Document;
        if (!DocumentStateMachine.CanTransition(document.Status, DocumentStatus.Approved))
        {
            return InvalidTransition(document, DocumentStatus.Approved);
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        var change = new DocumentStatusChange(document.Status, DocumentStatus.Approved);
        var claimed = await db.Documents
            .Where(d => d.Id == id && d.Status == change.From)
            .ExecuteUpdateAsync(
                s => s.SetProperty(d => d.Status, DocumentStatus.Approved)
                    .SetProperty(d => d.ApprovedAt, (DateTimeOffset?)now)
                    .SetProperty(d => d.ApprovedBy, actor)
                    .SetProperty(d => d.UpdatedAt, now),
                ct);
        if (claimed == 0)
        {
            return ConcurrentChange(id);
        }

        document.Status = DocumentStatus.Approved;
        document.ApprovedAt = now;
        document.ApprovedBy = actor;
        document.UpdatedAt = now;
        await CloseOpenTasksAsync(db, document.Id, ReviewTaskStatus.Done, now, ct);
        DocumentQueries.AddEvent(db, document, DocumentEventTypes.Approved, change, actor, new { comment = Clean(request?.Comment, 2000) }, now);
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(new ProcessDocument(document.TenantId, document.Id, PipelineStage.Publish), ct);
        return TypedResults.Ok(DocumentQueries.ToDetail(row));
    }

    private static async Task<Results<Ok<DocumentDetail>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        Guid id,
        RejectDocumentRequest request,
        [FromServices] DocProcDbContext db,
        IPipelineQueue queue,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var reason = Clean(request.Reason, 2000);
        if (reason is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["reason"] = ["A reason is required."] });
        }

        var row = await DocumentQueries.FindForDetailAsync(db, id, track: false, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var document = row.Document;
        if (!DocumentStateMachine.CanTransition(document.Status, DocumentStatus.Rejected))
        {
            return InvalidTransition(document, DocumentStatus.Rejected);
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        var change = new DocumentStatusChange(document.Status, DocumentStatus.Rejected);
        var claimed = await db.Documents
            .Where(d => d.Id == id && d.Status == change.From)
            .ExecuteUpdateAsync(
                s => s.SetProperty(d => d.Status, DocumentStatus.Rejected)
                    .SetProperty(d => d.UpdatedAt, now),
                ct);
        if (claimed == 0)
        {
            return ConcurrentChange(id);
        }

        document.Status = DocumentStatus.Rejected;
        document.UpdatedAt = now;
        await CloseOpenTasksAsync(db, document.Id, ReviewTaskStatus.Done, now, ct);
        DocumentQueries.AddEvent(db, document, DocumentEventTypes.Rejected, change, actor, new { reason }, now);
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(new ProcessDocument(document.TenantId, document.Id, PipelineStage.Publish), ct);
        return TypedResults.Ok(DocumentQueries.ToDetail(row));
    }

    private static async Task<Results<Ok<DocumentDetail>, ValidationProblem, ProblemHttpResult>> ReprocessAsync(
        Guid id,
        ReprocessDocumentRequest? request,
        [FromServices] DocProcDbContext db,
        IPipelineQueue queue,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var stage = PipelineStage.Extract;
        if (!string.IsNullOrWhiteSpace(request?.FromStage))
        {
            if (!Enum.TryParse(request.FromStage.Trim(), ignoreCase: true, out stage) || !ReprocessStart.ContainsKey(stage))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["fromStage"] = [$"Must be one of {string.Join(", ", ReprocessStart.Keys)}."] });
            }
        }

        var row = await DocumentQueries.FindForDetailAsync(db, id, track: false, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var document = row.Document;
        var target = ReprocessStart[stage];
        if (!DocumentStateMachine.CanReprocess(document.Status, target))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Cannot reprocess",
                detail: $"Document {document.Id} is {document.Status}; only Failed, NeedsReview, Rejected or Validated documents can be sent back through the pipeline.");
        }

        // A stage needs the output of the stages before it (Extract reads the text layer Normalize wrote, Validate the
        // canonical data Extract wrote). A document that failed early never produced that input, so it may only be
        // reset to a state its timeline shows it has been in.
        var reached = await FurthestPipelineStatusAsync(db, document, ct);
        if (Array.IndexOf(PipelineOrder, target) > Array.IndexOf(PipelineOrder, reached))
        {
            var latest = ReprocessStart.Where(kv => Array.IndexOf(PipelineOrder, kv.Value) <= Array.IndexOf(PipelineOrder, reached)).Select(kv => kv.Key).Max();
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Stage not reached",
                detail: $"Document {document.Id} never got past {reached}, so it cannot rerun from {stage} (which needs the output of the stages before it). The latest stage it can rerun from is {latest}.");
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        var change = new DocumentStatusChange(document.Status, target);
        var claimed = await db.Documents
            .Where(d => d.Id == id && d.Status == change.From)
            .ExecuteUpdateAsync(
                s => s.SetProperty(d => d.Status, target)
                    .SetProperty(d => d.FailureReason, (string?)null)
                    .SetProperty(d => d.UpdatedAt, now),
                ct);
        if (claimed == 0)
        {
            return ConcurrentChange(id);
        }

        document.Status = target;
        document.FailureReason = null;
        document.UpdatedAt = now;
        // A review task for the old extraction is moot; the pipeline opens a new one if the rerun still needs a human.
        await CloseOpenTasksAsync(db, document.Id, ReviewTaskStatus.Skipped, now, ct);
        DocumentQueries.AddEvent(db, document, DocumentEventTypes.Reprocessed, change, actor, new { fromStage = stage.ToString() }, now);
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(new ProcessDocument(document.TenantId, document.Id, stage), ct);
        return TypedResults.Ok(DocumentQueries.ToDetail(row));
    }

    private static async Task<Results<Ok<DocumentDetail>, ValidationProblem, ProblemHttpResult>> CorrectFieldsAsync(
        Guid id,
        CorrectFieldsRequest request,
        [FromServices] DocProcDbContext db,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request.Corrections is not { Count: > 0 })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["corrections"] = ["At least one correction is required."] });
        }

        var row = await DocumentQueries.FindForDetailAsync(db, id, track: true, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var document = row.Document;
        if (!Correctable.Contains(document.Status))
        {
            var detail = DocumentStateMachine.IsSettled(document.Status)
                ? $"Document {document.Id} is {document.Status}; reprocess it before correcting fields."
                : $"Document {document.Id} is {document.Status}: the pipeline is still writing it. Fields can be corrected once it is NeedsReview, Validated or Failed.";
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Document not correctable", detail: detail);
        }

        var rootType = CanonicalPatcher.RootTypeFor(document.Type);
        if (rootType is null || string.IsNullOrWhiteSpace(document.CanonicalJson))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Nothing to correct", detail: $"Document {document.Id} ({document.Type}, {document.Status}) has no canonical data yet.");
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        var tenantCurrency = await db.Tenants.AsNoTracking().Where(t => t.Id == document.TenantId).Select(t => t.DefaultCurrency).FirstOrDefaultAsync(ct);

        CanonicalPatchResult patched;
        try
        {
            patched = CanonicalPatcher.Apply(rootType, document.CanonicalJson, request.Corrections, actor, now, tenantCurrency);
        }
        catch (CanonicalPatchException ex)
        {
            var key = string.IsNullOrEmpty(ex.Path) ? "corrections" : ex.Path;
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { [key] = [ex.Message] });
        }

        document.CanonicalJson = patched.Json;
        document.UpdatedAt = now;
        RefreshDenormalizedColumns(document);

        foreach (var applied in patched.Applied)
        {
            db.Corrections.Add(new Correction
            {
                TenantId = document.TenantId,
                DocumentId = document.Id,
                FieldPath = applied.Path,
                OldValueJson = applied.OldValueJson,
                NewValueJson = applied.NewValueJson,
                By = actor,
                At = now,
                Reason = Clean(applied.Reason, 500),
            });
        }

        DocumentQueries.AddEvent(db, document, DocumentEventTypes.Corrected, null, actor, new { paths = patched.Applied.Select(a => a.Path).ToList() }, now);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(DocumentQueries.ToDetail(row));
    }

    /// <summary>
    /// The furthest pipeline state the document's timeline records (as the source or target of any event), or its
    /// current state when that is a pipeline state. Review and settled states say nothing about which stages ran, so
    /// they do not count; <c>Received</c> is always reached.
    /// </summary>
    private static async Task<DocumentStatus> FurthestPipelineStatusAsync(DocProcDbContext db, Document document, CancellationToken ct)
    {
        var recorded = await db.DocumentEvents.AsNoTracking()
            .Where(e => e.DocumentId == document.Id)
            .Select(e => new { e.FromStatus, e.ToStatus })
            .ToListAsync(ct);

        var furthest = 0;
        foreach (var status in recorded.SelectMany(e => new[] { e.FromStatus, e.ToStatus }).Append(document.Status))
        {
            if (status is { } s)
            {
                furthest = Math.Max(furthest, Array.IndexOf(PipelineOrder, s));
            }
        }

        return PipelineOrder[furthest];
    }

    /// <summary>The list columns are always rewritten from the canonical JSON (docs/02 §2), so a correction shows in the inbox too.</summary>
    private static void RefreshDenormalizedColumns(Document document)
    {
        switch (document.Type)
        {
            case DocumentType.Invoice or DocumentType.CreditNote:
                var invoice = CanonicalDocumentSerializer.DeserializeInvoice(document.CanonicalJson!);
                SetNumber(document, invoice.InvoiceNumber.HasValue ? invoice.InvoiceNumber.Value : null);
                document.IssueDate = invoice.IssueDate.HasValue ? invoice.IssueDate.Value : null;
                SetCurrency(document, invoice.Currency.HasValue ? invoice.Currency.Value : null);
                document.TotalGross = invoice.TotalGross.HasValue ? invoice.TotalGross.Value.Amount : null;
                break;

            case DocumentType.PurchaseOrder:
                var order = CanonicalDocumentSerializer.DeserializePurchaseOrder(document.CanonicalJson!);
                SetNumber(document, order.PoNumber.HasValue ? order.PoNumber.Value : null);
                document.IssueDate = order.OrderDate.HasValue ? order.OrderDate.Value : null;
                SetCurrency(document, order.Currency.HasValue ? order.Currency.Value : null);
                document.TotalGross = order.TotalGross.HasValue ? order.TotalGross.Value.Amount : null;
                break;

            case DocumentType.DeliveryNote:
                var note = CanonicalDocumentSerializer.DeserializeDeliveryNote(document.CanonicalJson!);
                SetNumber(document, note.DeliveryNoteNumber.HasValue ? note.DeliveryNoteNumber.Value : null);
                document.IssueDate = note.DeliveryDate.HasValue ? note.DeliveryDate.Value : null;
                break;

            default:
                break;
        }
    }

    private static void SetNumber(Document document, DocumentIdentifier? number)
    {
        document.DocumentNumber = number is null ? null : Truncate(number.Verbatim, 100);
        document.DocumentNumberNormalized = number is null ? null : Truncate(number.Normalized, 100);
    }

    /// <summary><c>Document.Currency</c> is <c>char(3)</c>: only a well-formed code goes into the list column; anything else stays in the canonical JSON alone.</summary>
    private static void SetCurrency(Document document, string? currency)
    {
        var normalized = currency is null ? null : CurrencyCode.Normalize(currency);
        document.Currency = CurrencyCode.IsWellFormed(normalized) ? normalized : null;
    }

    private static async Task CloseOpenTasksAsync(DocProcDbContext db, Guid documentId, ReviewTaskStatus outcome, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.ReviewTasks
            .Where(t => t.DocumentId == documentId && (t.Status == ReviewTaskStatus.Open || t.Status == ReviewTaskStatus.InProgress))
            .ToListAsync(ct);
        foreach (var task in open)
        {
            task.Status = outcome;
            task.CompletedAt = now;
        }
    }

    /// <summary>Only PDFs and raster images are rendered by the viewer; served inline under their own type. SVG can carry script, so it downloads like everything else.</summary>
    private static bool IsInlineSafe(string mimeType) =>
        string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !string.Equals(mimeType, "image/svg+xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>Browsers must not second-guess the declared type of streamed bytes (a PDF that starts with HTML stays a PDF).</summary>
    private static void NoSniff(HttpContext context) => context.Response.Headers.XContentTypeOptions = "nosniff";

    private static ProblemHttpResult NotFound(Guid id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Document not found", detail: $"No document {id} is visible in the current scope.");

    private static ProblemHttpResult InvalidTransition(Document document, DocumentStatus to) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Invalid transition",
            detail: $"Document {document.Id} is {document.Status} and cannot move to {to}. Allowed: {string.Join(", ", DocumentStateMachine.NextStates(document.Status))}.");

    /// <summary>The conditional update matched nothing: another reviewer or the pipeline changed the document between the read and the write.</summary>
    private static ProblemHttpResult ConcurrentChange(Guid id) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Document changed",
            detail: $"Document {id} was changed by someone else while this request ran; reload it and decide again.");

    private static List<T> ParseList<T>(string? csv, string name, Dictionary<string, string[]> errors)
        where T : struct, Enum
    {
        var values = new List<T>();
        if (string.IsNullOrWhiteSpace(csv))
        {
            return values;
        }

        foreach (var token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<T>(token, ignoreCase: true, out var value) && Enum.IsDefined(value))
            {
                values.Add(value);
            }
            else
            {
                errors[name] = [$"'{token}' is not one of {string.Join(", ", Enum.GetNames<T>())}."];
            }
        }

        return values;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Truncate(trimmed, maxLength);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
