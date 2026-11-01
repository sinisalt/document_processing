using System.Runtime.CompilerServices;
using DocProc.Application.Abstractions;
using DocProc.Domain.Text;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Pdf;

/// <summary>
/// <see cref="IPdfProcessor"/> over PdfPig (inspection, text layer, embedded files) and PDFtoImage (page rasters), the
/// libraries chosen in docs/01 "PDF". Implements stage 2 of docs/03: the canonical pair of positioned text layer and
/// one PNG per page, and the "embedded structured data first" check. Encrypted or corrupt input raises a
/// <see cref="DocumentProcessingException"/> with the reason the pipeline records on the failed stage.
/// </summary>
/// <remarks>
/// PdfPig and PDFtoImage are synchronous and CPU bound; the methods run them inline on the worker and honour the
/// cancellation token between pages. Coordinates are PDF points with a top-left origin (see <see cref="PdfTextLayerExtractor"/>).
/// <see cref="PdfInfo.IsEncrypted"/> reports an encryption dictionary that PdfPig got past without a password (an
/// owner password restricting printing or copying); such files go through every stage like any other.
/// </remarks>
public sealed class PdfPigPdfProcessor(IOptions<PdfProcessingOptions> options) : IPdfProcessor
{
    private readonly PdfProcessingOptions _options = options.Value;

    public async Task<PdfInfo> InspectAsync(Stream pdf, CancellationToken cancellationToken)
    {
        var bytes = await PdfDocumentReader.ReadAllAsync(pdf, _options.MaximumPdfBytes, cancellationToken).ConfigureAwait(false);
        using var document = PdfDocumentReader.Open(bytes);

        var hasTextLayer = false;
        for (var number = 1; number <= document.NumberOfPages; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.CountsAsText(PdfTextLayerExtractor.CountWords(document.GetPage(number))))
            {
                hasTextLayer = true;
                break;
            }
        }

        return new PdfInfo(document.NumberOfPages, hasTextLayer, document.IsEncrypted, NullIfEmpty(document.Information.Producer));
    }

    public async Task<TextLayer> ExtractTextAsync(Stream pdf, CancellationToken cancellationToken)
    {
        var bytes = await PdfDocumentReader.ReadAllAsync(pdf, _options.MaximumPdfBytes, cancellationToken).ConfigureAwait(false);
        using var document = PdfDocumentReader.Open(bytes);

        var pages = new List<TextPage>(document.NumberOfPages);
        for (var number = 1; number <= document.NumberOfPages; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(PdfTextLayerExtractor.ExtractPage(document.GetPage(number)));
        }

        return new TextLayer(TextLayerSource.PdfText, pages);
    }

    public async IAsyncEnumerable<RenderedPage> RenderPagesAsync(Stream pdf, int dpi, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dpi, _options.MaximumRenderDpi);

        var bytes = await PdfDocumentReader.ReadAllAsync(pdf, _options.MaximumPdfBytes, cancellationToken).ConfigureAwait(false);
        int pageCount;
        using (var document = PdfDocumentReader.Open(bytes))
        {
            pageCount = document.NumberOfPages;
        }

        foreach (var page in PdfPageRenderer.RenderAll(bytes, pageCount, dpi, cancellationToken))
        {
            yield return page;
        }
    }

    public async Task<EmbeddedXml?> FindEmbeddedInvoiceXmlAsync(Stream pdf, CancellationToken cancellationToken)
    {
        var bytes = await PdfDocumentReader.ReadAllAsync(pdf, _options.MaximumPdfBytes, cancellationToken).ConfigureAwait(false);
        using var document = PdfDocumentReader.Open(bytes);
        return EmbeddedInvoiceXmlFinder.Find(document, _options.MaximumAttachmentBytes);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
