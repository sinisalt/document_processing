using System.Text.Json;
using DocProc.Application.Abstractions;

namespace DocProc.Infrastructure.Blobs;

/// <summary>
/// Content-addressed blob store on a local directory for development and tests (docs/09 "Blob storage"):
/// <c>&lt;root&gt;/&lt;container&gt;/&lt;tenant&gt;/&lt;sha256&gt;</c> with a <c>.meta.json</c> sidecar carrying the content
/// type and size, so a <see cref="BlobRef"/> can be rebuilt from the key alone. Uploads are spooled to a temp file
/// under the root while hashing and then moved into place, so a crash never leaves a partial blob at its final path
/// and two concurrent uploads of the same bytes cannot corrupt each other.
/// </summary>
public sealed class FileSystemBlobStore : IContainerBlobStore
{
    public const string MetadataSuffix = ".meta.json";
    private const string SpoolDirectoryName = ".spool";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _spoolPath;

    public FileSystemBlobStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        _spoolPath = Path.Combine(RootPath, SpoolDirectoryName);
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>Absolute root the layout lives under.</summary>
    public string RootPath { get; }

    public Task<BlobRef> PutAsync(Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken) =>
        PutAsync(BlobContainers.Raw, tenantId, content, contentType, cancellationToken);

    public async Task<BlobRef> PutAsync(string container, Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValidContainer(container);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        await using var spool = await HashedSpool.CreateAsync(content, _spoolPath, cancellationToken).ConfigureAwait(false);
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

    public Task<Stream> OpenReadAsync(BlobRef reference, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValid(reference);
        var path = PathFor(reference.Container, reference.Key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Blob {reference.Container}/{reference.Key} does not exist.", path);
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(BlobRef reference, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValid(reference);
        return Task.FromResult(File.Exists(PathFor(reference.Container, reference.Key)));
    }

    /// <summary>
    /// A <c>file:</c> URI of the blob. A file cannot expire, so <paramref name="validFor"/> is ignored: in local mode
    /// the API serves document bytes itself through <see cref="OpenReadAsync"/> (docs/05 viewer endpoint) and this
    /// URI is only ever consumed in-process, never handed to a browser.
    /// </summary>
    public Task<Uri> GetReadUrlAsync(BlobRef reference, TimeSpan validFor, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValid(reference);
        var path = PathFor(reference.Container, reference.Key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Blob {reference.Container}/{reference.Key} does not exist.", path);
        }

        return Task.FromResult(new Uri(path, UriKind.Absolute));
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
