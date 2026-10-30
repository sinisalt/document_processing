using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using DocProc.Application.Abstractions;

namespace DocProc.Infrastructure.Blobs;

/// <summary>
/// <see cref="IBlobStore"/> on Azure Blob Storage, and on Azurite locally through the same SDK (docs/01 "Blob",
/// docs/09 "Blob storage"). Blob names follow the same <c>{tenant}/{sha256}</c> layout as
/// <see cref="FileSystemBlobStore"/> inside the containers of <see cref="BlobContainers"/>. Uploads carry
/// <c>If-None-Match: *</c> so an existing blob is never rewritten, which is what makes the raw container safe to put
/// under a WORM policy. Read URLs are user-delegation SAS tokens when the client authenticates with a credential
/// (managed identity in Azure) and account-key SAS tokens when the connection string carries a key (Azurite).
/// </summary>
public sealed class AzureBlobStore : IContainerBlobStore
{
    /// <summary>Blob metadata key under which the content hash is stored, for audits that do not trust the name.</summary>
    public const string Sha256MetadataKey = "sha256";

    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DelegationKeyRenewalMargin = TimeSpan.FromMinutes(1);

    private readonly BlobServiceClient _client;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maxReadUrlLifetime;
    private readonly string _spoolPath;
    private UserDelegationKey? _delegationKey;

    public AzureBlobStore(BlobServiceClient client, TimeProvider clock, TimeSpan maxReadUrlLifetime)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxReadUrlLifetime, TimeSpan.Zero);
        _client = client;
        _clock = clock;
        _maxReadUrlLifetime = maxReadUrlLifetime;
        _spoolPath = Path.Combine(Path.GetTempPath(), "docproc", "blob-spool");
    }

    public Task<BlobRef> PutAsync(Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken) =>
        PutAsync(BlobContainers.Raw, tenantId, content, contentType, cancellationToken);

    public async Task<BlobRef> PutAsync(string container, Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValidContainer(container);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        if (content.CanSeek)
        {
            // Hash in place, then rewind: no copy needed when the caller already holds the bytes on disk or in memory.
            var start = content.Position;
            var sha256 = await ContentHash.Sha256HexAsync(content, cancellationToken).ConfigureAwait(false);
            var size = content.Length - start;
            content.Position = start;
            return await StoreAsync(container, tenantId, sha256, size, content, contentType, cancellationToken).ConfigureAwait(false);
        }

        await using var spool = await HashedSpool.CreateAsync(content, _spoolPath, cancellationToken).ConfigureAwait(false);
        var file = new FileStream(spool.Path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (file.ConfigureAwait(false))
        {
            return await StoreAsync(container, tenantId, spool.Sha256, spool.SizeBytes, file, contentType, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Stream> OpenReadAsync(BlobRef reference, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValid(reference);
        var response = await BlobFor(reference).DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Value.Content;
    }

    public async Task<bool> ExistsAsync(BlobRef reference, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValid(reference);
        return await BlobFor(reference).ExistsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read-only SAS URL, clamped to <c>Blobs:MaxReadUrlLifetime</c>. Account-key SAS when the client can sign
    /// locally (connection string with a key: Azurite, or an explicit account key); otherwise a user-delegation SAS
    /// signed with a key fetched from the service under the client's credential and cached until it nears expiry.
    /// </summary>
    public async Task<Uri> GetReadUrlAsync(BlobRef reference, TimeSpan validFor, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValid(reference);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validFor, TimeSpan.Zero);

        var now = _clock.GetUtcNow();
        var startsOn = now - ClockSkewAllowance;
        var expiresOn = now + (validFor < _maxReadUrlLifetime ? validFor : _maxReadUrlLifetime);
        var builder = new BlobSasBuilder(BlobSasPermissions.Read, expiresOn)
        {
            BlobContainerName = reference.Container,
            BlobName = reference.Key,
            Resource = "b",
            StartsOn = startsOn,
        };

        var blob = BlobFor(reference);
        if (blob.CanGenerateSasUri)
        {
            return blob.GenerateSasUri(builder);
        }

        var delegationKey = await GetUserDelegationKeyAsync(expiresOn, cancellationToken).ConfigureAwait(false);
        return new BlobUriBuilder(blob.Uri)
        {
            Sas = builder.ToSasQueryParameters(delegationKey, _client.AccountName),
        }.ToUri();
    }

    public async Task<BlobRef?> FindAsync(string container, string key, CancellationToken cancellationToken)
    {
        BlobKeys.EnsureValidContainer(container);
        BlobKeys.EnsureValidKey(key);
        var blob = _client.GetBlobContainerClient(container).GetBlobClient(key);
        if (!await blob.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await DescribeAsync(blob, container, key, BlobKeys.Sha256Of(key), FileSystemBlobStore.BlobMetadata.DefaultContentType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the containers of <see cref="BlobContainers"/> when missing; for Azurite and tests, Bicep owns them in Azure.</summary>
    public async Task EnsureContainersAsync(CancellationToken cancellationToken)
    {
        foreach (var container in BlobContainers.All)
        {
            await _client.GetBlobContainerClient(container).CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private BlobClient BlobFor(BlobRef reference) => _client.GetBlobContainerClient(reference.Container).GetBlobClient(reference.Key);

    private async Task<BlobRef> StoreAsync(string container, Guid tenantId, string sha256, long size, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var key = BlobKeys.ForContent(tenantId, sha256);
        var blob = _client.GetBlobContainerClient(container).GetBlobClient(key);

        if (await blob.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return await DescribeAsync(blob, container, key, sha256, contentType, cancellationToken).ConfigureAwait(false);
        }

        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [Sha256MetadataKey] = sha256 },
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
        };

        try
        {
            await blob.UploadAsync(content, options, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Lost the race against another upload of the same bytes: the blob is there, so the put succeeded.
            return await DescribeAsync(blob, container, key, sha256, contentType, cancellationToken).ConfigureAwait(false);
        }

        return new BlobRef(container, key, sha256, size, contentType);
    }

    private static async Task<BlobRef> DescribeAsync(BlobClient blob, string container, string key, string sha256, string fallbackContentType, CancellationToken cancellationToken)
    {
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var contentType = string.IsNullOrEmpty(properties.Value.ContentType) ? fallbackContentType : properties.Value.ContentType;
        return new BlobRef(container, key, sha256, properties.Value.ContentLength, contentType);
    }

    private async Task<UserDelegationKey> GetUserDelegationKeyAsync(DateTimeOffset mustCover, CancellationToken cancellationToken)
    {
        // No lock: two callers refreshing at once both get a valid key and the last write wins, which is harmless
        // and cheaper than holding a lock across the service call.
        if (Volatile.Read(ref _delegationKey) is { } cached && cached.SignedExpiresOn >= mustCover + DelegationKeyRenewalMargin)
        {
            return cached;
        }

        // One key covers many URLs: ask for well beyond the longest URL lifetime so the fetch is rare.
        var now = _clock.GetUtcNow();
        var keyLifetime = _maxReadUrlLifetime * 4 > TimeSpan.FromHours(1) ? _maxReadUrlLifetime * 4 : TimeSpan.FromHours(1);
        var response = await _client.GetUserDelegationKeyAsync(now - ClockSkewAllowance, now + keyLifetime, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _delegationKey, response.Value);
        return response.Value;
    }
}
