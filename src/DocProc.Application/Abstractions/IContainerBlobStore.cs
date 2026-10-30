namespace DocProc.Application.Abstractions;

/// <summary>
/// <see cref="IBlobStore"/> plus the container to write to. The application-level <see cref="IBlobStore.PutAsync"/>
/// stores into <see cref="BlobContainers.Raw"/>; pipeline stages that produce derived artefacts (stage 2: page
/// images, normalised PDFs, text layers) or exports (stage 9) use this overload with the matching container
/// (docs/01 "Blob"). Both interfaces resolve to the same singleton.
/// </summary>
public interface IContainerBlobStore : IBlobStore
{
    /// <inheritdoc cref="IBlobStore.PutAsync"/>
    Task<BlobRef> PutAsync(string container, Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>Rebuilds the reference for a stored key (size and content type from the store), or null when nothing is there.</summary>
    Task<BlobRef?> FindAsync(string container, string key, CancellationToken cancellationToken);
}
