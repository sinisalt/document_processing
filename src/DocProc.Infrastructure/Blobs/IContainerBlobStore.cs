using DocProc.Application.Abstractions;

namespace DocProc.Infrastructure.Blobs;

/// <summary>
/// <see cref="IBlobStore"/> plus the container to write to. The application-level <see cref="IBlobStore.PutAsync"/>
/// stores into <see cref="BlobContainers.Raw"/>; pipeline stages that produce derived artefacts or exports use this
/// overload with the matching container (docs/01 "Blob").
/// </summary>
public interface IContainerBlobStore : IBlobStore
{
    /// <inheritdoc cref="IBlobStore.PutAsync"/>
    Task<BlobRef> PutAsync(string container, Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>Rebuilds the reference for a stored key (size and content type from the store), or null when nothing is there.</summary>
    Task<BlobRef?> FindAsync(string container, string key, CancellationToken cancellationToken);
}
