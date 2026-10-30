using System.Text.Json;
using DocProc.Application.Abstractions;

namespace DocProc.Infrastructure.Blobs;

/// <summary>
/// Content-addressed blob store on a local directory for development and tests (docs/09 "Blob storage"):
/// <c>&lt;root&gt;/&lt;container&gt;/&lt;tenant&gt;/&lt;sha256&gt;</c> with a <c>.meta.json</c> sidecar carrying the content
/// type and size, so a <see cref="BlobRef"/> can be rebuilt from the key alone. Uploads are spooled to a temp file
/// under the root while hashing and then moved into place, so a crash never leaves a partial blob at its final path
/// and two concurrent uploads of the same bytes cannot corrupt each other. Spool files a crashed process left
/// behind are swept when the store is constructed.
/// </summary>
public sealed class FileSystemBlobStore : IContainerBlobStore
{
    public const string MetadataSuffix = ".meta.json";
    private const string SpoolDirectoryName = ".spool";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FileSystemBlobStore(string rootPath)
        : this(rootPath, TimeProvider.System)
    {
    }

    public FileSystemBlobStore(string rootPath, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(clock);
        RootPath = Path.GetFullPath(rootPath);
        SpoolPath = Path.Combine(RootPath, SpoolDirectoryName);
        Directory.CreateDirectory(RootPath);
        HashedSpool.SweepStale(SpoolPath, clock);
    }

    /// <summary>Absolute root the layout lives under.</summary>
    public string RootPath { get; }

    /// <summary>Where uploads are hashed before they are moved into place.</summary>
    public string SpoolPath { get; }

    public Task<BlobRef> PutAsync(Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken) =>
        PutAsync(BlobContainers.Raw, tenantId, content, contentType, cancellationToken);

    public async Task<BlobRef> PutAsync(string container, Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValidContainer(container);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        await using var spool = await HashedSpool.CreateAsync(content, SpoolPath, cancellationToken).ConfigureAwait(false);
        var key = BlobKeys.ForContent(tenantId, spool.Sha256);
        var path = PathFor(container, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var metadata = new BlobMetadata(spool.Sha256, spool.SizeBytes, contentType);
        if (TryMoveIntoPlace(spool.Path, path))
        {
            await WriteMetadataAsync(path, metadata, cancellationToken).ConfigureAwait(false);
            return new BlobRef(container, key, spool.Sha256, spool.SizeBytes, contentType);
        }

        // Same content was stored before (or by a concurrent caller): keep whatever is there and report it. If the
        // concurrent writer has not finished its sidecar yet, write ours; both describe the same bytes.
        var existing = await ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await WriteMetadataAsync(path, metadata, cancellationToken).ConfigureAwait(false);
            existing = metadata;
        }

        return new BlobRef(container, key, spool.Sha256, existing.SizeBytes, existing.ContentType);
    }

    /// <inheritdoc/>
    /// <exception cref="BlobNotFoundException">Nothing is stored under the reference.</exception>
    public Task<Stream> OpenReadAsync(BlobRef reference, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValid(reference);
        var path = PathFor(reference.Container, reference.Key);
        try
        {
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new BlobNotFoundException(reference, ex);
        }
    }

    public Task<bool> ExistsAsync(BlobRef reference, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValid(reference);
        return Task.FromResult(File.Exists(PathFor(reference.Container, reference.Key)));
    }

    /// <summary>
    /// Not supported: a file has no expiring URL, and a <c>file:</c> URI would be a permanent link exposing the
    /// server's paths. In local mode the API serves document bytes itself through <see cref="OpenReadAsync"/>
    /// (docs/05 viewer endpoint).
    /// </summary>
    /// <exception cref="NotSupportedException">Always, once the reference passed validation.</exception>
    public Task<Uri> GetReadUrlAsync(BlobRef reference, TimeSpan validFor, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValid(reference);
        throw new NotSupportedException("The file-system blob store cannot issue short-lived read URLs; stream the blob through OpenReadAsync instead.");
    }

    public async Task<BlobRef?> FindAsync(string container, string key, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValidContainer(container);
        BlobKeys.EnsureValidKey(key);
        var path = PathFor(container, key);
        if (!File.Exists(path))
        {
            return null;
        }

        var metadata = await ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        var size = metadata?.SizeBytes ?? new FileInfo(path).Length;
        return new BlobRef(container, key, BlobKeys.Sha256Of(key), size, metadata?.ContentType ?? BlobMetadata.DefaultContentType);
    }

    /// <summary>Absolute path of a blob, guaranteed to be under <see cref="RootPath"/> even for a hostile reference.</summary>
    public string PathFor(string container, string key)
    {
        BlobKeys.EnsureValidContainer(container);
        BlobKeys.EnsureValidKey(key);
        var path = Path.GetFullPath(Path.Combine(RootPath, container, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The reference resolves outside the store root.", nameof(key));
        }

        return path;
    }

    private static bool TryMoveIntoPlace(string source, string destination)
    {
        if (File.Exists(destination))
        {
            return false;
        }

        try
        {
            File.Move(source, destination, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(destination))
        {
            return false;
        }
    }

    private static async Task WriteMetadataAsync(string blobPath, BlobMetadata metadata, CancellationToken cancellationToken)
    {
        var target = blobPath + MetadataSuffix;
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(file, metadata, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, target, overwrite: true);
    }

    private static async Task<BlobMetadata?> ReadMetadataAsync(string blobPath, CancellationToken cancellationToken)
    {
        var target = blobPath + MetadataSuffix;
        if (!File.Exists(target))
        {
            return null;
        }

        var file = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync<BlobMetadata>(file, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What the sidecar holds next to each blob.</summary>
    internal sealed record BlobMetadata(string Sha256, long SizeBytes, string ContentType)
    {
        public const string DefaultContentType = "application/octet-stream";
    }
}
