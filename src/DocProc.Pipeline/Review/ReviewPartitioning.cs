using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Pdf;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Stages;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Review;

/// <summary>A part of a review split: its pages (1-based within the document, consecutive) and, when the reviewer knows it, its type.</summary>
/// <param name="Pages">The part's pages.</param>
/// <param name="Type">The type the part has; null to classify it.</param>
/// <param name="TypeVersionId">The type version it is pinned to (the tenant's published version), when typed.</param>
public sealed record ReviewSplitPart(IReadOnlyList<int> Pages, DocumentTypeKey? Type, Guid? TypeVersionId);

/// <summary>A document a review split or join left behind, and the stage it was queued for.</summary>
public sealed record ReviewPartOutcome(Guid DocumentId, IReadOnlyList<int> Pages, DocumentTypeKey? Type, DocumentStatus Status, PipelineStage Stage);

/// <summary>A split or join that cannot be done as asked; the message says why.</summary>
public sealed class ReviewActionException(string message) : InvalidOperationException(message);

/// <summary>
/// The page operations of review (docs/11 §12.7), next to the pipeline's own split (<see cref="SplitHandler"/>, whose
/// slicing they reuse): a document of one attachment is cut into several documents, or parts of one attachment are
/// joined back into one. Both change only tracked rows and write new text layers to the derived store; the caller
/// saves (the documents' version tokens guard against a concurrent writer) and then queues the returned stages.
/// <list type="bullet">
/// <item><description>Pages: a part keeps the page images of its pages and a text layer of its own pages, renumbered
/// from 1; no part keeps the normalised PDF (it holds every page), until a join makes the document the whole
/// attachment again. The item's page range of each document
/// (<c>PageStart</c>, <c>PageEnd</c>) is what a later reprocess from Normalize renders again.</description></item>
/// <item><description>Data: what the document held was captured for its old pages, so its canonical data, list columns
/// and field index are cleared and its pipeline flags dropped; the parts are classified (or, with a type, extracted)
/// again. Corrections stay on the document they were made on, as its audit; nothing is copied to a new part.</description></item>
/// <item><description>Reviews: the open review tasks of every document involved are closed as Skipped; the pipeline
/// opens new ones where a part still needs a reviewer.</description></item>
/// <item><description>Outbox: only documents that are not settled are split or joined, so no decision of theirs is
/// waiting to be published; earlier settlements' outbox rows are untouched.</description></item>
/// </list>
/// </summary>
public static class ReviewPartitioning
{
    /// <summary>The Correction path of a split or a join: the operation is on the document's pages, not on a field.</summary>
    public const string PagesPath = "pages";

    /// <summary>
    /// Cuts <paramref name="document"/> (tracked, with its flags) into <paramref name="parts"/>. The first part keeps the
    /// document; every further part becomes a new document of the same ingestion item. An untyped part goes to Classify
    /// (status <c>Split</c>); a typed one is <c>TypeSource.Human</c>, pinned to its version, keeps the supplier and goes
    /// to Extract (status <c>Matched</c>). The parts must cover the document's pages in order, each once.
    /// </summary>
    public static async Task<IReadOnlyList<ReviewPartOutcome>> SplitAsync(
        DocProcDbContext db,
        IContainerBlobStore store,
        PdfProcessingOptions pdfOptions,
        Document document,
        IReadOnlyList<ReviewSplitPart> parts,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parts);
        CheckPartition(document, parts);

