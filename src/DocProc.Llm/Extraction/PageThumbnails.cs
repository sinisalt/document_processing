using SkiaSharp;

namespace DocProc.Llm.Extraction;

/// <summary>
/// Downscales rendered pages (200 dpi PNGs from stage 2, docs/03) to the thumbnails the split call needs: a page
/// boundary is visible at a few hundred pixels, and a 20-page scan at full size would be tens of megabytes of base64
/// and far over the provider request limits. Output is an 8-bit greyscale PNG (a quarter of the raw bytes of RGBA,
/// and documents are black on white), so the adapters' image media type stays the same.
/// </summary>
public static class PageThumbnails
{
    /// <summary>A page whose long edge is already at or below the target is attached unchanged, as is one that cannot be decoded.</summary>
    public static ReadOnlyMemory<byte> Downscale(ReadOnlyMemory<byte> png, int longEdge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(longEdge, 1);
        if (!PngDimensions.TryRead(png.Span, out var width, out var height) || Math.Max(width, height) <= longEdge)
        {
            return png;
        }

        using var data = SKData.CreateCopy(png.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return png;
        }

        // Not decodable (a truncated render): attached as is; the provider reports it if it is unusable.
        using var original = SKBitmap.Decode(codec);
        if (original is null)
        {
            return png;
        }

        var scale = (double)longEdge / Math.Max(original.Width, original.Height);
        var info = new SKImageInfo(
            Math.Max(1, (int)Math.Round(original.Width * scale)),
            Math.Max(1, (int)Math.Round(original.Height * scale)),
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var resized = original.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized is null)
        {
            return png;
        }

        using var grey = resized.Copy(SKColorType.Gray8);
        if (grey is not null)
        {
            Posterize(grey.GetPixelSpan());
        }

        using var image = SKImage.FromBitmap(grey ?? resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Sixteen grey levels are plenty to see a header, a table or a blank page, and the scanner speckle that
    /// downscaling smears into dozens of near-white shades would otherwise defeat PNG's compression.
    /// </summary>
    private static void Posterize(Span<byte> greyPixels)
    {
        const int Levels = 16;
        const int Step = 256 / Levels;
        for (var i = 0; i < greyPixels.Length; i++)
        {
            var level = (greyPixels[i] + (Step / 2)) / Step;
            greyPixels[i] = (byte)Math.Min(255, level * Step);
        }
    }
}
