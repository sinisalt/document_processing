using DocProc.Domain.Text;

namespace DocProc.Application.Abstractions;

/// <summary>Positioned words from page images. Tesseract first, Azure Document Intelligence Layout for poor scans (docs/01).</summary>
public interface IOcrProvider
{
    string Name { get; }

    /// <summary>Recognises one page image (PNG) and returns words in pixel coordinates of that image.</summary>
    Task<TextPage> RecognizeAsync(int pageNumber, ReadOnlyMemory<byte> png, IReadOnlyList<string> languageHints, CancellationToken cancellationToken);
}

/// <summary>
/// PDF text layer and rasterisation (PdfPig and PDFtoImage in production). Every method reads the stream from its
/// current position and throws <see cref="DocumentProcessingException"/> for input that can never be processed
/// (<see cref="DocumentProcessingFailure.EncryptedPdf"/>, <see cref="DocumentProcessingFailure.CorruptPdf"/>,
/// <see cref="DocumentProcessingFailure.TooLarge"/>); a file that opens is processable, whatever
/// <see cref="PdfInfo.IsEncrypted"/> says.
/// </summary>
public interface IPdfProcessor
{
    /// <summary>Page count, text-layer presence, encryption dictionary and producer; throws when the file cannot be opened.</summary>
    Task<PdfInfo> InspectAsync(Stream pdf, CancellationToken cancellationToken);

    /// <summary>Words with positions from the PDF's own text layer; empty pages mean "needs OCR".</summary>
    Task<TextLayer> ExtractTextAsync(Stream pdf, CancellationToken cancellationToken);

    /// <summary>PNG per page at the given dpi (200 by default for classification, LLM input and the review UI).</summary>
    IAsyncEnumerable<RenderedPage> RenderPagesAsync(Stream pdf, int dpi, CancellationToken cancellationToken);

    /// <summary>Embedded e-invoice XML (Factur-X / ZUGFeRD attachment), when present.</summary>
    Task<EmbeddedXml?> FindEmbeddedInvoiceXmlAsync(Stream pdf, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IPdfProcessor.InspectAsync"/> learns about a file that opened.</summary>
/// <param name="PageCount">Number of pages.</param>
/// <param name="HasTextLayer">At least one page carries real text, so the text layer can be extracted instead of OCR'd.</param>
/// <param name="IsEncrypted">
/// The file has an encryption dictionary but opened without a password: typically an owner password that only
/// restricts printing or copying, which is common on supplier invoices. Such a file is fully processable (text,
/// rendering, attachments); a file that needs a user password never produces a <see cref="PdfInfo"/> but throws
/// <see cref="DocumentProcessingException"/> with <see cref="DocumentProcessingFailure.EncryptedPdf"/>. Pipeline
/// stages must not branch on this flag; it is informational (admin timeline, metrics).
/// </param>
/// <param name="Producer">The producer string from the document information dictionary, when present.</param>
public sealed record PdfInfo(int PageCount, bool HasTextLayer, bool IsEncrypted, string? Producer);

public sealed record RenderedPage(int PageNumber, ReadOnlyMemory<byte> Png, int Width, int Height, int Dpi);

public sealed record EmbeddedXml(string FileName, ReadOnlyMemory<byte> Content, string? Flavour);
