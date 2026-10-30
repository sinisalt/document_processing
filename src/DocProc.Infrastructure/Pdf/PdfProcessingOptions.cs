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
