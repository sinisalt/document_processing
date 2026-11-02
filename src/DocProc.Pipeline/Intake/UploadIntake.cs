using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Pipeline.Intake;

/// <summary>One file of an upload: its name, normalised content type, the coarse kind the channel saw and a factory for its bytes.</summary>
public sealed record UploadFile(string FileName, string ContentType, ItemKind Kind, Func<Stream> Open);

/// <summary>An upload file after its bytes reached the raw blob container.</summary>
public sealed record StoredUpload(UploadFile File, BlobRef Blob);

/// <summary>
/// Where an ingestion came from (docs/11 §7.1), kept apart so the sender of a mail and the actor of an upload no
/// longer share one column. <see cref="Sender"/> is the real e-mail From of a mailbox ingestion;
/// <see cref="SubmittedBy"/> is the user or API key that uploaded the files. The routing facts of docs/11 §7.1
/// (recipients, mailbox, folder, sender authentication, hints) join this record with the channels that capture them.
/// </summary>
public sealed record SourceMetadata(string? Sender = null, string? SubmittedBy = null, string? Subject = null)
{
    /// <summary>An upload by <paramref name="submittedBy"/>: no sender, only the actor.</summary>
    public static SourceMetadata Upload(string? submittedBy) => new(SubmittedBy: submittedBy);
}

/// <summary>
/// The storing half of stage 1 for uploads (docs/03 "API upload"), shared by <c>POST /ingestions</c> and the demo
/// data of the development mode (docs/09 "Demo data") so both create exactly the same rows: the bytes go to the
/// content-addressed raw store, one <see cref="Ingestion"/> stands for the upload with one candidate
/// <see cref="IngestionItem"/> per file, and after the caller's commit a <c>ProcessIngestion</c> message hands it to
/// the pipeline. Idempotency keys and channel resolution stay with the caller.
/// </summary>
public static class UploadIntake
{
    public const int MaxFileNameLength = 260;
    public const int MaxContentTypeLength = 127;
    public const int MaxSourceFromLength = 320;
    public const int MaxSourceSenderLength = 320;
    public const int MaxSubmittedByLength = 200;
    public const int MaxSubjectLength = 1000;

    /// <summary>Stores every file in order. Content-addressed: storing the same bytes twice is a no-op, so a retry after a failed save is safe.</summary>
    public static async Task<IReadOnlyList<StoredUpload>> StoreAsync(IBlobStore blobs, Guid tenantId, IReadOnlyList<UploadFile> files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(files);

        var stored = new List<StoredUpload>(files.Count);
        foreach (var file in files)
        {
            var content = file.Open();
            await using (content.ConfigureAwait(false))
            {
                stored.Add(new StoredUpload(file, await blobs.PutAsync(tenantId, content, BlobContentType(file.ContentType), cancellationToken).ConfigureAwait(false)));
            }
        }

        return stored;
    }

    /// <summary>
    /// The received ingestion for stored files, not yet added to a context. One upload, one raw artefact: for a
    /// multi-file upload the first file stands for the batch; every file is its own item. The legacy
    /// <c>SourceFrom</c> column still receives the sender, or the actor when there is none, exactly as before the
    /// split, until it is dropped (docs/11 §6.3).
    /// </summary>
    public static Ingestion CreateIngestion(Guid tenantId, Guid channelId, SourceMetadata source, IReadOnlyList<StoredUpload> stored, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.Count == 0)
        {
            throw new ArgumentException("An ingestion needs at least one file.", nameof(stored));
        }

        var first = stored[0];
        var ingestion = new Ingestion
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ChannelId = channelId,
            ReceivedAt = now,
            SourceFrom = (source.Sender ?? source.SubmittedBy) is { } legacyFrom ? Truncate(legacyFrom, MaxSourceFromLength) : null,
            SourceSender = Clean(source.Sender, MaxSourceSenderLength)?.ToLowerInvariant(),
            SubmittedBy = Clean(source.SubmittedBy, MaxSubmittedByLength),
            SourceSubject = Clean(source.Subject, MaxSubjectLength),
            SourceFileName = FitFileName(first.File.FileName),
            RawBlob = ToReference(first.Blob),
            Status = IngestionStatus.Received,
            UpdatedAt = now,
        };

        foreach (var (file, blob) in stored)
        {
            ingestion.Items.Add(new IngestionItem
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                IngestionId = ingestion.Id,
                FileName = FitFileName(file.FileName),
                MimeType = Truncate(file.ContentType, MaxContentTypeLength),
                SizeBytes = blob.SizeBytes,
                Sha256 = blob.Sha256,
                Blob = ToReference(blob),
                Kind = file.Kind,
                Disposition = ItemDisposition.Candidate,
                CreatedAt = now,
            });
        }

        return ingestion;
    }

    /// <summary>
    /// A file name that fits the <c>FileName</c> columns (<see cref="MaxFileNameLength"/>). A longer one (a crafted
    /// upload, a deep archive entry) is cut in its stem so the extension, which the viewer and the kind detection
    /// read, survives.
    /// </summary>
    public static string FitFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (fileName.Length <= MaxFileNameLength)
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        return extension.Length is > 0 and <= 16
            ? string.Concat(fileName.AsSpan(0, MaxFileNameLength - extension.Length), extension)
            : fileName[..MaxFileNameLength];
    }

    /// <summary>
    /// The content type the blob is stored under. The blob reference columns hold <see cref="MaxContentTypeLength"/>
    /// characters and the store echoes the type back, so a longer (necessarily bogus) media type is stored as
    /// <c>application/octet-stream</c> instead of failing the ingestion's save.
    /// </summary>
    public static string BlobContentType(string contentType) =>
        contentType.Length <= MaxContentTypeLength ? contentType : "application/octet-stream";

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static string? Clean(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim(), maxLength);

    private static BlobReference ToReference(BlobRef blob) => new()
    {
        Container = blob.Container,
        Key = blob.Key,
        Sha256 = blob.Sha256,
        SizeBytes = blob.SizeBytes,
        ContentType = BlobContentType(blob.ContentType),
    };
}
