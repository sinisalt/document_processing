using System.IO.Compression;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 1 and the unpacking half of stage 2 (docs/03): decides what every candidate item is, expands archives one
/// level, links exact duplicates (same tenant, same SHA-256) to the strictly earlier item or to the item that already
/// owns the document, ignores obvious non-documents with a reason, and creates one <c>Received</c> document per
/// remaining candidate. Two units of work: the items unpacked from archives are committed first (as candidates), so a
/// twin ingestion deciding at the same time sees them, then the decisions and documents are committed together and
/// the documents are queued for stage 2 only after that commit. A crash leaves the ingestion <c>Received</c> and a
/// retry starts over from the committed items; the decide-and-commit half runs one ingestion at a time per tenant in
/// this process (<see cref="IngestCommitGate"/>).
/// </summary>
internal sealed class IngestHandler(
    DocProcDbContext db,
    PipelineBlobs blobs,
    IPipelineQueue queue,
    DocumentTimeline timeline,
    IngestCommitGate commitGate,
    IOptions<PipelineOptions> options,
    ILogger<IngestHandler> logger) : IIngestionHandler
{
    private const int MaxReasonLength = 500;
    private readonly PipelineOptions _options = options.Value;

    public async Task HandleAsync(ProcessIngestion message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var ingestion = await db.Ingestions
            .Include(i => i.Items)
            .SingleOrDefaultAsync(i => i.Id == message.IngestionId, cancellationToken)
            .ConfigureAwait(false);

        if (ingestion is null)
        {
            logger.LogWarning("Ingestion {IngestionId} of tenant {TenantId} does not exist; message dropped.", message.IngestionId, message.TenantId);
            return;
        }

        if (ingestion.Status != IngestionStatus.Received)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Ingestion {IngestionId} is {Status}; message (attempt {Attempt}) dropped as already handled.", ingestion.Id, ingestion.Status, message.Attempt);
            }

            return;
        }

        var now = timeline.Now;
        var items = ingestion.Items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToList();

        // Unit of work 1: the archives. Their files become candidate rows of this ingestion before anything decides
        // on them, so another ingestion carrying the same bytes finds them like it finds the items the API stored.
        var unpacked = await UnpackArchivesAsync(ingestion, items, now, cancellationToken).ConfigureAwait(false);
        if (unpacked > 0)
        {
            // The ingestion is touched too: its version token bumps, so a twin of this message on another node that
            // loaded the same ingestion fails its own first commit and inserts no second set of children.
            ingestion.Attempts = message.Attempt;
            ingestion.UpdatedAt = now;
            if (!await TryCommitAsync(ingestion, message.Attempt, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        // Unit of work 2: dispositions and documents, one ingestion at a time per tenant in this process.
        var toEnqueue = new List<ProcessDocument>();
        using (await commitGate.EnterAsync(ingestion.TenantId, cancellationToken).ConfigureAwait(false))
        {
            var created = await CreateDocumentsAsync(ingestion, items, now, toEnqueue, cancellationToken).ConfigureAwait(false);

            ingestion.Status = IngestionStatus.Unpacked;
            ingestion.Attempts = message.Attempt;
            ingestion.LastError = null;
            ingestion.UpdatedAt = now;
            if (!await TryCommitAsync(ingestion, message.Attempt, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Ingestion {IngestionId}: {Items} item(s), {Documents} document(s) created.", ingestion.Id, items.Count, created);
            }
        }

        foreach (var next in toEnqueue)
        {
            await queue.EnqueueAsync(next, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Detects every candidate's kind and expands the archives among them, nested ones included up to the depth limit;
    /// the files become candidate items of the ingestion. Returns how many were added.
    /// </summary>
    private async Task<int> UnpackArchivesAsync(Ingestion ingestion, List<IngestionItem> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = new Queue<IngestionItem>(items.Where(i => i.Disposition == ItemDisposition.Candidate));
        var unpacked = 0;

        while (pending.TryDequeue(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Kind = ItemKinds.Detect(item.MimeType, item.FileName);
            if (item.Kind != ItemKind.Archive)
            {
                continue;
            }

            if (Depth(item, items) >= _options.MaxArchiveDepth)
            {
                Ignore(item, $"nested archive beyond depth {_options.MaxArchiveDepth}");
                continue;
            }

            List<IngestionItem> children;
            try
            {
                children = await ExpandArchiveAsync(ingestion, item, now, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                // Not a zip after all, or one that is truncated or corrupt: nothing can be unpacked from it.
                logger.LogWarning(ex, "Item {ItemId} ({FileName}) is not a readable zip archive; ignored.", item.Id, item.FileName);
                Ignore(item, $"unreadable zip archive: {ex.Message}");
                continue;
            }

            foreach (var child in children)
            {
                items.Add(child);
                db.IngestionItems.Add(child);
                pending.Enqueue(child);
            }

            item.Disposition = ItemDisposition.Unpacked;
            item.DispositionReason = $"{children.Count} file(s) unpacked";
            unpacked += children.Count;
        }

        return unpacked;
    }

    /// <summary>Decides every remaining candidate, in the order the duplicate rule ranks them, and creates the documents.</summary>
    private async Task<int> CreateDocumentsAsync(Ingestion ingestion, List<IngestionItem> items, DateTimeOffset now, List<ProcessDocument> toEnqueue, CancellationToken cancellationToken)
    {
        var acceptedByHash = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var created = 0;

        foreach (var item in items.Where(i => i.Disposition == ItemDisposition.Candidate).OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IgnoreReason(item) is { } reason)
            {
                Ignore(item, reason);
                continue;
            }

            var original = await FindOriginalAsync(ingestion, item, acceptedByHash, cancellationToken).ConfigureAwait(false);
            if (original is { } duplicateOf)
            {
                item.Disposition = ItemDisposition.Duplicate;
                item.DuplicateOfItemId = duplicateOf;
                item.DispositionReason = "exact duplicate of an earlier item (same bytes)";
                continue;
            }

            acceptedByHash[item.Sha256] = item.Id;
            var document = new Document
            {
                Id = Guid.CreateVersion7(),
                TenantId = ingestion.TenantId,
                IngestionItemId = item.Id,
                PageStart = 1,
                PageEnd = 1,
                PageCount = 1,
                Status = DocumentStatus.Received,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Documents.Add(document);
            timeline.Record(document, DocumentEventTypes.Received, null, DocumentStatus.Received, new
            {
                ingestionId = ingestion.Id,
                itemId = item.Id,
                fileName = item.FileName,
                mimeType = item.MimeType,
                kind = item.Kind,
                sizeBytes = item.SizeBytes,
            });
            toEnqueue.Add(new ProcessDocument(ingestion.TenantId, document.Id, PipelineStage.Normalize));
            created++;
        }

        return created;
    }

    /// <summary>
    /// Commits the unit of work; false when another writer (a twin message on another node, or an operator) changed
    /// the ingestion after this handler loaded it: its outcome stands, and nothing of this unit is committed or queued.
    /// </summary>
    private async Task<bool> TryCommitAsync(Ingestion ingestion, int attempt, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(ex, "Ingestion {IngestionId} was changed by another writer while attempt {Attempt} ran; message dropped as already handled.", ingestion.Id, attempt);
            }

            return false;
        }
    }

    private string? IgnoreReason(IngestionItem item)
    {
        if (ItemKinds.IsCalendar(item.MimeType, item.FileName))
        {
            return "calendar invitation";
        }

        if (item.Kind == ItemKind.Image && item.SizeBytes < _options.MinimumImageBytes)
        {
            return $"image under {_options.MinimumImageBytes / 1024} KB (signature, logo or tracking pixel)";
        }

        return null;
    }

    private static void Ignore(IngestionItem item, string reason)
    {
        item.Disposition = ItemDisposition.Ignored;
        item.DispositionReason = reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];
    }

    /// <summary>How many archives the item sits inside: 0 for a direct attachment, 1 for a file from a zip, and so on.</summary>
    private static int Depth(IngestionItem item, List<IngestionItem> items)
    {
        var depth = 0;
        var parentId = item.ParentItemId;
        while (parentId is { } id && depth < 16)
        {
            depth++;
            parentId = items.Find(i => i.Id == id)?.ParentItemId;
        }

        return depth;
    }

    /// <summary>
    /// The item this one duplicates, or null when it owns its document: the accepted item with the same bytes in this
    /// ingestion first, then, in any other ingestion of the tenant, the earliest item that already owns a document for
    /// the bytes, else the earliest item strictly before this one (older <c>CreatedAt</c>, or the same instant and a
    /// smaller v7 id). Two ingestions with the same bytes deciding at the same time thus agree on the owner, an older
    /// item is never the duplicate of a newer one that is still undecided, and a document that exists is never created
    /// a second time, whichever ingestion got there first. Unpacked files follow the same rule as stored attachments;
    /// their <c>CreatedAt</c> is the unpacking time.
    /// </summary>
    private async Task<Guid?> FindOriginalAsync(Ingestion ingestion, IngestionItem item, Dictionary<string, Guid> acceptedByHash, CancellationToken cancellationToken)
    {
        if (acceptedByHash.TryGetValue(item.Sha256, out var inThisIngestion))
        {
            return inThisIngestion;
        }

        // The tie on CreatedAt is broken in memory: .NET orders v7 GUIDs by their timestamp prefix, SQL Server's
        // uniqueidentifier ordering does not, so the comparison never goes to the database.
        var candidates = await db.IngestionItems.AsNoTracking()
            .Where(i => i.Sha256 == item.Sha256 && i.IngestionId != ingestion.Id
                        && i.Disposition != ItemDisposition.Duplicate && i.Disposition != ItemDisposition.Ignored
                        && (i.CreatedAt <= item.CreatedAt || i.Documents.Any()))
            .Select(i => new { i.Id, i.CreatedAt, OwnsDocument = i.Documents.Any() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var owner = candidates
            .Where(c => c.OwnsDocument)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .FirstOrDefault();

        return owner?.Id ?? candidates
            .Where(c => c.CreatedAt < item.CreatedAt || (c.CreatedAt == item.CreatedAt && c.Id.CompareTo(item.Id) < 0))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .FirstOrDefault()?.Id;
    }

    /// <summary>Unpacks one level of the archive; throws <see cref="InvalidDataException"/> when the bytes are not a readable zip.</summary>
    private async Task<List<IngestionItem>> ExpandArchiveAsync(Ingestion ingestion, IngestionItem archiveItem, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var bytes = await blobs.ReadAsync(archiveItem.Blob, cancellationToken).ConfigureAwait(false);
        var children = new List<IngestionItem>();
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0 || entry.Length == 0)
            {
                continue; // directory entry or empty file
            }

            if (children.Count >= _options.MaxArchiveEntries)
            {
                logger.LogWarning("Archive {FileName} has more than {Max} entries; the rest are skipped.", archiveItem.FileName, _options.MaxArchiveEntries);
                break;
            }

            if (entry.Length > _options.MaxArchiveEntryBytes)
            {
                logger.LogWarning("Archive entry {Entry} in {FileName} is {Size} bytes, over the limit; skipped.", entry.FullName, archiveItem.FileName, entry.Length);
                continue;
            }

            // The declared length is not trusted: the copy stops as soon as the inflated bytes pass the limit.
            byte[]? content;
            await using (var entryStream = entry.Open())
            {
                content = await BoundedCopy.ReadAsync(entryStream, _options.MaxArchiveEntryBytes, cancellationToken).ConfigureAwait(false);
            }

            if (content is null)
            {
                logger.LogWarning("Archive entry {Entry} in {FileName} declares {Declared} bytes but inflates past the {Max} byte limit; skipped.", entry.FullName, archiveItem.FileName, entry.Length, _options.MaxArchiveEntryBytes);
                continue;
            }

            var mimeType = ItemKinds.MimeTypeFor(entry.Name);
            var stored = await blobs.PutRawAsync(ingestion.TenantId, content, mimeType, cancellationToken).ConfigureAwait(false);
            children.Add(new IngestionItem
            {
                Id = Guid.CreateVersion7(),
                TenantId = ingestion.TenantId,
                IngestionId = ingestion.Id,
                ParentItemId = archiveItem.Id,
                FileName = entry.Name,
                MimeType = mimeType,
                SizeBytes = content.Length,
                Sha256 = stored.Sha256,
                Blob = PipelineBlobs.ToEntity(stored),
                Kind = ItemKinds.Detect(mimeType, entry.Name),
                Disposition = ItemDisposition.Candidate,
                CreatedAt = now,
            });
        }

        return children;
    }
}
