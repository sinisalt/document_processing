using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 5 (docs/03 "Match supplier and extractor profile"): collects the signals (ids and names from the text layer
/// or from the e-invoice parties, the sender domain from the ingestion) and lets <see cref="SupplierMatcher"/> pick the
/// supplier. No match is not an error: the document carries <c>SUPPLIER_UNMATCHED</c> and review can create the
/// supplier. The extractor profile stays unset until the profile block lands.
/// </summary>
internal sealed class MatchHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    PipelineBlobs blobs,
    IEInvoiceParser eInvoices,
    ILogger<MatchHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    private static readonly string[] OwnedFlags = [FlagCodes.SupplierUnmatched];

    public override PipelineStage Stage => PipelineStage.Match;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Classified;

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        var suppliers = await Db.Suppliers.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var signals = await CollectSignalsAsync(document, cancellationToken).ConfigureAwait(false);
        var match = SupplierMatcher.Match(suppliers, signals);

        document.SupplierId = match?.Supplier.Id;
        document.SupplierMatchConfidence = match?.Confidence;
        document.ExtractorProfileId = null;
        flags.Replace(document, OwnedFlags, match is null
            ? [FlagCodes.Create(FlagCodes.SupplierUnmatched, "No supplier of the tenant matched the document; a new supplier may be needed.")]
            : []);

        Timeline.Transition(document, DocumentStatus.Matched, new
        {
            supplierId = match?.Supplier.Id,
            supplierName = match?.Supplier.Name,
            confidence = match?.Confidence,
            method = match?.Method,
            vatIdsFound = signals.VatIds.Order(StringComparer.Ordinal).ToList(),
            senderDomain = signals.SenderDomain,
        });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Advanced;
    }

    private async Task<SupplierSignals> CollectSignalsAsync(Document document, CancellationToken cancellationToken)
    {
        var senderDomain = SupplierMatcher.DomainOf(document.IngestionItem?.Ingestion?.SourceFrom);

        if (document.EmbeddedXmlBlob is { } xmlBlob)
        {
            var xml = await blobs.ReadAsync(xmlBlob, cancellationToken).ConfigureAwait(false);
            if (eInvoices.Detect(xml) is { } detection)
            {
                var invoice = eInvoices.Parse(xml, detection);
                var supplier = invoice.Supplier.Value;
                var ids = new[] { supplier?.VatId, supplier?.Iban, invoice.PaymentTerms.Value?.Iban }
                    .Select(Identifiers.NormalizeCompact)
                    .Where(id => id is { Length: > 0 })
                    .Select(id => id!)
                    .ToList();
                var vatIds = new HashSet<string>(StringComparer.Ordinal);
                if (Identifiers.NormalizeCompact(supplier?.VatId) is { } vat)
                {
                    vatIds.Add(vat);
                }

                return new SupplierSignals(vatIds, string.Join('|', ids), senderDomain, supplier?.Name ?? string.Empty);
            }
        }

        var textLayer = await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false);
        if (textLayer is null || textLayer.WordCount == 0)
        {
            return SupplierSignals.Empty with { SenderDomain = senderDomain };
        }

        var text = textLayer.PlainText;
        var firstPage = textLayer.Pages.OrderBy(p => p.Number).FirstOrDefault()?.PlainText ?? string.Empty;
        return new SupplierSignals(
            SupplierMatcher.FindVatIds(text),
            Identifiers.NormalizeCompact(text) ?? string.Empty,
            senderDomain,
            firstPage);
    }
}
