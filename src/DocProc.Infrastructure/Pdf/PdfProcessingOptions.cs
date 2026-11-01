using DocProc.Domain.Text;

namespace DocProc.Infrastructure.Pdf;

/// <summary>Tunables of <see cref="PdfPigPdfProcessor"/> (docs/03 stage 2 "Unpack and normalize").</summary>
public sealed class PdfProcessingOptions
{
    public const string SectionName = "DocumentProcessing:Pdf";

    /// <summary>
    /// A page counts as having a text layer when it carries more than this many words. A lone page number or a
    /// stray OCR'd word on a scan must not stop the "needs OCR" path.
    /// </summary>
    public int TextLayerMinimumWordsPerPage { get; set; } = 5;

    /// <summary>True when a page with <paramref name="words"/> words counts as having a text layer (<see cref="TextLayerMinimumWordsPerPage"/>).</summary>
    public bool CountsAsText(int words) => words > TextLayerMinimumWordsPerPage;

    /// <summary>
    /// True when at least one page of <paramref name="layer"/> counts as having a text layer: the rule stage 2 applies
    /// to a whole file (<see cref="PdfPigPdfProcessor.InspectAsync"/>), applied to a part of a split, so a few footer
    /// words on every page of a scan do not make any part readable.
    /// </summary>
    public bool HasTextLayer(TextLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return layer.Pages.Any(p => CountsAsText(p.Words.Count));
    }

    /// <summary>Upper bound for <see cref="Application.Abstractions.IPdfProcessor.RenderPagesAsync"/>; 600 dpi of an A4 page is already a 35-megapixel raster.</summary>
    public int MaximumRenderDpi { get; set; } = 600;

    /// <summary>
    /// Largest PDF the processor materialises in memory. PdfPig and PDFium both need random access, so every stage
    /// reads the whole file into a byte array; a larger file fails with
    /// <see cref="Application.Abstractions.DocumentProcessingFailure.TooLarge"/> instead of exhausting the worker.
    /// </summary>
    public long MaximumPdfBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// Largest embedded file the e-invoice finder decodes, measured both on the stored (usually compressed) stream
    /// and on its decoded content. Bigger attachments are ignored: a real Factur-X / ZUGFeRD XML is a few hundred
    /// kilobytes, a decompression bomb is not.
    /// </summary>
    public int MaximumAttachmentBytes { get; set; } = 32 * 1024 * 1024;
}
