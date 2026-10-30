using System.Runtime.InteropServices;
using DocProc.Application.Abstractions;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Pipeline.Support;

/// <summary>Blob access the stages share: reading raw items, reading and writing derived artefacts, mapping the two reference types.</summary>
internal sealed class PipelineBlobs(IContainerBlobStore store)
{
    public const string PngContentType = "image/png";
    public const string JsonContentType = "application/json";
    public const string XmlContentType = "application/xml";

    public static BlobRef ToRef(BlobReference reference) =>
        new(reference.Container, reference.Key, reference.Sha256, reference.SizeBytes, reference.ContentType);

    /// <summary>A fresh instance every time: EF complex types cannot share one instance between two entities.</summary>
    public static BlobReference ToEntity(BlobRef reference) => new()
    {
        Container = reference.Container,
        Key = reference.Key,
        Sha256 = reference.Sha256,
        SizeBytes = reference.SizeBytes,
        ContentType = reference.ContentType,
    };

    public static BlobReference Clone(BlobReference reference) => ToEntity(ToRef(reference));

    public static MemoryStream AsStream(ReadOnlyMemory<byte> bytes) =>
        MemoryMarshal.TryGetArray(bytes, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);

    public async Task<byte[]> ReadAsync(BlobReference reference, CancellationToken cancellationToken)
    {
        await using var stream = await store.OpenReadAsync(ToRef(reference), cancellationToken).ConfigureAwait(false);
        return await ToArrayAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A derived artefact by key (page images are stored as keys only); throws <see cref="StageFailedException"/> when it is gone.</summary>
    public async Task<byte[]> ReadDerivedAsync(string key, CancellationToken cancellationToken)
    {
        var reference = await store.FindAsync(BlobContainers.Derived, key, cancellationToken).ConfigureAwait(false)
            ?? throw new StageFailedException($"Derived blob {key} is missing; the document must be normalised again.");
        await using var stream = await store.OpenReadAsync(reference, cancellationToken).ConfigureAwait(false);
        return await ToArrayAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BlobRef> PutDerivedAsync(Guid tenantId, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        await using var stream = AsStream(content);
        return await store.PutAsync(BlobContainers.Derived, tenantId, stream, contentType, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BlobRef> PutRawAsync(Guid tenantId, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        await using var stream = AsStream(content);
        return await store.PutAsync(BlobContainers.Raw, tenantId, stream, contentType, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BlobRef> PutTextLayerAsync(Guid tenantId, TextLayer layer, CancellationToken cancellationToken) =>
        await PutDerivedAsync(tenantId, PipelineJson.SerializeTextLayer(layer), JsonContentType, cancellationToken).ConfigureAwait(false);

    public async Task<TextLayer?> ReadTextLayerAsync(Document document, CancellationToken cancellationToken)
    {
        if (document.TextLayerBlob is not { } reference)
        {
            return null;
        }

        var json = await ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        return PipelineJson.DeserializeTextLayer(json);
    }

    public async Task<List<ReadOnlyMemory<byte>>> ReadPageImagesAsync(Document document, CancellationToken cancellationToken)
    {
        var pages = new List<ReadOnlyMemory<byte>>(document.PageImageKeys.Count);
        foreach (var key in document.PageImageKeys)
        {
            pages.Add(await ReadDerivedAsync(key, cancellationToken).ConfigureAwait(false));
        }

        return pages;
    }

    private static async Task<byte[]> ToArrayAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is MemoryStream memory && memory.Position == 0)
        {
            return memory.ToArray();
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
