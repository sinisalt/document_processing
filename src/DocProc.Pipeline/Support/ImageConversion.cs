using SkiaSharp;

namespace DocProc.Pipeline.Support;

/// <summary>Stage 2 for image items: a page image is always a PNG, so JPEG, BMP, GIF and WebP attachments are re-encoded.</summary>
internal static class ImageConversion
{
    /// <summary>The image as PNG, unchanged when it already is one; null when SkiaSharp cannot decode it (TIFF, HEIC, a truncated file).</summary>
    public static byte[]? ToPng(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (ItemKinds.IsPng(image))
        {
            return image;
        }

        using var bitmap = SKBitmap.Decode(image);
        if (bitmap is null)
        {
            return null;
        }

        using var skImage = SKImage.FromBitmap(bitmap);
        using var encoded = skImage.Encode(SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray();
    }
}