        var blobs = new PipelineBlobs(store);
        var textLayer = await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false);
        var imageKeys = document.PageImageKeys.ToList();
        var pageStart = document.PageStart;
        var inherited = document.Flags
            .Where(f => f.Code == FlagCodes.UnreadablePages && f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null)
            .Select(f => f.ToDomain())
            .ToList();
        var splitFlag = new DocumentFlag(
            FlagCodes.MultiDocumentSplit,
            FlagSeverity.Info,
            $"A reviewer split the document into {parts.Count} documents.",
            Params: FlagParams.Of(("count", FlagParams.Number(parts.Count)), (FlagParams.Reason, "review")));

        var outcomes = new List<ReviewPartOutcome>(parts.Count);
        var ids = new List<Guid> { document.Id };
        var created = new List<(Document Child, ReviewSplitPart Part)>();
        foreach (var part in parts.Skip(1))
        {
            var range = RangeOf(part);
            var child = new Document
            {
                Id = Guid.CreateVersion7(),
                TenantId = document.TenantId,
                IngestionItemId = document.IngestionItemId,
                PageStart = pageStart + range.StartPage - 1,
                PageEnd = pageStart + range.EndPage - 1,
                PageCount = range.EndPage - range.StartPage + 1,
                NormalizedBlob = null,
                PageImageKeys = Slice(imageKeys, range),
                SupplierId = document.SupplierId,
                SupplierMatchConfidence = document.SupplierMatchConfidence,
                SupplierMatchMethod = document.SupplierMatchMethod,
                Status = DocumentStatus.Received,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var (layer, text) = await SliceTextLayerAsync(blobs, document.TenantId, textLayer, range, cancellationToken).ConfigureAwait(false);
            child.TextLayerBlob = layer;
            db.Documents.Add(child);
            ids.Add(child.Id);
            created.Add((child, part));
            AddFlags(db, child, [splitFlag, .. PartFlags(pdfOptions, text, inherited)], now);
        }

        var details = new
        {
            parts = parts.Select((p, i) => new { documentId = ids[i], pages = p.Pages, typeKey = p.Type?.Value }).ToList(),
            by = "review",
        };

        foreach (var (child, part) in created)
        {
            AddEvent(db, child, DocumentEventTypes.Received, null, DocumentStatus.Received, actor, new { splitFrom = document.Id, pageStart = child.PageStart, pageEnd = child.PageEnd, by = "review" }, now);
            Move(db, child, DocumentStatus.Normalized, actor, new { inheritedFrom = document.Id }, now);
            Move(db, child, DocumentStatus.Split, actor, details, now);
            if (part.Type is { } type)
            {
                Typed(db, child, type, part.TypeVersionId, actor, now);
            }

            outcomes.Add(new ReviewPartOutcome(child.Id, part.Pages, part.Type, child.Status, part.Type is null ? PipelineStage.Classify : PipelineStage.Extract));
        }

        // The document itself becomes the first part.
        var first = parts[0];
        var firstRange = RangeOf(first);
        var before = new { pageStart = document.PageStart, pageEnd = document.PageEnd };
        document.PageEnd = pageStart + firstRange.EndPage - 1;
        document.PageCount = firstRange.EndPage - firstRange.StartPage + 1;
        document.NormalizedBlob = null;
        document.PageImageKeys = Slice(imageKeys, firstRange);
        var (firstLayer, firstText) = await SliceTextLayerAsync(blobs, document.TenantId, textLayer, firstRange, cancellationToken).ConfigureAwait(false);
        document.TextLayerBlob = firstLayer;
        await ClearCapturedAsync(db, document, cancellationToken).ConfigureAwait(false);
        DropPipelineFlags(db, document);
        AddFlags(db, document, [splitFlag, .. PartFlags(pdfOptions, firstText, inherited)], now);
        await CloseOpenTasksAsync(db, [document.Id], now, cancellationToken).ConfigureAwait(false);
        AddCorrection(db, document, CorrectionOpSplit, before, details, actor, now);

        var from = document.Status;
        var target = first.Type is null ? DocumentStatus.Split : DocumentStatus.Matched;
        document.Type = first.Type ?? DocumentTypeKey.Unknown;
        document.TypeSource = first.Type is null ? null : TypeSource.Human;
        document.TypeConfidence = first.Type is null ? null : 1.0;
        document.DocumentTypeVersionId = first.Type is null ? null : first.TypeVersionId;
        document.Status = target;
        document.FailureReason = null;
        document.UpdatedAt = now;
        AddEvent(db, document, DocumentEventTypes.Split, from, target, actor, details, now);
        outcomes.Insert(0, new ReviewPartOutcome(document.Id, first.Pages, first.Type, target, first.Type is null ? PipelineStage.Classify : PipelineStage.Extract));
        return outcomes;
    }

    /// <summary>
    /// Joins <paramref name="others"/> into <paramref name="target"/> (all tracked, with their flags): parts of the same
    /// ingestion item whose page ranges together are one unbroken range. The target takes every page in item order
    /// and goes back to Classify; the others become <c>Cancelled</c> with <c>JoinedIntoDocumentId</c> set.
    /// </summary>
    public static async Task<ReviewPartOutcome> JoinAsync(
        DocProcDbContext db,
        IContainerBlobStore store,
        PdfProcessingOptions pdfOptions,
        Document target,
        IReadOnlyList<Document> others,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(others);
        var all = others.Append(target).OrderBy(d => d.PageStart).ToList();
        if (others.Count == 0 || all.Any(d => d.IngestionItemId != target.IngestionItemId || d.TenantId != target.TenantId))
        {
            throw new ReviewActionException("Only parts of the same attachment can be joined.");
        }

        for (var i = 1; i < all.Count; i++)
        {
            if (all[i].PageStart != all[i - 1].PageEnd + 1)
            {
                throw new ReviewActionException($"The parts do not make one unbroken run of pages: pages {all[i - 1].PageEnd + 1} to {all[i].PageStart - 1} of the attachment belong to none of them. Join the part in between too.");
            }
        }

        if (all.Any(d => d.PageImageKeys.Count != d.PageCount))
        {
            throw new ReviewActionException("Every part must have its rendered pages; reprocess a part that never got past Normalize first.");
        }

        var blobs = new PipelineBlobs(store);
        var pages = new List<TextPage>();
        TextLayerSource? source = null;
        var offset = 0;
        foreach (var part in all)
        {
            if (await blobs.ReadTextLayerAsync(part, cancellationToken).ConfigureAwait(false) is { } layer)
            {
                source ??= layer.Source;
                pages.AddRange(layer.Pages.OrderBy(p => p.Number).Select(p => p with { Number = p.Number + offset }));
            }

            offset += part.PageCount;
        }

        var joinedLayer = source is { } kind ? new TextLayer(kind, pages) : null;
        var details = new
        {
            joined = others.Select(d => d.Id).ToList(),
            pageStart = all[0].PageStart,
            pageEnd = all[^1].PageEnd,
            by = "review",
        };
        var before = new { pageStart = target.PageStart, pageEnd = target.PageEnd };

        target.PageStart = all[0].PageStart;
        target.PageEnd = all[^1].PageEnd;
        target.PageCount = all.Sum(d => d.PageCount);
        target.PageImageKeys = all.SelectMany(d => d.PageImageKeys).ToList();

        // Joined back into the whole attachment (no other live part of the item is left, and the run starts at its first
        // page), the document is the item again: it gets the normalised file back, as stage 2 gives a whole file, and
        // the stages read it as one (a cancelled part does not count as a part of a split).
        var ids = all.Select(d => d.Id).ToList();
        var whole = all[0].PageStart == 1
                    && !await db.Documents.AnyAsync(d => d.IngestionItemId == target.IngestionItemId && d.Status != DocumentStatus.Cancelled && !ids.Contains(d.Id), cancellationToken).ConfigureAwait(false);
        var item = whole ? await db.IngestionItems.SingleAsync(i => i.Id == target.IngestionItemId, cancellationToken).ConfigureAwait(false) : null;
        target.NormalizedBlob = item is not null && item.Kind != ItemKind.Xml ? PipelineBlobs.Clone(item.Blob) : null;
        target.TextLayerBlob = joinedLayer is null ? null : PipelineBlobs.ToEntity(await blobs.PutTextLayerAsync(target.TenantId, joinedLayer, cancellationToken).ConfigureAwait(false));
        await ClearCapturedAsync(db, target, cancellationToken).ConfigureAwait(false);
        DropPipelineFlags(db, target);
        AddFlags(db, target, PartFlags(pdfOptions, joinedLayer, []), now);
        await CloseOpenTasksAsync(db, [.. all.Select(d => d.Id)], now, cancellationToken).ConfigureAwait(false);
        AddCorrection(db, target, CorrectionOpJoin, before, details, actor, now);

        var from = target.Status;
        target.Type = DocumentTypeKey.Unknown;
        target.TypeSource = null;
        target.TypeConfidence = null;
        target.DocumentTypeVersionId = null;
        target.Status = DocumentStatus.Split;
        target.FailureReason = null;
        target.UpdatedAt = now;
        AddEvent(db, target, DocumentEventTypes.Joined, from, DocumentStatus.Split, actor, details, now);

        foreach (var other in others)
        {
            var otherFrom = other.Status;
            other.Status = DocumentStateMachine.Transition(otherFrom, DocumentStatus.Cancelled);
            other.JoinedIntoDocumentId = target.Id;
            other.UpdatedAt = now;
            // The canonical data stays as the audit of what the part held; the list columns and the field index go,
            // so a cancelled part is never a duplicate, an inbox match or a filter hit.
            ClearColumns(other);
            await DocumentFieldIndexWriter.RefreshAsync(db, other, [], cancellationToken).ConfigureAwait(false);
            AddEvent(db, other, DocumentEventTypes.Joined, otherFrom, DocumentStatus.Cancelled, actor, new { into = target.Id, pageStart = other.PageStart, pageEnd = other.PageEnd, by = "review" }, now);
        }

        return new ReviewPartOutcome(target.Id, [.. Enumerable.Range(1, target.PageCount)], null, DocumentStatus.Split, PipelineStage.Classify);
    }

    private const string CorrectionOpSplit = "split";
    private const string CorrectionOpJoin = "join";

    /// <summary>Every page once, in order, each part a consecutive run.</summary>
    private static void CheckPartition(Document document, IReadOnlyList<ReviewSplitPart> parts)
    {
        if (parts.Count < 2)
        {
            throw new ReviewActionException("A split needs at least two parts.");
        }

        if (document.PageImageKeys.Count != document.PageCount)
        {
            throw new ReviewActionException("The document's pages are not rendered; reprocess it from Normalize first.");
        }

        var expected = 1;
        foreach (var part in parts)
        {
            if (part.Pages.Count == 0)
            {
                throw new ReviewActionException("Every part needs at least one page.");
            }

            foreach (var page in part.Pages)
            {
                if (page != expected)
                {
                    throw new ReviewActionException($"The parts must list the document's pages 1 to {document.PageCount} in order, each once, every part a run of consecutive pages; page {expected} was expected where {page} is.");
                }

                expected++;
            }
        }

        if (expected != document.PageCount + 1)
        {
            throw new ReviewActionException($"The parts end at page {expected - 1}; the document has {document.PageCount} pages, and every page belongs to a part.");
        }
    }

    private static PageRange RangeOf(ReviewSplitPart part) => new(part.Pages[0], part.Pages[^1]);

    private static List<string> Slice(List<string> keys, PageRange range) => keys.GetRange(range.StartPage - 1, range.EndPage - range.StartPage + 1);

    private static async Task<(BlobReference? Blob, TextLayer? Layer)> SliceTextLayerAsync(PipelineBlobs blobs, Guid tenantId, TextLayer? layer, PageRange range, CancellationToken cancellationToken)
    {
        if (layer is null)
        {
            return (null, null);
        }

        var sliced = SplitHandler.Slice(layer, range.StartPage, range.EndPage);
        return (PipelineBlobs.ToEntity(await blobs.PutTextLayerAsync(tenantId, sliced, cancellationToken).ConfigureAwait(false)), sliced);
    }

    /// <summary>A part's "no text layer" flag, by stage 2's rule, as the pipeline's split decides it.</summary>
    private static IEnumerable<DocumentFlag> PartFlags(PdfProcessingOptions pdfOptions, TextLayer? part, IReadOnlyList<DocumentFlag> inherited) =>
        part is null ? inherited
        : pdfOptions.HasTextLayer(part) ? []
        : [NormalizeHandler.NoTextLayer()];

    /// <summary>A typed part of a split skips classification and matching: the reviewer gave the type, the supplier came with the document.</summary>
    private static void Typed(DocProcDbContext db, Document child, DocumentTypeKey type, Guid? versionId, string actor, DateTimeOffset now)
    {
        child.Type = type;
        child.TypeSource = TypeSource.Human;
        child.TypeConfidence = 1.0;
        child.DocumentTypeVersionId = versionId;
        Move(db, child, DocumentStatus.Classified, actor, new { type = type.Value, confidence = 1.0, source = TypeSource.Human, by = "review" }, now);
        Move(db, child, DocumentStatus.Matched, actor, new { supplierId = child.SupplierId, inherited = true, by = "review" }, now);
    }

    private static async Task ClearCapturedAsync(DocProcDbContext db, Document document, CancellationToken cancellationToken)
    {
        document.CanonicalJson = null;
        document.CanonicalSchemaVersion = null;
        document.ExtractionMethod = null;
        document.AppliedConfigJson = null;
        ClearColumns(document);
        await DocumentFieldIndexWriter.RefreshAsync(db, document, [], cancellationToken).ConfigureAwait(false);
        await DocumentTableReports.RemoveAllAsync(db, document.Id, cancellationToken).ConfigureAwait(false);
    }

    private static void ClearColumns(Document document)
    {
        document.DocumentNumber = null;
        document.DocumentNumberNormalized = null;
        document.IssueDate = null;
        document.Currency = null;
        document.TotalGross = null;
        document.ValidFrom = null;
        document.ValidUntil = null;
        document.LineCount = null;
    }

    /// <summary>The open pipeline flags described the document's old pages and data; the stages that run again raise theirs anew. A reviewer's flags stay.</summary>
    private static void DropPipelineFlags(DocProcDbContext db, Document document)
    {
        foreach (var stale in document.Flags.Where(f => f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null).ToList())
        {
            document.Flags.Remove(stale);
            db.DocumentFlags.Remove(stale);
        }
    }

    private static void AddFlags(DocProcDbContext db, Document document, IEnumerable<DocumentFlag> flags, DateTimeOffset now)
    {
        foreach (var flag in flags)
        {
            var record = DocumentFlagRecord.FromDomain(document.TenantId, document.Id, flag, now);
            db.DocumentFlags.Add(record);
            if (!document.Flags.Contains(record))
            {
                document.Flags.Add(record);
            }
        }
    }

    private static async Task CloseOpenTasksAsync(DocProcDbContext db, IReadOnlyList<Guid> documentIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = await db.ReviewTasks
            .Where(t => documentIds.Contains(t.DocumentId) && (t.Status == ReviewTaskStatus.Open || t.Status == ReviewTaskStatus.InProgress))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var task in open)
        {
            task.Status = ReviewTaskStatus.Skipped;
            task.CompletedAt = now;
        }
    }

    private static void AddCorrection(DocProcDbContext db, Document document, string op, object before, object after, string actor, DateTimeOffset now) =>
        db.Corrections.Add(new Correction
        {
            TenantId = document.TenantId,
            DocumentId = document.Id,
            FieldPath = PagesPath,
            Op = op,
            OldValueJson = PipelineJson.Serialize(before),
            NewValueJson = PipelineJson.Serialize(after),
            DocumentTypeVersionId = document.DocumentTypeVersionId,
            By = actor,
            At = now,
        });

    private static void Move(DocProcDbContext db, Document document, DocumentStatus to, string actor, object details, DateTimeOffset now)
    {
        var from = document.Status;
        document.Status = DocumentStateMachine.Transition(from, to);
        document.UpdatedAt = now;
        AddEvent(db, document, DocumentEventTypes.StageCompleted, from, to, actor, details, now);
    }

    private static void AddEvent(DocProcDbContext db, Document document, string eventType, DocumentStatus? from, DocumentStatus? to, string actor, object details, DateTimeOffset now) =>
        db.DocumentEvents.Add(new DocumentEvent
        {
            TenantId = document.TenantId,
            DocumentId = document.Id,
            EventType = eventType,
            FromStatus = from,
            ToStatus = to,
            Actor = actor,
            DetailsJson = PipelineJson.Serialize(details),
            At = now,
        });
}
