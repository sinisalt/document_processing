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
/// in the derived container, plus the embedded e-invoice XML when the PDF carries one the parser recognises. A part
/// of an earlier split keeps its page range (a reprocess from Normalize). Images become a single page
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

        // A part of an earlier split (a reprocess from Normalize) is normalised to its own range again, so the
        // split it came from stays the only one; any other document covers the whole file.
        var lastPage = Math.Max(1, info.PageCount);
        var part = await IsSplitPartAsync(document, cancellationToken).ConfigureAwait(false);
        var pageStart = part ? Math.Clamp(document.PageStart, 1, lastPage) : 1;
        var pageEnd = part ? Math.Clamp(document.PageEnd, pageStart, lastPage) : lastPage;

        var keys = new List<string>(pageEnd - pageStart + 1);
        await using (var stream = PipelineBlobs.AsStream(bytes))
        {
            var number = 0;
            await foreach (var page in pdf.RenderPagesAsync(stream, _options.RenderDpi, cancellationToken).ConfigureAwait(false))
            {
                number++;
                if (number < pageStart || number > pageEnd)
                {
                    continue;
                }

                var stored = await blobs.PutDerivedAsync(document.TenantId, page.Png, PipelineBlobs.PngContentType, cancellationToken).ConfigureAwait(false);
                keys.Add(stored.Key);
            }
        }

        Domain.Text.TextLayer textLayer;
        await using (var stream = PipelineBlobs.AsStream(bytes))
        {
            textLayer = await pdf.ExtractTextAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        if (part)
        {
            textLayer = SplitHandler.Slice(textLayer, pageStart, pageEnd);
        }

        // A split part never has an embedded e-invoice (stage 3 does not split a file that carries one).
        EmbeddedXml? embedded = null;
        string? ignoredXml = null;
        if (!part)
        {
            await using var stream = PipelineBlobs.AsStream(bytes);
            embedded = await pdf.FindEmbeddedInvoiceXmlAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        // Only an attachment the e-invoice parser recognises takes the structured path; any other (ZUGFeRD 1.0, a
        // broken file) would fail classification for good, while the pages can still be read.
        if (embedded is not null && eInvoices.Detect(embedded.Content) is null)
        {
            ignoredXml = embedded.FileName;
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Document {DocumentId}: the embedded {File} is not a UBL or CII e-invoice; the pages are read instead.", document.Id, embedded.FileName);
            }

            embedded = null;
        }

        var hasText = part ? textLayer.WordCount > 0 : info.HasTextLayer;
        document.PageCount = part ? pageEnd - pageStart + 1 : info.PageCount;
        document.PageStart = pageStart;
        document.PageEnd = pageEnd;
        document.NormalizedBlob = part ? null : PipelineBlobs.Clone(item.Blob);
        document.PageImageKeys = keys;
        document.TextLayerBlob = PipelineBlobs.ToEntity(await blobs.PutTextLayerAsync(document.TenantId, textLayer, cancellationToken).ConfigureAwait(false));
        document.EmbeddedXmlBlob = embedded is null
            ? null
            : PipelineBlobs.ToEntity(await blobs.PutDerivedAsync(document.TenantId, embedded.Content, PipelineBlobs.XmlContentType, cancellationToken).ConfigureAwait(false));

        flags.Replace(document, OwnedFlags, hasText ? [] : [NoTextLayer()]);

        return new
        {
            kind = item.Kind,
            pageCount = info.PageCount,
            splitPart = part ? new { start = pageStart, end = pageEnd } : null,
            hasTextLayer = hasText,
            words = textLayer.WordCount,
            isEncrypted = info.IsEncrypted,
            producer = info.Producer,
            embeddedXml = embedded?.FileName,
            embeddedXmlFlavour = embedded?.Flavour,
            embeddedXmlIgnored = ignoredXml,
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

    /// <summary>
    /// Blocking, as the catalogue says (docs/02): without OCR nobody has read these pages, so an image-only invoice
    /// must reach a reviewer instead of being auto-approved on the LLM's say-so alone.
    /// </summary>
    internal static DocumentFlag NoTextLayer() =>
        FlagCodes.Create(FlagCodes.UnreadablePages, "No text layer and OCR is not configured; the pages have not been read by anything but the LLM.");
}
