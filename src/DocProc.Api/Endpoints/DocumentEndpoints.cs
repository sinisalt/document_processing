using System.Collections.Frozen;
using System.Security.Claims;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Domain.Validation;
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
/// The conditional update bumps the <c>Version</c> token like a tracked save would (so a tracked writer that loaded
/// the row earlier, the pipeline or <c>PATCH /fields</c>, fails its own save instead of overwriting the decision) and
/// shares one transaction with the event and the task closures it entails; the queue message goes out after the commit.
/// </summary>
public static partial class DocumentEndpoints
{
    /// <summary>The most corrections one <c>PATCH …/fields</c> carries.</summary>
    public const int MaxCorrectionsPerRequest = 5_000;

    /// <summary>
    /// The largest <c>PATCH …/fields</c> body: a <c>set</c> of a 10,000-row list with its tiers is about 4.5 MB; a larger
    /// body is refused (413) before it is read.
    /// </summary>
    public const long MaxCorrectionsRequestBytes = 8L * 1024 * 1024;

    /// <summary>Stages a reviewer may rerun with <c>:reprocess</c>, and the state the document is reset to for each (the output of the stage before it).</summary>
    private static readonly IReadOnlyDictionary<PipelineStage, DocumentStatus> ReprocessStart = new Dictionary<PipelineStage, DocumentStatus>
    {
        [PipelineStage.Normalize] = DocumentStatus.Received,
        [PipelineStage.Classify] = DocumentStatus.Split,
        [PipelineStage.Match] = DocumentStatus.Classified,
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
            .WithSummary("Inbox: documents newest first with status, type, supplier, flag, issue-date and text filters, plus field.<key>=value for the indexed fields of the document types (docs/11 §18); cursor-paginated.");

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
            .WithMetadata(new RequestSizeLimitAttribute(MaxCorrectionsRequestBytes))
            .WithName("CorrectDocumentFields")
            .WithSummary("Applies reviewer corrections by canonical field path to a document that needs review, is validated or has failed; each keeps the previous value in the field's history and is recorded as a Correction.");

        MapReviewActions(documents);
        MapMapMode(documents);
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
        string? fields,
        HttpContext http,
        [FromServices] DocProcDbContext db,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var statuses = ParseList<DocumentStatus>(status, "status", errors);
        var types = ParseTypeKeys(type, errors);
        var fieldFilters = ParseFieldFilters(http.Request.Query, errors);
        var columns = ParseFieldColumns(fields, errors);
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

        // ?field.<key>=value (docs/11 §18): documents with that value in an indexed field, compared as the index stores
        // it (strings and identifiers normalised, numbers and dates by value); several filters must all hold.
        foreach (var filter in fieldFilters)
        {
            query = filter.Apply(query);
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
        var rows = await DocumentQueries.Rows(db, query
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
        if (columns.Count > 0 && items.Count > 0)
        {
            items = await WithFieldColumnsAsync(db, items, columns, ct);
        }

        var next = hasMore ? new PageCursor(items[^1].CreatedAt, items[^1].Id).Encode() : null;
        return TypedResults.Ok(new Page<DocumentSummary>(items, next));
    }

    /// <summary>The most indexed fields one list request may ask for as columns.</summary>
    public const int MaxFieldColumns = 10;

    /// <summary><c>fields=supplierCustomerRef,lines[*].supplierSku</c>: the indexed fields to return as columns (docs/11 §19).</summary>
    private static List<string> ParseFieldColumns(string? fields, Dictionary<string, string[]> errors)
    {
        var keys = (fields ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count > MaxFieldColumns)
        {
            errors["fields"] = [$"At most {MaxFieldColumns} fields."];
            return [];
        }

        var invalid = keys.Where(k => k.Length > DocumentFieldIndexEntry.MaxFieldKeyLength || !FieldKeyPattern().IsMatch(k)).ToList();
        if (invalid.Count > 0)
        {
            errors["fields"] = [$"Not indexed field paths: {string.Join(", ", invalid)}."];
            return [];
        }

        return keys;
    }

    /// <summary>
    /// The page's rows with the asked-for indexed values in <see cref="DocumentSummary.Fields"/>: per field the first
    /// value the index holds (ordinal 0; a row field such as <c>lines[*].supplierSku</c> has one per row, and only the
    /// first is read, so a page costs at most one index row per document and key).
    /// </summary>
    private static async Task<List<DocumentSummary>> WithFieldColumnsAsync(DocProcDbContext db, List<DocumentSummary> items, List<string> keys, CancellationToken ct)
    {
        var ids = items.Select(i => i.Id).ToList();
        var entries = await db.DocumentFieldIndex.AsNoTracking()
            .Where(e => ids.Contains(e.DocumentId) && keys.Contains(e.FieldKey) && e.Ordinal == 0)
            .Select(e => new { e.DocumentId, e.FieldKey, e.StringValue, e.NumberValue, e.DateValue })
            .ToListAsync(ct);
        var byDocument = entries
            .GroupBy(e => e.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, string>)g
                    .Select(e => (e.FieldKey, Value: e.DateValue?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                                                     ?? e.NumberValue?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                     ?? e.StringValue))
                    .Where(e => e.Value is not null)
                    .ToDictionary(e => e.FieldKey, e => e.Value!, StringComparer.Ordinal));
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        return [.. items.Select(i => i with { Fields = byDocument.TryGetValue(i.Id, out var values) ? values : empty })];
    }

    private static async Task<Results<Ok<DocumentDetail>, ProblemHttpResult>> GetAsync(
        Guid id,
        [FromServices] DocProcDbContext db,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
        CancellationToken ct)
    {
        var row = await DocumentQueries.FindForDetailAsync(db, id, track: false, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        return TypedResults.Ok(await DetailAsync(row, configs, features, ct));
    }

    /// <summary>
    /// The detail of a document as every endpoint answers it: with the type version it is shown with
    /// (<see cref="DefinitionOfAsync"/>), and, for a correctable document, the tree the next <c>PATCH …/fields</c> starts
    /// from: for a definition-driven type that has no stored tree yet (a delivery note extracted before the tenant
    /// switched <c>configurableTypes</c> on) the empty tree of its version with <c>canonicalSchemaVersion</c> 2, so the
    /// generated form can be filled at once instead of showing "no extracted data"; for a stored version 2 tree, the
    /// tree completed with the fields its version has and it lacks. Nothing is written.
    /// </summary>
    private static async Task<DocumentDetail> DetailAsync(DocumentRow row, IEffectiveConfigResolver configs, ITenantFeatures features, CancellationToken ct)
    {
        var document = row.Document;
        var (definition, type) = await DefinitionOfAsync(document, configs, features, ct);
        var detail = DocumentQueries.ToDetail(row, definition) with
        {
            ApprovalBlocked = await ApprovalBlockAsync(document, configs, features, ct),
            ConfigurableTypes = await features.IsEnabledAsync(document.TenantId, TenantFeature.ConfigurableTypes, ct),
            Outputs = await features.IsEnabledAsync(document.TenantId, TenantFeature.Outputs, ct),
        };
        if (type?.Compiled is not { } compiled || !Correctable.Contains(document.Status))
        {
            return detail;
        }

        string? view = null;
        if (string.IsNullOrWhiteSpace(document.CanonicalJson))
        {
            view = CanonicalStorage.IsDefinitionDriven(type, configurable: true) ? DefinitionTree.CreateEmpty(compiled.Descriptors).ToJsonString() : null;
        }
        else if (document.CanonicalSchemaVersion == DefinitionTree.SchemaVersion
                 && System.Text.Json.Nodes.JsonNode.Parse(document.CanonicalJson) is System.Text.Json.Nodes.JsonObject stored
                 && DefinitionTree.Complete(stored, compiled.Descriptors))
        {
            // A tree of an earlier version (reprocessed from Validate onto a newer one) lacks the fields the version
            // added; PATCH completes it the same way before it applies a correction.
            view = stored.ToJsonString();
        }

        if (view is not null)
        {
            using var tree = System.Text.Json.JsonDocument.Parse(view);
            detail = detail with { Canonical = tree.RootElement.Clone(), CanonicalSchemaVersion = DefinitionTree.SchemaVersion };
        }

        return detail;
    }

    /// <summary>
    /// The type version a document is shown with (<see cref="DocumentDefinitionRef"/>): for a definition-driven document,
    /// and for every document of a tenant with <c>configurableTypes</c> on; null otherwise, so a tenant that is not on the
    /// feature gets the payload it got before. Also answers the resolved type, which is null exactly when the reference is.
    /// </summary>
    private static async Task<(DocumentDefinitionRef? Definition, EffectiveType? Type)> DefinitionOfAsync(Document document, IEffectiveConfigResolver configs, ITenantFeatures features, CancellationToken ct)
    {
        if (document.Type.IsUnknown)
        {
            return (null, null);
        }

        if (document.CanonicalSchemaVersion != DefinitionTree.SchemaVersion && !await features.IsEnabledAsync(document.TenantId, TenantFeature.ConfigurableTypes, ct))
        {
            return (null, null);
        }

        var config = await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, document.Type, document.SupplierId, document.DocumentTypeVersionId), ct);
        return config.Type is { Compiled: { } compiled } type ? (DefinitionRef(type, compiled), type) : (null, null);
    }

