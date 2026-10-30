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
    private const int CopyBufferSize = 64 * 1024;

    private static readonly ParsingOptions Options = new()
    {
        UseLenientParsing = true,
        SkipMissingFonts = true,
        ClipPaths = false,
    };

    /// <summary>
    /// Materialises the stream from its current position once; PdfPig and PDFtoImage both need random access and may
    /// be called several times per stage. More than <paramref name="maximumBytes"/> fails with
    /// <see cref="DocumentProcessingFailure.TooLarge"/> before the whole file sits in memory.
    /// </summary>
    public static async Task<byte[]> ReadAllAsync(Stream pdf, long maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        if (pdf.CanSeek)
        {
            ThrowIfTooLarge(pdf.Length - pdf.Position, maximumBytes);
        }

        if (pdf is MemoryStream memory && memory.TryGetBuffer(out var buffer))
        {
            // No copy when the stream exposes exactly its bytes; otherwise only the bytes after Position, as any other stream.
            var position = checked((int)memory.Position);
            var remaining = Math.Max(0, buffer.Count - position);
            memory.Position = buffer.Count;
            return position == 0 && buffer.Offset == 0 && remaining == buffer.Array!.Length
                ? buffer.Array
                : buffer.AsSpan(position, remaining).ToArray();
        }

        using var copy = new MemoryStream();
        var chunk = new byte[CopyBufferSize];
        int read;
        while ((read = await pdf.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            ThrowIfTooLarge(copy.Length + read, maximumBytes);
            copy.Write(chunk, 0, read);
        }

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

    private static void ThrowIfTooLarge(long length, long maximumBytes)
    {
        if (length > maximumBytes)
        {
            throw new DocumentProcessingException(
                DocumentProcessingFailure.TooLarge,
                $"The PDF is larger than the configured maximum of {maximumBytes} bytes.");
        }
    }
}
