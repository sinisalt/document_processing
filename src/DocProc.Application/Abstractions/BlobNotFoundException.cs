namespace DocProc.Application.Abstractions;

/// <summary>
/// The referenced blob is not in the store. Every <see cref="IBlobStore"/> implementation throws this from
/// <see cref="IBlobStore.OpenReadAsync"/> for a missing blob, whatever the underlying storage reports (a missing
/// file, an HTTP 404), so callers handle "not found" in one place.
/// </summary>
public sealed class BlobNotFoundException : Exception
{
    public BlobNotFoundException()
        : base("The blob does not exist.")
    {
    }

    public BlobNotFoundException(string message)
        : base(message)
    {
    }

    public BlobNotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public BlobNotFoundException(BlobRef reference, Exception? innerException = null)
        : base($"Blob {reference?.Container}/{reference?.Key} does not exist.", innerException)
    {
        Reference = reference;
    }

    /// <summary>The reference that was looked up, when known.</summary>
    public BlobRef? Reference { get; }
}
