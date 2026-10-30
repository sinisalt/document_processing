namespace DocProc.Application.Abstractions;

/// <summary>Content-addressed blob storage: Azure Blob in production, Azurite locally (docs/01, docs/03 "Idempotency").</summary>
public interface IBlobStore
{
    /// <summary>Stores the content under <c>{tenant}/{sha256}</c> and returns the reference; storing the same bytes twice is a no-op.</summary>
    Task<BlobRef> PutAsync(Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(BlobRef reference, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(BlobRef reference, CancellationToken cancellationToken);

    /// <summary>Short-lived URL for the viewer; never a permanent public link.</summary>
    Task<Uri> GetReadUrlAsync(BlobRef reference, TimeSpan validFor, CancellationToken cancellationToken);
}

/// <summary>Where a blob lives. <see cref="Sha256"/> is the hex digest of the content and part of the key.</summary>
public sealed record BlobRef(string Container, string Key, string Sha256, long SizeBytes, string ContentType);
