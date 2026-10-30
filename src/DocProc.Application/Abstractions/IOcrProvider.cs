using DocProc.Domain.Text;

namespace DocProc.Application.Abstractions;

/// <summary>Positioned words from page images. Tesseract first, Azure Document Intelligence Layout for poor scans (docs/01).</summary>
public interface IOcrProvider
{
    string Name { get; }

    /// <summary>Recognises one page image (PNG) and returns words in pixel coordinates of that image.</summary>
    Task<TextPage> RecognizeAsync(int pageNumber, ReadOnlyMemory<byte> png, IReadOnlyList<string> languageHints, CancellationToken cancellationToken);
}

/// <summary>PDF text layer and rasterisation (PdfPig and PDFtoImage in production).</summary>
public interface IPdfProcessor
{
    Task<PdfInfo> InspectAsync(Stream pdf, CancellationToken cancellationToken);

    /// <summary>Words with positions from the PDF's own text layer; empty pages mean "needs OCR".</summary>
    Task<TextLayer> ExtractTextAsync(Stream pdf, CancellationToken cancellationToken);

    /// <summary>PNG per page at the given dpi (200 by default for classification, LLM input and the review UI).</summary>
    IAsyncEnumerable<RenderedPage> RenderPagesAsync(Stream pdf, int dpi, CancellationToken cancellationToken);

    /// <summary>Embedded e-invoice XML (Factur-X / ZUGFeRD attachment), when present.</summary>
    Task<EmbeddedXml?> FindEmbeddedInvoiceXmlAsync(Stream pdf, CancellationToken cancellationToken);
}

public sealed record PdfInfo(int PageCount, bool HasTextLayer, bool IsEncrypted, string? Producer);

public sealed record RenderedPage(int PageNumber, ReadOnlyMemory<byte> Png, int Width, int Height, int Dpi);

public sealed record EmbeddedXml(string FileName, ReadOnlyMemory<byte> Content, string? Flavour);
