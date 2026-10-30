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
/// level, links exact duplicates (same tenant, same SHA-256) to the earlier item, ignores obvious non-documents with
/// a reason, and creates one <c>Received</c> document per remaining candidate. All rows are committed together and
/// the documents are queued for stage 2 only after the commit, so a crash leaves the ingestion <c>Received</c> and
/// a retry starts over.
/// </summary>
internal sealed class IngestHandler(
    DocProcDbContext db,
    PipelineBlobs blobs,
    IPipelineQueue queue,
    DocumentTimeline timeline,
    IOptions<PipelineOptions> options,
    ILogger<IngestHandler> logger) : IIngestionHandler
{
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

                var children = await ExpandArchiveAsync(ingestion, item, now, cancellationToken).ConfigureAwait(false);
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
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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
        item.DispositionReason = reason;
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

    /// <summary>The earlier accepted item with the same bytes: in this ingestion first, then any other ingestion of the tenant.</summary>
    private async Task<Guid?> FindEarlierAsync(Ingestion ingestion, IngestionItem item, Dictionary<string, Guid> acceptedByHash, CancellationToken cancellationToken)
    {
        if (acceptedByHash.TryGetValue(item.Sha256, out var inThisIngestion))
        {
            return inThisIngestion;
        }

        var candidates = await db.IngestionItems.AsNoTracking()
            .Where(i => i.Sha256 == item.Sha256 && i.IngestionId != ingestion.Id && i.Disposition != ItemDisposition.Duplicate && i.Disposition != ItemDisposition.Ignored)
            .Select(i => new { i.Id, i.CreatedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).FirstOrDefault()?.Id;
    }

    private async Task<List<IngestionItem>> ExpandArchiveAsync(Ingestion ingestion, IngestionItem archiveItem, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var bytes = await blobs.ReadAsync(archiveItem.Blob, cancellationToken).ConfigureAwait(false);
        var children = new List<IngestionItem>();
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Item {ItemId} ({FileName}) is not a readable zip archive.", archiveItem.Id, archiveItem.FileName);
            return children;
        }

        using (archive)
        {
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

                byte[] content;
                await using (var entryStream = entry.Open())
                {
                    using var buffer = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
                    await entryStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                    content = buffer.ToArray();
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
        }

        return children;
    }
}