    private static DocumentDefinitionRef DefinitionRef(EffectiveType type, Definitions.Compilation.CompiledDefinition compiled)
    {
        using var formSpec = System.Text.Json.JsonDocument.Parse(compiled.FormSpecJson);
        return new DocumentDefinitionRef(type.TypeKey, type.Version, type.VersionId, type.Definition.Name, formSpec.RootElement.Clone());
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
            // inline, and under a constant of the allow-list, never the stored string ("image/png,text/html" would be
            // parsed as HTML by a browser); everything else (HTML, SVG, Office, archives) downloads as opaque bytes, so
            // a crafted upload can never run as a page of this origin. The lookup is on the bare media type (no
            // parameters, lower case): "image/svg+xml; charset=utf-8" is SVG.
            var inlineType = InlineContentType(ItemKinds.Normalize(item.MimeType));
            var disposition = new ContentDispositionHeaderValue(inlineType is null ? "attachment" : "inline");
            disposition.SetHttpFileName(item.FileName);
            context.Response.Headers.ContentDisposition = disposition.ToString();
            NoSniff(context);
            return TypedResults.Stream(stream, inlineType ?? ItemKinds.DefaultContentType, entityTag: new EntityTagHeaderValue($"\"{reference.Sha256}\""));
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
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
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

        // docs/11 §11: a document without a definition or without captured data is not approved (it would be published
        // empty); the detail's approvalBlocked says the same before the click. Gated by configurableTypes.
        if (await ApprovalBlockAsync(document, configs, features, ct) is { } block)
        {
            return ApprovalRefused(document, block);
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        var change = new DocumentStatusChange(document.Status, DocumentStatus.Approved);
        var claimed = await InTransactionAsync(db, async cancel =>
        {
            var updated = await db.Documents
                .Where(d => d.Id == id && d.Status == change.From)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.Status, DocumentStatus.Approved)
                        .SetProperty(d => d.ApprovedAt, (DateTimeOffset?)now)
                        .SetProperty(d => d.ApprovedBy, actor)
                        .SetProperty(d => d.UpdatedAt, now)
                        .SetProperty(d => d.Version, d => d.Version + 1),
                    cancel);
            if (updated == 0)
            {
                return false;
            }

            document.Status = DocumentStatus.Approved;
            await CloseOpenTasksAsync(db, document.Id, ReviewTaskStatus.Done, now, cancel);
            DocumentQueries.AddEvent(db, document, DocumentEventTypes.Approved, change, actor, new { comment = Clean(request?.Comment, 2000) }, now);
            // The outbox row commits with the decision (docs/11 §13.5): if the process dies before the publish message
            // below is queued, the publish sweeper finds the row and publishes it.
            DocumentOutbox.Add(db, document, now);
            await db.SaveChangesAsync(cancel);
            return true;
        }, ct);
        if (!claimed)
        {
            return ConcurrentChange(id);
        }

