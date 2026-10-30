using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 2 (docs/03 "Unpack and normalize"): the canonical pair of one PNG per page and a positioned text layer, both
/// in the derived container, plus the embedded e-invoice XML when the PDF carries one. Images become a single page
/// without text (OCR is a later block, so the page is flagged); UBL / CII files skip pages entirely; everything else
/// is unsupported for now and fails without a retry.
/// </summary>
internal sealed class NormalizeHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    PipelineBlobs blobs,
    IPdfProcessor pdf,
    IEInvoiceParser eInvoices,
    IOptions<PipelineOptions> options,
    ILogger<NormalizeHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    private static readonly string[] OwnedFlags = [FlagCodes.UnreadablePages];
    private readonly PipelineOptions _options = options.Value;

    public override PipelineStage Stage => PipelineStage.Normalize;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Received;

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        var item = document.IngestionItem ?? throw new StageFailedException("The document's ingestion item is missing.");
        object details = item.Kind switch
        {
            ItemKind.Pdf => await NormalizePdfAsync(document, item, cancellationToken).ConfigureAwait(false),
            ItemKind.Image => await NormalizeImageAsync(document, item, cancellationToken).ConfigureAwait(false),
            ItemKind.Xml => await NormalizeXmlAsync(document, item, cancellationToken).ConfigureAwait(false),
            _ => throw new StageFailedException($"unsupported format: {item.Kind} ({item.MimeType}, {item.FileName})"),
        };

        Timeline.Transition(document, DocumentStatus.Normalized, details);
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Advanced;
    }

    private async Task<object> NormalizePdfAsync(Document document, IngestionItem item, CancellationToken cancellationToken)
    {
        var bytes = await blobs.ReadAsync(item.Blob, cancellationToken).ConfigureAwait(false);

        PdfInfo info;
        await using (var stream = PipelineBlobs.AsStream(bytes))
        {
            info = await pdf.InspectAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        var keys = new List<string>(info.PageCount);
        await using (var stream = PipelineBlobs.AsStream(bytes))
        {
            await foreach (var page in pdf.RenderPagesAsync(stream, _options.RenderDpi, cancellationToken).ConfigureAwait(false))
            {
                var stored = await blobs.PutDerivedAsync(document.TenantId, page.Png, PipelineBlobs.PngContentType, cancellationToken).ConfigureAwait(false);
                keys.Add(stored.Key);
            }
        }

        Domain.Text.TextLayer textLayer;
        await using (var stream = PipelineBlobs.AsStream(bytes))
        {
            textLayer = await pdf.ExtractTextAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        EmbeddedXml? embedded;
        await using (var stream = PipelineBlobs.AsStream(bytes))
        {
            embedded = await pdf.FindEmbeddedInvoiceXmlAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        document.PageCount = info.PageCount;
        document.PageStart = 1;
        document.PageEnd = Math.Max(1, info.PageCount);
        document.NormalizedBlob = PipelineBlobs.Clone(item.Blob);
        document.PageImageKeys = keys;
        document.TextLayerBlob = PipelineBlobs.ToEntity(await blobs.PutTextLayerAsync(document.TenantId, textLayer, cancellationToken).ConfigureAwait(false));
        document.EmbeddedXmlBlob = embedded is null
            ? null
            : PipelineBlobs.ToEntity(await blobs.PutDerivedAsync(document.TenantId, embedded.Content, PipelineBlobs.XmlContentType, cancellationToken).ConfigureAwait(false));

        flags.Replace(document, OwnedFlags, info.HasTextLayer ? [] : [NoTextLayer()]);

        return new
        {
            kind = item.Kind,
            pageCount = info.PageCount,
            hasTextLayer = info.HasTextLayer,
            words = textLayer.WordCount,
            isEncrypted = info.IsEncrypted,
            producer = info.Producer,
            embeddedXml = embedded?.FileName,
            embeddedXmlFlavour = embedded?.Flavour,
            renderDpi = _options.RenderDpi,
        };
    }

    private async Task<object> NormalizeImageAsync(Document document, IngestionItem item, CancellationToken cancellationToken)
    {
        var bytes = await blobs.ReadAsync(item.Blob, cancellationToken).ConfigureAwait(false);
        var png = ImageConversion.ToPng(bytes)
            ?? throw new StageFailedException($"unsupported image: {item.MimeType} ({item.FileName}) could not be decoded");

        var stored = await blobs.PutDerivedAsync(document.TenantId, png, PipelineBlobs.PngContentType, cancellationToken).ConfigureAwait(false);
        document.PageCount = 1;
        document.PageStart = 1;
        document.PageEnd = 1;
        document.NormalizedBlob = PipelineBlobs.Clone(item.Blob);
        document.PageImageKeys = [stored.Key];
        document.TextLayerBlob = null;
        document.EmbeddedXmlBlob = null;
        flags.Replace(document, OwnedFlags, [NoTextLayer()]);

        return new { kind = item.Kind, pageCount = 1, hasTextLayer = false, mimeType = item.MimeType, convertedToPng = !ReferenceEquals(png, bytes) };
    }

    private async Task<object> NormalizeXmlAsync(Document document, IngestionItem item, CancellationToken cancellationToken)
    {
        var bytes = await blobs.ReadAsync(item.Blob, cancellationToken).ConfigureAwait(false);
        var detection = eInvoices.Detect(bytes)
            ?? throw new StageFailedException($"unsupported xml: {item.FileName} is not a UBL or CII e-invoice");

        // An e-invoice has no pages: the structured path reads the XML and the review UI shows the canonical data.
        document.PageCount = 0;
        document.PageStart = 1;
        document.PageEnd = 1;
        document.NormalizedBlob = null;
        document.PageImageKeys = [];
        document.TextLayerBlob = null;
        document.EmbeddedXmlBlob = PipelineBlobs.Clone(item.Blob);
        flags.Replace(document, OwnedFlags, []);

        return new { kind = item.Kind, pageCount = 0, flavour = detection.Flavour, profile = detection.Profile, rootElement = detection.RootElement };
    }

    private static DocumentFlag NoTextLayer() =>
        new(FlagCodes.UnreadablePages, FlagSeverity.Warning, "No text layer; OCR is not configured.");
}
