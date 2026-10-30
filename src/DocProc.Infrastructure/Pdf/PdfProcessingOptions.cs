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
}
