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
/// level, links exact duplicates (same tenant, same SHA-256) to the strictly earlier item, ignores obvious
/// non-documents with a reason, and creates one <c>Received</c> document per remaining candidate. All rows are
/// committed together and the documents are queued for stage 2 only after the commit, so a crash leaves the
/// ingestion <c>Received</c> and a retry starts over.
/// </summary>
internal sealed class IngestHandler(
    DocProcDbContext db,
    PipelineBlobs blobs,
    IPipelineQueue queue,
    DocumentTimeline timeline,
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
        var pending = new Queue<IngestionItem>(items.Where(i => i.Disposition == ItemDisposition.Candidate));
        var acceptedByHash = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var toEnqueue = new List<ProcessDocument>();
        var created = 0;

        while (pending.TryDequeue(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Kind = ItemKinds.Detect(item.MimeType, item.FileName);

            if (item.Kind == ItemKind.Archive)
            {
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
                continue;
            }

            if (IgnoreReason(item) is { } reason)
            {
                Ignore(item, reason);
                continue;
            }

            var earlier = await FindEarlierAsync(ingestion, item, acceptedByHash, cancellationToken).ConfigureAwait(false);
            if (earlier is { } duplicateOf)
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

        ingestion.Status = IngestionStatus.Unpacked;
        ingestion.Attempts = message.Attempt;
        ingestion.LastError = null;
        ingestion.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Another writer (a twin message on another node, or an operator) changed the ingestion after this one
            // loaded it: its outcome stands, and nothing of this attempt is committed or queued.
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(ex, "Ingestion {IngestionId} was changed by another writer while attempt {Attempt} ran; message dropped as already handled.", ingestion.Id, message.Attempt);
            }

            return;
        }

        foreach (var next in toEnqueue)
        {
            await queue.EnqueueAsync(next, cancellationToken).ConfigureAwait(false);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Ingestion {IngestionId}: {Items} item(s), {Documents} document(s) created.", ingestion.Id, items.Count, created);
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
    /// The earliest accepted item with the same bytes: in this ingestion first, then any other ingestion of the tenant.
    /// Only items strictly before this one count (older <c>CreatedAt</c>, or the same instant and a smaller v7 id):
    /// two ingestions with the same bytes running at the same time must agree on the one that owns the document,
    /// and an older item is never the duplicate of a newer one.
    /// </summary>
    private async Task<Guid?> FindEarlierAsync(Ingestion ingestion, IngestionItem item, Dictionary<string, Guid> acceptedByHash, CancellationToken cancellationToken)
    {
        if (acceptedByHash.TryGetValue(item.Sha256, out var inThisIngestion))
        {
            return inThisIngestion;
        }

        // The tie on CreatedAt is broken in memory: .NET orders v7 GUIDs by their timestamp prefix, SQL Server's
        // uniqueidentifier ordering does not, so the comparison never goes to the database.
        var candidates = await db.IngestionItems.AsNoTracking()
            .Where(i => i.Sha256 == item.Sha256 && i.IngestionId != ingestion.Id && i.CreatedAt <= item.CreatedAt
                        && i.Disposition != ItemDisposition.Duplicate && i.Disposition != ItemDisposition.Ignored)
            .Select(i => new { i.Id, i.CreatedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates
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
