using System.IO.Compression;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 1 and the unpacking half of stage 2 (docs/03): decides what every candidate item is, expands archives one
/// level, links exact duplicates (same tenant, same SHA-256) to the item that owns the document or to the strictly
/// earlier item, ignores obvious non-documents with a reason, and creates one <c>Received</c> document per remaining
/// candidate, which becomes <c>Accepted</c>. Two units of work: the items unpacked from archives are committed first
/// (as candidates), so a twin ingestion deciding at the same time sees them, then the decisions and documents are
/// committed together and the documents are queued for stage 2 only after that commit. Ownership is a database rule
/// (<see cref="IngestionItemOwnership"/>): when another process accepted the same bytes between this handler's query
/// and its commit, the commit fails on the unique index, the items concerned are relinked as duplicates of the
/// committed owner, their documents leave the unit of work, and the commit is tried again. A crash leaves the
/// ingestion <c>Received</c> and a retry starts over from the committed items.
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
    private const string DuplicateReason = "exact duplicate of an earlier item (same bytes)";
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

        // Unit of work 2: dispositions and documents. The ownership index decides a race with another process.
        var accepted = await CreateDocumentsAsync(ingestion, items, now, cancellationToken).ConfigureAwait(false);

        ingestion.Status = IngestionStatus.Unpacked;
        ingestion.Attempts = message.Attempt;
        ingestion.LastError = null;
        ingestion.UpdatedAt = now;
        if (!await TryCommitDecisionsAsync(ingestion, items, accepted, message.Attempt, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Ingestion {IngestionId}: {Items} item(s), {Documents} document(s) created.", ingestion.Id, items.Count, accepted.Count);
        }

        foreach (var next in accepted)
        {
            await queue.EnqueueAsync(new ProcessDocument(ingestion.TenantId, next.Document.Id, PipelineStage.Normalize), cancellationToken).ConfigureAwait(false);
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

        // One budget for the whole ingestion, nested archives included; what an earlier attempt committed counts.
        var alreadyUnpacked = items.Where(i => i.ParentItemId is not null).ToList();
        var budget = new UnpackBudget(
            _options.MaxUnpackedBytesPerIngestion - alreadyUnpacked.Sum(i => i.SizeBytes),
            _options.MaxUnpackedItemsPerIngestion - alreadyUnpacked.Count);

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

            if (budget.ExhaustedReason(_options) is { } exhausted)
            {
                Ignore(item, $"not unpacked: {exhausted}");
                continue;
            }

            List<IngestionItem> children;
            int skipped;
            string? stoppedBecause;
            try
            {
                (children, skipped, stoppedBecause) = await ExpandArchiveAsync(ingestion, item, budget, now, cancellationToken).ConfigureAwait(false);
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
            var reason = $"{children.Count} file(s) unpacked";
            if (skipped > 0)
            {
                reason += $"; {skipped} skipped over the entry size or compression limit";
            }

            if (stoppedBecause is not null)
            {
                reason += $"; the rest was not: {stoppedBecause}";
            }

            item.DispositionReason = Truncate(reason);
            unpacked += children.Count;
        }

        return unpacked;
    }

    /// <summary>Decides every remaining candidate, in the order the duplicate rule ranks them, and creates the documents of the accepted ones.</summary>
    private async Task<List<AcceptedItem>> CreateDocumentsAsync(Ingestion ingestion, List<IngestionItem> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var acceptedByHash = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var accepted = new List<AcceptedItem>();

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
                MarkDuplicate(item, duplicateOf);
                continue;
            }

            acceptedByHash[item.Sha256] = item.Id;
            item.Disposition = ItemDisposition.Accepted;
            item.DispositionReason = null;
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
            var received = timeline.Record(document, DocumentEventTypes.Received, null, DocumentStatus.Received, new
            {
                ingestionId = ingestion.Id,
                itemId = item.Id,
                fileName = item.FileName,
                mimeType = item.MimeType,
                kind = item.Kind,
                sizeBytes = item.SizeBytes,
            });
            accepted.Add(new AcceptedItem(item, document, received));
        }

        return accepted;
    }

    /// <summary>
    /// Commits the decisions. When the ownership index rejects the commit, another process accepted the same bytes
    /// between this handler's duplicate query and its save: the accepted items that now have a committed owner are
    /// relinked as its duplicates (together with the items of this ingestion that pointed at them), their documents
    /// and events leave the unit of work, and the commit is tried again. Every round removes at least one accepted
    /// item, so the loop ends; a violation that no committed owner explains is not a race and surfaces as the error it
    /// is. False when another writer changed the ingestion (see <see cref="TryCommitAsync"/>).
    /// </summary>
    private async Task<bool> TryCommitDecisionsAsync(Ingestion ingestion, List<IngestionItem> items, List<AcceptedItem> accepted, int attempt, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await TryCommitAsync(ingestion, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && IngestionItemOwnership.IsViolation(ex))
            {
                var relinked = await RelinkToCommittedOwnersAsync(ingestion, items, accepted, cancellationToken).ConfigureAwait(false);
                if (relinked == 0)
                {
                    throw;
                }

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Ingestion {IngestionId}: {Count} item(s) were accepted first by another process while attempt {Attempt} decided; relinked as duplicates of the committed owner.", ingestion.Id, relinked, attempt);
                }
            }
        }
    }

    /// <summary>
    /// Re-decides the accepted items against the owners committed since the decision: each one with a committed owner
    /// for its bytes in another ingestion becomes that owner's duplicate, its document and event are dropped from the
    /// unit of work, and the items of this ingestion that duplicated it follow it to the owner. Returns how many were relinked.
    /// </summary>
    private async Task<int> RelinkToCommittedOwnersAsync(Ingestion ingestion, List<IngestionItem> items, List<AcceptedItem> accepted, CancellationToken cancellationToken)
    {
        var relinked = 0;
        foreach (var entry in accepted.ToList())
        {
            var owners = await db.IngestionItems.AsNoTracking()
                .Where(i => i.Sha256 == entry.Item.Sha256 && i.IngestionId != ingestion.Id && i.Disposition == ItemDisposition.Accepted)
                .Select(i => new { i.Id, i.CreatedAt })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var owner = owners.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).FirstOrDefault();
            if (owner is null)
            {
                continue;
            }

            MarkDuplicate(entry.Item, owner.Id);
            foreach (var follower in items.Where(i => i.DuplicateOfItemId == entry.Item.Id))
            {
                follower.DuplicateOfItemId = owner.Id;
            }

            // Both are still Added: removing them detaches them, so the next commit inserts neither.
            db.DocumentEvents.Remove(entry.Received);
            db.Documents.Remove(entry.Document);
            accepted.Remove(entry);
            relinked++;
        }

        return relinked;
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
        item.DispositionReason = Truncate(reason);
    }

    private static string Truncate(string reason) => reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];

    private static void MarkDuplicate(IngestionItem item, Guid ownerId)
    {
        item.Disposition = ItemDisposition.Duplicate;
        item.DuplicateOfItemId = ownerId;
        item.DispositionReason = DuplicateReason;
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
    /// ingestion first, then, in any other ingestion of the tenant, the earliest <c>Accepted</c> item (it owns a
    /// document for the bytes), else the earliest candidate strictly before this one (older <c>CreatedAt</c>, or the
    /// same instant and a smaller v7 id). Two ingestions with the same bytes deciding at the same time thus agree on
    /// the owner, an older item is never the duplicate of a newer one that is still undecided, and a document that
    /// exists is never created a second time, whichever ingestion got there first. Unpacked files follow the same
    /// rule as stored attachments; their <c>CreatedAt</c> is the unpacking time. The ownership index catches the one
    /// case this query cannot: an owner committed by another process after the query ran.
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
                        && (i.Disposition == ItemDisposition.Accepted
                            || (i.Disposition == ItemDisposition.Candidate && i.CreatedAt <= item.CreatedAt)))
            .Select(i => new { i.Id, i.CreatedAt, OwnsDocument = i.Disposition == ItemDisposition.Accepted })
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

    /// <summary>
    /// Unpacks one level of the archive within the ingestion's <paramref name="budget"/>, which it draws down; returns
    /// the files, how many entries were skipped over the entry size or compression limit and, when the budget ran out
    /// before the last entry, why the rest was not unpacked. Throws <see cref="InvalidDataException"/> when the bytes
    /// are not a readable zip.
    /// </summary>
    private async Task<(List<IngestionItem> Children, int Skipped, string? StoppedBecause)> ExpandArchiveAsync(Ingestion ingestion, IngestionItem archiveItem, UnpackBudget budget, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var bytes = await blobs.ReadAsync(archiveItem.Blob, cancellationToken).ConfigureAwait(false);
        var children = new List<IngestionItem>();
        var skipped = 0;
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

            if (budget.ExhaustedReason(_options) is { } exhausted)
            {
                logger.LogWarning("Ingestion {IngestionId}: {Reason}; the rest of archive {FileName} is not unpacked.", ingestion.Id, exhausted, archiveItem.FileName);
                return (children, skipped, exhausted);
            }

            if (entry.Length > _options.MaxArchiveEntryBytes)
            {
                logger.LogWarning("Archive entry {Entry} in {FileName} is {Size} bytes, over the limit; skipped.", entry.FullName, archiveItem.FileName, entry.Length);
                skipped++;
                continue;
            }

            if (entry.Length > budget.Bytes)
            {
                var reason = UnpackBudget.BytesReason(_options);
                logger.LogWarning("Ingestion {IngestionId}: {Reason}; the rest of archive {FileName} is not unpacked.", ingestion.Id, reason, archiveItem.FileName);
                return (children, skipped, reason);
            }

            // The declared length is not trusted: the copy stops as soon as the inflated bytes pass the smallest of the
            // entry limit, what is left of the ingestion's budget and what the entry's compressed size allows.
            var sizeLimit = Math.Min(_options.MaxArchiveEntryBytes, budget.Bytes);
            var ratioLimit = RatioLimit(entry.CompressedLength);
            byte[]? content;
            await using (var entryStream = entry.Open())
            {
                content = await BoundedCopy.ReadAsync(entryStream, Math.Min(sizeLimit, ratioLimit), cancellationToken).ConfigureAwait(false);
            }

            if (content is null)
            {
                if (ratioLimit < sizeLimit)
                {
                    logger.LogWarning("Archive entry {Entry} in {FileName} inflates more than {Ratio} times its {Compressed} compressed bytes; skipped.", entry.FullName, archiveItem.FileName, _options.MaxArchiveCompressionRatio, entry.CompressedLength);
                    skipped++;
                    continue;
                }

                if (budget.Bytes < _options.MaxArchiveEntryBytes)
                {
                    var reason = UnpackBudget.BytesReason(_options);
                    logger.LogWarning("Ingestion {IngestionId}: {Reason}; the rest of archive {FileName} is not unpacked.", ingestion.Id, reason, archiveItem.FileName);
                    return (children, skipped, reason);
                }

                logger.LogWarning("Archive entry {Entry} in {FileName} declares {Declared} bytes but inflates past the {Max} byte limit; skipped.", entry.FullName, archiveItem.FileName, entry.Length, _options.MaxArchiveEntryBytes);
                skipped++;
                continue;
            }

            budget.Take(content.Length);

            var mimeType = ItemKinds.MimeTypeFor(entry.Name);
            var stored = await blobs.PutRawAsync(ingestion.TenantId, content, mimeType, cancellationToken).ConfigureAwait(false);
            children.Add(new IngestionItem
            {
                Id = Guid.CreateVersion7(),
                TenantId = ingestion.TenantId,
                IngestionId = ingestion.Id,
                ParentItemId = archiveItem.Id,
                FileName = UploadIntake.FitFileName(entry.Name),
                MimeType = mimeType,
                SizeBytes = content.Length,
                Sha256 = stored.Sha256,
                Blob = PipelineBlobs.ToEntity(stored),
                Kind = ItemKinds.Detect(mimeType, entry.Name),
                Disposition = ItemDisposition.Candidate,
                CreatedAt = now,
            });
        }

        return (children, skipped, null);
    }

    /// <summary>The most an entry of <paramref name="compressedLength"/> bytes may inflate to under the ratio limit.</summary>
    private long RatioLimit(long compressedLength)
    {
        var ratio = _options.MaxArchiveCompressionRatio;
        if (ratio <= 0)
        {
            return long.MaxValue;
        }

        var allowed = compressedLength > long.MaxValue / ratio ? long.MaxValue : compressedLength * ratio;
        return Math.Max(PipelineOptions.CompressionRatioFloorBytes, allowed);
    }

    /// <summary>An item that owns a document after the decision, with the rows the attempt created for it.</summary>
    private sealed record AcceptedItem(IngestionItem Item, Document Document, DocumentEvent Received);

    /// <summary>
    /// What is left of an ingestion's unpack budget (<see cref="PipelineOptions.MaxUnpackedBytesPerIngestion"/>,
    /// <see cref="PipelineOptions.MaxUnpackedItemsPerIngestion"/>), drawn down by every unpacked file.
    /// </summary>
    private sealed class UnpackBudget(long bytes, int items)
    {
        public long Bytes { get; private set; } = Math.Max(0, bytes);

        public int Items { get; private set; } = Math.Max(0, items);

        public void Take(long size)
        {
            Bytes = Math.Max(0, Bytes - size);
            Items = Math.Max(0, Items - 1);
        }

        /// <summary>Why nothing more can be unpacked, or null while the budget lasts.</summary>
        public string? ExhaustedReason(PipelineOptions options) =>
            Items <= 0 ? $"the ingestion's limit of {options.MaxUnpackedItemsPerIngestion} unpacked files is reached"
            : Bytes <= 0 ? BytesReason(options)
            : null;

        public static string BytesReason(PipelineOptions options) => $"the ingestion's limit of {options.MaxUnpackedBytesPerIngestion} unpacked bytes is reached";
    }
}