        document.Status = DocumentStatus.Approved;
        document.ApprovedAt = now;
        document.ApprovedBy = actor;
        document.UpdatedAt = now;
        document.Version++;

        await queue.EnqueueAsync(new ProcessDocument(document.TenantId, document.Id, PipelineStage.Publish), ct);
        return TypedResults.Ok(await DetailAsync(row, configs, features, ct));
    }

    private static async Task<Results<Ok<DocumentDetail>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        Guid id,
        RejectDocumentRequest request,
        [FromServices] DocProcDbContext db,
        IPipelineQueue queue,
        TimeProvider clock,
        ClaimsPrincipal user,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
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
        var claimed = await InTransactionAsync(db, async cancel =>
        {
            var updated = await db.Documents
                .Where(d => d.Id == id && d.Status == change.From)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.Status, DocumentStatus.Rejected)
                        .SetProperty(d => d.UpdatedAt, now)
                        .SetProperty(d => d.Version, d => d.Version + 1),
                    cancel);
            if (updated == 0)
            {
                return false;
            }

            document.Status = DocumentStatus.Rejected;
            await CloseOpenTasksAsync(db, document.Id, ReviewTaskStatus.Done, now, cancel);
            DocumentQueries.AddEvent(db, document, DocumentEventTypes.Rejected, change, actor, new { reason }, now);
            DocumentOutbox.Add(db, document, now);
            await db.SaveChangesAsync(cancel);
            return true;
        }, ct);
        if (!claimed)
        {
            return ConcurrentChange(id);
        }

        document.Status = DocumentStatus.Rejected;
        document.UpdatedAt = now;
        document.Version++;

        await queue.EnqueueAsync(new ProcessDocument(document.TenantId, document.Id, PipelineStage.Publish), ct);
        return TypedResults.Ok(await DetailAsync(row, configs, features, ct));
    }

    private static async Task<Results<Ok<DocumentDetail>, ValidationProblem, ProblemHttpResult>> ReprocessAsync(
        Guid id,
        ReprocessDocumentRequest? request,
        [FromServices] DocProcDbContext db,
        IPipelineQueue queue,
        TimeProvider clock,
        ClaimsPrincipal user,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
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

        var configMode = string.IsNullOrWhiteSpace(request?.ConfigMode) ? ReprocessConfigModes.Pinned : request.ConfigMode.Trim();
        if (configMode is not (ReprocessConfigModes.Pinned or ReprocessConfigModes.Current))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["configMode"] = [$"Must be {ReprocessConfigModes.Pinned} or {ReprocessConfigModes.Current}."] });
        }

        var unpin = configMode == ReprocessConfigModes.Current;
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
        var claimed = await InTransactionAsync(db, async cancel =>
        {
            var updated = await db.Documents
                .Where(d => d.Id == id && d.Status == change.From)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.Status, target)
                        .SetProperty(d => d.FailureReason, (string?)null)
                        .SetProperty(d => d.UpdatedAt, now)
                        .SetProperty(d => d.Version, d => d.Version + 1)
                        .SetProperty(d => d.DocumentTypeVersionId, d => unpin ? null : d.DocumentTypeVersionId),
                    cancel);
            if (updated == 0)
            {
                return false;
            }

            // A review task for the old extraction is moot; the pipeline opens a new one if the rerun still needs a human.
            await CloseOpenTasksAsync(db, document.Id, ReviewTaskStatus.Skipped, now, cancel);
            DocumentQueries.AddEvent(db, document, DocumentEventTypes.Reprocessed, change, actor, new { fromStage = stage.ToString(), configMode }, now);
            await db.SaveChangesAsync(cancel);
            return true;
        }, ct);
        if (!claimed)
        {
            return ConcurrentChange(id);
        }

        document.Status = target;
        document.FailureReason = null;
        document.UpdatedAt = now;
        document.Version++;
        if (unpin)
        {
            document.DocumentTypeVersionId = null;
        }

        await queue.EnqueueAsync(new ProcessDocument(document.TenantId, document.Id, stage), ct);
        return TypedResults.Ok(await DetailAsync(row, configs, features, ct));
    }

    private static async Task<Results<Ok<DocumentDetail>, ValidationProblem, ProblemHttpResult>> CorrectFieldsAsync(
        Guid id,
        CorrectFieldsRequest request,
        [FromServices] DocProcDbContext db,
        [FromServices] IFieldSchemaResolver schemas,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
        [FromServices] DocumentValidator validator,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request.Corrections is not { Count: > 0 })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["corrections"] = ["At least one correction is required."] });
        }

        if (request.Corrections.Count > MaxCorrectionsPerRequest)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["corrections"] = [$"At most {MaxCorrectionsPerRequest} corrections in one request; save them in parts."] });
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

        // A definition-driven document (docs/11 §5.6, §12.8: CanonicalSchemaVersion 2, or one of a definition-driven type
        // that has no tree yet) is patched by its type version's descriptors, with append and remove; every other one by
        // the coded records, exactly as before.
        var config = await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, document.Type, document.SupplierId, document.DocumentTypeVersionId), ct);
        var compiled = config.Type?.Compiled;
        var definitionDriven = compiled is not null
                               && (document.CanonicalSchemaVersion == DefinitionTree.SchemaVersion
                                   || (string.IsNullOrWhiteSpace(document.CanonicalJson)
                                       && CanonicalStorage.IsDefinitionDriven(config.Type, await features.IsEnabledAsync(document.TenantId, TenantFeature.ConfigurableTypes, ct))));
        var schema = definitionDriven ? null : schemas.RootFor(document.Type);
        if (!definitionDriven && (schema is null || string.IsNullOrWhiteSpace(document.CanonicalJson)))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Nothing to correct", detail: $"Document {document.Id} ({document.Type}, {document.Status}) has no canonical data yet.");
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        var tenantCurrency = await db.Tenants.AsNoTracking().Where(t => t.Id == document.TenantId).Select(t => t.DefaultCurrency).FirstOrDefaultAsync(ct);

        CanonicalPatchResult patched;
        try
        {
            patched = definitionDriven
                ? DefinitionPatcher.Apply(compiled!.Descriptors, DefinitionTreeOf(document.CanonicalJson, compiled.Descriptors), request.Corrections, actor, now, tenantCurrency, typedRecord: document.Type.IsInvoiceFamily)
                : CanonicalPatcher.Apply(schema!, document.CanonicalJson!, request.Corrections, actor, now, tenantCurrency);
        }
        catch (CanonicalPatchException ex)
        {
            var key = string.IsNullOrEmpty(ex.Path) ? "corrections" : ex.Path;
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { [key] = [ex.Message] });
        }

        document.CanonicalJson = patched.Json;
        if (definitionDriven)
        {
            document.CanonicalSchemaVersion = DefinitionTree.SchemaVersion;
        }

        document.UpdatedAt = now;
        // The list columns are always rewritten from the canonical JSON (docs/02 §2), so a correction shows in the inbox
        // too, under the role bindings of the type version the document is pinned to; so is the field index.
        DocumentRoleColumns.Refresh(document, config.Type?.Roles);
        await DocumentFieldIndexWriter.RefreshAsync(db, document, config.Type?.IndexedFields ?? [], ct);

        foreach (var applied in patched.Applied)
        {
            db.Corrections.Add(new Correction
            {
                TenantId = document.TenantId,
                DocumentId = document.Id,
                FieldPath = applied.Path,
                Op = applied.Op,
                OldValueJson = applied.OldValueJson,
                NewValueJson = applied.NewValueJson,
                SourceJson = CorrectionSources.ToJson(applied.Source, applied.From),
                DocumentTypeVersionId = document.DocumentTypeVersionId,
                By = actor,
                At = now,
                Reason = Clean(applied.Reason, 500),
            });
        }

        // A reviewer who replaced every row of a group wrote it from another source than the table tool: its table
        // report no longer describes the rows (docs/11 §6.2).
        if (definitionDriven)
        {
            foreach (var replaced in patched.Applied.Where(a => a.Op == CorrectionOps.Set && compiled!.Descriptors.Find(a.Path) is { Kind: Definitions.FieldKinds.Array }).Select(a => a.Path).Distinct(StringComparer.Ordinal))
            {
                await DocumentTableReports.RemoveAsync(db, document.Id, replaced, ct);
            }
        }

        DocumentQueries.AddEvent(db, document, DocumentEventTypes.Corrected, null, actor, new { paths = patched.Applied.Select(a => a.Path).ToList(), ops = patched.Applied.Select(a => a.Op).Distinct(StringComparer.Ordinal).ToList() }, now);
        await db.SaveChangesAsync(ct);

        // docs/11 §12.8: a definition-driven document is re-validated inline (the structural check and the rules of its
        // type version), so the flags a correction answers go and the ones it causes show at once. After the save, so
        // the checks (the duplicate check reads the field index) see the corrected document.
        if (definitionDriven && config.Type is { } type)
        {
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == document.TenantId, ct);
            var run = await DocProc.Pipeline.Support.DocumentChecks.RunAsync(db, document, type, tenant, DocProc.Pipeline.Support.DocumentChecks.TodayFor(tenant, now), validator, ct);
            DocProc.Pipeline.Support.DocumentChecks.ReplaceFlags(db, document, DocProc.Pipeline.Support.DocumentChecks.OwnedFlags, run.Flags, now);
            await db.SaveChangesAsync(ct);
            row = row with
            {
                BlockingFlagCount = document.Flags.Count(f => f.ResolvedAt == null && f.Severity == Domain.Flags.FlagSeverity.Blocking),
                WarningFlagCount = document.Flags.Count(f => f.ResolvedAt == null && f.Severity == Domain.Flags.FlagSeverity.Warning),
            };
        }

        return TypedResults.Ok(await DetailAsync(row, configs, features, ct));
    }

    /// <summary>The stored tree of a definition-driven document, completed with the fields of its version; an empty tree when it has none yet.</summary>
    private static string DefinitionTreeOf(string? canonicalJson, Definitions.Compilation.DescriptorIndex descriptors)
    {
        System.Text.Json.Nodes.JsonObject? tree = null;
        if (!string.IsNullOrWhiteSpace(canonicalJson))
        {
            try
            {
                tree = System.Text.Json.Nodes.JsonNode.Parse(canonicalJson) as System.Text.Json.Nodes.JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                throw new CanonicalPatchException(string.Empty, "The stored canonical document is not JSON.");
            }
        }

        if (tree is null)
        {
            return DefinitionTree.CreateEmpty(descriptors).ToJsonString();
        }

        return DefinitionTree.Complete(tree, descriptors) ? tree.ToJsonString() : canonicalJson!;
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

    /// <summary>
    /// Runs a decision's writes in one transaction under the context's execution strategy. On SQL Server the context
    /// retries transient failures (<c>EnableRetryOnFailure</c>), and that strategy refuses a transaction it does not run
    /// itself ("does not support user-initiated transactions"), so approve, reject and reprocess answered 500 there
    /// while the SQLite tests passed. Each attempt starts from an empty change tracker, so a retry does not save the
    /// failed attempt's rows a second time, and <paramref name="work"/> sets every value it writes rather than
    /// incrementing one. It returns false when the document is no longer in the expected state, and the transaction
    /// is rolled back. A retry after a commit whose acknowledgement was lost finds the document already moved and
    /// reports a concurrent change: the decision stands, and its outbox row with it.
    /// </summary>
    private static Task<bool> InTransactionAsync(DocProcDbContext db, Func<CancellationToken, Task<bool>> work, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(
            async cancel =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancel);
                if (!await work(cancel))
                {
                    return false;
                }

                await transaction.CommitAsync(cancel);
                return true;
            },
            ct);

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

    /// <summary>
    /// Only PDFs and raster images are rendered by the viewer; they are served inline under the type this map gives,
    /// a constant, so nothing the uploader wrote reaches the Content-Type header. SVG can carry script and is not
    /// here, so it downloads like everything else; so does every other <c>image/*</c> type the viewer cannot show.
    /// </summary>
    private static readonly FrozenDictionary<string, string> InlineContentTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["application/pdf"] = "application/pdf",
        ["application/x-pdf"] = "application/pdf",
        ["image/png"] = "image/png",
        ["image/jpeg"] = "image/jpeg",
        ["image/jpg"] = "image/jpeg",
        ["image/pjpeg"] = "image/jpeg",
        ["image/gif"] = "image/gif",
        ["image/webp"] = "image/webp",
        ["image/tiff"] = "image/tiff",
        ["image/bmp"] = "image/bmp",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The constant type to serve an original inline under, or null when it must download. Takes a media type normalised by <see cref="ItemKinds.Normalize"/>.</summary>
    private static string? InlineContentType(string mediaType) => InlineContentTypes.GetValueOrDefault(mediaType);

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

    /// <summary>
    /// The <c>type</c> filter (docs/11 §18): any type key, not only the legacy ones; the legacy names in any case, as
    /// the enum filter took them. Only a value that is not of the key syntax is refused.
    /// </summary>
    private static List<DocumentTypeKey> ParseTypeKeys(string? csv, Dictionary<string, string[]> errors)
    {
        var values = new List<DocumentTypeKey>();
        foreach (var token in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DocumentTypeKey.TryParse(token, out var key))
            {
                values.Add(key);
            }
            else
            {
                errors["type"] = [$"'{token}' is not a document type key (a letter, then letters, digits, '_', '.' or '-', at most {DocumentTypeKey.MaxLength})."];
            }
        }

        return values;
    }

    /// <summary>The prefix of the indexed-field filters of the document list (docs/11 §18).</summary>
    public const string FieldFilterPrefix = "field.";

    /// <summary>
    /// The most indexed-field filters one list request may carry. Each one is a correlated EXISTS over the field index,
    /// and the cost of compiling the query plan grows fast with their number (a few dozen keep SQL Server busy for
    /// seconds and then fail), so the count is capped like <see cref="MaxFieldColumns"/>.
    /// </summary>
    public const int MaxFieldFilters = 5;

    /// <summary>The error key of a list request with more than <see cref="MaxFieldFilters"/> field filters.</summary>
    public const string FieldFiltersErrorKey = FieldFilterPrefix + "*";

    private static List<DocumentFieldFilter> ParseFieldFilters(IQueryCollection query, Dictionary<string, string[]> errors)
    {
        var filters = new List<DocumentFieldFilter>();
        var requested = query.Where(p => p.Key.StartsWith(FieldFilterPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (requested.Count > MaxFieldFilters)
        {
            errors[FieldFiltersErrorKey] = [$"At most {MaxFieldFilters} field filters."];
            return filters;
        }

        foreach (var (name, values) in requested)
        {
            if (values.Count > 1)
            {
                errors[name] = ["One value per field filter."];
                continue;
            }

            var key = name[FieldFilterPrefix.Length..];
            if (key.Length is 0 or > DocumentFieldIndexEntry.MaxFieldKeyLength || !FieldKeyPattern().IsMatch(key))
            {
                errors[name] = ["Not an indexed field path (field.<key>, for example field.supplierCustomerRef or field.lines[*].supplierSku)."];
                continue;
            }

            var raw = values.ToString().Trim();
            if (raw.Length == 0)
            {
                errors[name] = ["A value is required."];
                continue;
            }

            // A number larger than the index holds is compared as text only (DocumentFieldFilter.Of), not sent as a
            // decimal(28,8) parameter it would overflow on SQL Server.
            filters.Add(DocumentFieldFilter.Of(key, raw));
        }

        return filters;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*(\[\*\])?(\.[A-Za-z_][A-Za-z0-9_-]*(\[\*\])?)*$")]
    private static partial System.Text.RegularExpressions.Regex FieldKeyPattern();

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

/// <summary>The <c>configMode</c> values of <c>:reprocess</c> (docs/11 §18).</summary>
public static class ReprocessConfigModes
{
    public const string Pinned = "pinned";
    public const string Current = "current";
}
