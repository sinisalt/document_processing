using DocProc.Application.Abstractions;
using PDFtoImage;
using SkiaSharp;

namespace DocProc.Infrastructure.Pdf;

/// <summary>
/// Rasterises pages with PDFtoImage (PDFium + SkiaSharp, docs/01 "PDF"). The pipeline keeps one PNG per page at
/// 200 dpi for classification, LLM vision input and the review UI (docs/03 stage 2).
/// </summary>
internal static class PdfPageRenderer
{
    public static RenderedPage Render(byte[] pdf, int pageIndex, int dpi)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithAspectRatio: true, AntiAliasing: PdfAntiAliasing.All);
            using var bitmap = Conversion.ToImage(pdf, new Index(pageIndex), password: null, options: options);
            return Encode(bitmap, pageIndex, dpi);
        }

        throw new DocumentProcessingException(
            DocumentProcessingFailure.UnsupportedPlatform,
            "Page rendering needs the PDFium and SkiaSharp native libraries, which are only shipped for Windows, Linux and macOS.");
    }

    private static RenderedPage Encode(SKBitmap bitmap, int pageIndex, int dpi)
    {
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, quality: 100)
            ?? throw new DocumentProcessingException(DocumentProcessingFailure.UnreadablePage, $"Page {pageIndex + 1} could not be encoded as PNG.");

        return new RenderedPage(pageIndex + 1, encoded.ToArray(), bitmap.Width, bitmap.Height, dpi);
    }
}
