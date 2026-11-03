using DocProc.Application.Abstractions;
using DocProc.Domain.Fields;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using SkiaSharp;

namespace DocProc.Pipeline.Review;

/// <summary>A part of a page image cut out for OCR, with where it lies on the page in pixels.</summary>
public sealed record PageRegionImage(ReadOnlyMemory<byte> Png, int Left, int Top, int Width, int Height, int PageWidth, int PageHeight);

/// <summary>
/// What map mode reads a page from (docs/11 §12.3): the stored text layer, and the rendered page image cut to a box for
/// region OCR (<c>regions:read</c>).
/// </summary>
public static class PageSources
{
    /// <summary>The narrowest region cut out for OCR, in pixels; a smaller box is widened to it around its centre.</summary>
    public const int MinRegionPixels = 8;

    /// <summary>The text layer the Normalize stage stored for the document, or null when it has none (an image, a scan without OCR).</summary>
    public static async Task<TextLayer?> ReadTextLayerAsync(IContainerBlobStore blobs, Document document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return await new PipelineBlobs(blobs).ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The page image (PNG) of page <paramref name="page"/> (1-based), or null when it was not rendered.</summary>
    public static async Task<byte[]?> ReadPageImageAsync(IContainerBlobStore blobs, Document document, int page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(document);
        if (page < 1 || page > document.PageImageKeys.Count)
        {
            return null;
        }

        var reference = await blobs.FindAsync(BlobContainers.Derived, document.PageImageKeys[page - 1], cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            return null;
        }

        try
        {
            await using var stream = await blobs.OpenReadAsync(reference, cancellationToken).ConfigureAwait(false);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            return copy.ToArray();
        }
        catch (BlobNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cuts <paramref name="box"/> (page units, 0 to 1000 on both axes) out of a page image; null when the image does not
    /// decode. The cut is whole pixels, at least <see cref="MinRegionPixels"/> on each side, within the image.
    /// </summary>
    public static PageRegionImage? Crop(byte[] png, BoundingBox box)
    {
        ArgumentNullException.ThrowIfNull(png);
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return null;
        }

        var (left, width) = Span(box.X0, box.X1, bitmap.Width);
        var (top, height) = Span(box.Y0, box.Y1, bitmap.Height);
        using var cropped = new SKBitmap(width, height);
        if (!bitmap.ExtractSubset(cropped, new SKRectI(left, top, left + width, top + height)))
        {
            return null;
        }

        using var image = SKImage.FromBitmap(cropped);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded is null ? null : new PageRegionImage(encoded.ToArray(), left, top, width, height, bitmap.Width, bitmap.Height);
    }

    /// <summary>
    /// The words OCR read in a cut-out region, moved to page units (0 to 1000) of the whole page: a box in the region's
    /// pixels lands where that region lies on the page.
    /// </summary>
    public static IReadOnlyList<PositionedWord> ToPageUnits(PageRegionImage region, TextPage recognized)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(recognized);
        var sx = recognized.Width > 0 ? region.Width / recognized.Width : 1;
        var sy = recognized.Height > 0 ? region.Height / recognized.Height : 1;
        return
        [
            .. recognized.Words
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .Select(w => w with
                {
                    Text = w.Text.Trim(),
                    Box = new BoundingBox(
                        X(region.Left + (w.Box.X0 * sx), region.PageWidth),
                        X(region.Top + (w.Box.Y0 * sy), region.PageHeight),
                        X(region.Left + (w.Box.X1 * sx), region.PageWidth),
                        X(region.Top + (w.Box.Y1 * sy), region.PageHeight)),
                }),
        ];

        static double X(double pixels, int size) => Math.Round(Math.Clamp(pixels / size * TextPage.NormalizedSize, 0, TextPage.NormalizedSize), 2);
    }

    private static (int Start, int Length) Span(double from, double to, int size)
    {
        var start = (int)Math.Floor(Math.Clamp(from, 0, TextPage.NormalizedSize) / TextPage.NormalizedSize * size);
        var end = (int)Math.Ceiling(Math.Clamp(to, 0, TextPage.NormalizedSize) / TextPage.NormalizedSize * size);
        var length = Math.Max(end - start, Math.Min(MinRegionPixels, size));
        start = Math.Clamp(start - Math.Max(0, (length - (end - start)) / 2), 0, size - length);
        return (start, length);
    }
}
