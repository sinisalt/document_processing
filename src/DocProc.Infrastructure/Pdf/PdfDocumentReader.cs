using DocProc.Application.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace DocProc.Infrastructure.Pdf;

/// <summary>
/// Opens PDFs with PdfPig and turns its failure modes into <see cref="DocumentProcessingException"/> so a pipeline
/// stage can fail the document with a reason instead of retrying a file that will never parse.
/// </summary>
internal static class PdfDocumentReader
{
    private static readonly ParsingOptions Options = new()
    {
        UseLenientParsing = true,
        SkipMissingFonts = true,
        ClipPaths = false,
    };

    /// <summary>Materialises the stream once; PdfPig and PDFtoImage both need random access and may be called several times per stage.</summary>
    public static async Task<byte[]> ReadAllAsync(Stream pdf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        if (pdf is MemoryStream memory && memory.TryGetBuffer(out var buffer) && buffer.Offset == 0 && buffer.Count == buffer.Array!.Length)
        {
            return buffer.Array;
        }

        using var copy = new MemoryStream();
        await pdf.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }

    public static PdfDocument Open(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            throw new DocumentProcessingException(DocumentProcessingFailure.CorruptPdf, "The document is empty.");
        }

        try
        {
            return PdfDocument.Open(bytes, Options);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new DocumentProcessingException(
                DocumentProcessingFailure.EncryptedPdf,
                "The PDF is password protected and cannot be processed without the password.",
                ex);
        }
        catch (PdfDocumentFormatException ex)
        {
            throw new DocumentProcessingException(
                DocumentProcessingFailure.CorruptPdf,
                $"The file is not a readable PDF: {ex.Message}",
                ex);
        }
        catch (PdfDocumentStackDepthException ex)
        {
            throw new DocumentProcessingException(
                DocumentProcessingFailure.CorruptPdf,
                "The PDF's object structure is nested too deeply to be read.",
                ex);
        }
    }
}
