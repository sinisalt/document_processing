using System.Runtime.Versioning;
using DocProc.Application.Abstractions;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;

namespace DocProc.Infrastructure.Pdf;

/// <summary>
/// Rasterises pages with PDFtoImage (PDFium + SkiaSharp, docs/01 "PDF"). The pipeline keeps one PNG per page at
/// 200 dpi for classification, LLM vision input and the review UI (docs/03 stage 2). PDFium parses the document
/// once per call and streams the pages; its failures become <see cref="DocumentProcessingException"/>, since a
/// file PdfPig opened leniently but PDFium rejects will not render on a retry either.
/// </summary>
internal static class PdfPageRenderer
{
    public static IEnumerable<RenderedPage> RenderAll(byte[] pdf, int pageCount, int dpi, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return RenderOnSupportedPlatform(pdf, pageCount, dpi, cancellationToken);
        }

        throw new DocumentProcessingException(
            DocumentProcessingFailure.UnsupportedPlatform,
            "Page rendering needs the PDFium and SkiaSharp native libraries, which are only shipped for Windows, Linux and macOS.");
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static IEnumerable<RenderedPage> RenderOnSupportedPlatform(byte[] pdf, int pageCount, int dpi, CancellationToken cancellationToken)
    {
        var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithAspectRatio: true, AntiAliasing: PdfAntiAliasing.All);
        using var bitmaps = Conversion.ToImages(pdf, Enumerable.Range(0, pageCount), password: null, options: options).GetEnumerator();

        for (var index = 0; index < pageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryMoveNext(bitmaps, index, out var bitmap))
            {
                yield break;
            }

            using (bitmap)
            {
                yield return Encode(bitmap, index, dpi);
            }
        }
    }

    private static bool TryMoveNext(IEnumerator<SKBitmap> bitmaps, int pageIndex, out SKBitmap bitmap)
    {
        try
        {
            if (!bitmaps.MoveNext())
            {
                bitmap = null!;
                return false;
            }

            bitmap = bitmaps.Current;
            return true;
        }
        catch (PdfPasswordProtectedException ex)
        {
            throw new DocumentProcessingException(DocumentProcessingFailure.EncryptedPdf, "The PDF is password protected and cannot be rendered without the password.", ex);
        }
        catch (PdfUnsupportedSecuritySchemeException ex)
        {
            throw new DocumentProcessingException(DocumentProcessingFailure.EncryptedPdf, "The PDF uses an encryption scheme the renderer does not support.", ex);
        }
        catch (PdfPageNotFoundException ex)
        {
            throw new DocumentProcessingException(DocumentProcessingFailure.UnreadablePage, $"Page {pageIndex + 1} could not be rendered: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is PdfException or ArgumentException or InvalidOperationException)
        {
            throw new DocumentProcessingException(DocumentProcessingFailure.CorruptPdf, $"The PDF could not be rendered: {ex.Message}", ex);
        }
    }

    private static RenderedPage Encode(SKBitmap bitmap, int pageIndex, int dpi)
    {
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, quality: 100)
            ?? throw new DocumentProcessingException(DocumentProcessingFailure.UnreadablePage, $"Page {pageIndex + 1} could not be encoded as PNG.");

        return new RenderedPage(pageIndex + 1, encoded.ToArray(), bitmap.Width, bitmap.Height, dpi);
    }
}
