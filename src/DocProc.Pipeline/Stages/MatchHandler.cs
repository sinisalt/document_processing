using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Routing;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 5 (docs/03 "Match supplier and extractor profile"): collects the signals (ids and names from the text layer
/// or from the e-invoice parties, the sender domain from the ingestion) and lets <see cref="SupplierMatcher"/> pick the
/// supplier. No match is not an error: the document carries <c>SUPPLIER_UNMATCHED</c> and review can create the
/// supplier. The extractor profile stays unset until the profile block lands. A supplier a reviewer assigned
/// (<c>SupplierMatchMethod.Human</c>) is kept instead of matched again.
/// <para>
/// A supplier the source gave at ingest (a source rule, the channel default or a hint, docs/11 §7.4) is corroborated,
/// not matched again: VAT or IBAN evidence for the same supplier (or none) confirms it; evidence for another supplier
/// keeps an authoritative supplier (an <c>Exclusive</c> rule, a hint) and raises a blocking <c>SUPPLIER_CONFLICT</c>,
/// and replaces a default (<c>DefaultWhenNoContentMatch</c>, the channel default), undoing a type that only the old
/// supplier's configuration pinned (<c>TypeSource=Supplier</c>), which is classified again from the pages.
/// </para>
/// </summary>
internal sealed class MatchHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    PipelineBlobs blobs,
    IEInvoiceParser eInvoices,
    KeywordDocumentClassifier keywords,
    IServiceProvider services,
    IOptions<PipelineOptions> options,
    ILogger<MatchHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    private static readonly string[] OwnedFlags = [FlagCodes.SupplierUnmatched, FlagCodes.SupplierConflict];

    public override PipelineStage Stage => PipelineStage.Match;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Classified;

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        // A supplier a reviewer assigned (docs/11 §12.1 :assign-supplier) is the document's from then on: a reprocess
        // from Classify, or a part of a review split that inherited it, keeps it while the supplier exists.
        if (document.SupplierMatchMethod == SupplierMatchMethod.Human && document.SupplierId is { } assignedId)
        {
            var assignedName = await Db.Suppliers.AsNoTracking().Where(s => s.Id == assignedId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (assignedName is not null)
            {
                flags.Replace(document, OwnedFlags, []);
                Timeline.Transition(document, DocumentStatus.Matched, new { supplierId = assignedId, supplierName = assignedName, confidence = document.SupplierMatchConfidence, method = "human", kept = true });
                await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return StageOutcome.Advanced;
            }
        }

        var suppliers = await Db.Suppliers.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var signals = await CollectSignalsAsync(document, cancellationToken).ConfigureAwait(false);
        if (document.SupplierMatchMethod is SupplierMatchMethod.SourceRule or SupplierMatchMethod.ChannelDefault or SupplierMatchMethod.Hint
            && document.SupplierId is { } routedId
            && suppliers.FirstOrDefault(s => s.Id == routedId) is { } routed)
        {
            return await CorroborateAsync(document, routed, suppliers, signals, cancellationToken).ConfigureAwait(false);
        }

        var match = SupplierMatcher.Match(suppliers, signals);

        document.SupplierId = match?.Supplier.Id;
        document.SupplierMatchConfidence = match?.Confidence;
        document.SupplierMatchMethod = MethodOf(match);
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

    /// <summary>
    /// Match as corroboration (docs/11 §7.4): content evidence is a VAT id (0.98) or an IBAN (0.95) of a supplier; the
    /// sender domain and the name are not evidence against a source rule (the rule is the better signal).
    /// </summary>
    private async Task<StageOutcome> CorroborateAsync(Document document, Supplier routed, IReadOnlyList<Supplier> suppliers, SupplierSignals signals, CancellationToken cancellationToken)
    {
        var content = SupplierMatcher.Match(suppliers, signals with { SenderDomain = null, NameText = string.Empty });
        var evidence = content is { Method: "vatId" or "iban" } ? content : null;
        var rule = document.SupplierSourceRuleId is { } ruleId
            ? await Db.SupplierSourceRules.AsNoTracking().Where(r => r.Id == ruleId).Select(r => new { r.Id, r.Mode }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var exclusive = document.SupplierMatchMethod switch
        {
            SupplierMatchMethod.Hint => true,
            SupplierMatchMethod.ChannelDefault => false,
            // A rule deleted since keeps the mode its confidence recorded.
            _ => rule?.Mode == SourceRuleMode.Exclusive || (rule is null && document.SupplierMatchConfidence >= SupplierSourceResolver.ExclusiveConfidence),
        };

        if (evidence is null || evidence.Supplier.Id == routed.Id)
        {
            flags.Replace(document, OwnedFlags, []);
            Timeline.Transition(document, DocumentStatus.Matched, new
            {
                supplierId = routed.Id,
                supplierName = routed.Name,
                confidence = document.SupplierMatchConfidence,
                method = MethodName(document.SupplierMatchMethod),
                ruleId = document.SupplierSourceRuleId,
                corroborated = evidence is not null,
                evidence = evidence?.Method,
            });
            await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return StageOutcome.Advanced;
        }

        if (exclusive)
        {
            flags.Replace(document, OwnedFlags, [FlagCodes.Create(
                FlagCodes.SupplierConflict,
                $"The document's {(evidence.Method == "vatId" ? "VAT id" : "IBAN")} names {evidence.Supplier.Name}, but its source names {routed.Name}; the source supplier is kept until a reviewer decides.",
                parameters: FlagParams.Of(
                    ("ruleId", document.SupplierSourceRuleId?.ToString()),
                    ("sourceSupplierId", routed.Id.ToString()),
                    ("sourceSupplierName", routed.Name),
                    ("contentSupplierId", evidence.Supplier.Id.ToString()),
                    ("contentSupplierName", evidence.Supplier.Name),
                    ("evidence", evidence.Method)))]);
            Timeline.Transition(document, DocumentStatus.Matched, new
            {
                supplierId = routed.Id,
                supplierName = routed.Name,
                confidence = document.SupplierMatchConfidence,
                method = MethodName(document.SupplierMatchMethod),
                ruleId = document.SupplierSourceRuleId,
                conflict = new { supplierId = evidence.Supplier.Id, supplierName = evidence.Supplier.Name, evidence = evidence.Method },
            });
            await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return StageOutcome.Advanced;
        }

        // A default yields to VAT and IBAN. What only the old supplier's configuration decided is undone: a type its
        // rule, the channel or its allowed types pinned is classified again from the pages (a type a human, a hint or
        // the e-invoice XML set stays).
        var replaced = new
        {
            fromSupplierId = routed.Id,
            fromSupplierName = routed.Name,
            fromMethod = MethodName(document.SupplierMatchMethod),
            ruleId = document.SupplierSourceRuleId,
            toSupplierId = evidence.Supplier.Id,
            toSupplierName = evidence.Supplier.Name,
            evidence = evidence.Method,
            retypedFrom = document.TypeSource == TypeSource.Supplier ? document.Type.Value : null,
        };
        Timeline.Record(document, RoutingEventTypes.SupplierReplacedByContent, null, null, replaced);
        document.SupplierId = evidence.Supplier.Id;
        document.SupplierMatchConfidence = evidence.Confidence;
        document.SupplierMatchMethod = MethodOf(evidence);
        document.SupplierSourceRuleId = null;
        document.ExtractorProfileId = null;
        if (document.TypeSource == TypeSource.Supplier)
        {
            var (outcome, _) = await ClassifyHandler.ClassifyPagesAsync(document, blobs, keywords, services, options.Value, cancellationToken).ConfigureAwait(false);
            ClassifyHandler.SetType(document, outcome);
            flags.Replace(document, [FlagCodes.TypeUnknown], outcome.Type == DocumentTypeKey.Unknown
                ? [FlagCodes.Create(FlagCodes.TypeUnknown, "The document type could not be determined.")]
                : []);
        }

        flags.Replace(document, OwnedFlags, []);
        Timeline.Transition(document, DocumentStatus.Matched, new
        {
            supplierId = evidence.Supplier.Id,
            supplierName = evidence.Supplier.Name,
            confidence = evidence.Confidence,
            method = evidence.Method,
            replacedSourceSupplier = routed.Id,
            type = document.Type,
        });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Advanced;
    }

    private static string? MethodName(SupplierMatchMethod? method) => method switch
    {
        SupplierMatchMethod.SourceRule => "sourceRule",
        SupplierMatchMethod.ChannelDefault => "channelDefault",
        SupplierMatchMethod.Hint => "hint",
        null => null,
        _ => method.ToString(),
    };

    /// <summary>The matcher's method as <c>Documents.SupplierMatchMethod</c> stores it (docs/11 §6.2).</summary>
    private static SupplierMatchMethod? MethodOf(SupplierMatch? match) => match?.Method switch
    {
        null => null,
        "vatId" => SupplierMatchMethod.Vat,
        "iban" => SupplierMatchMethod.Iban,
        "emailDomain" => SupplierMatchMethod.Domain,
        _ => SupplierMatchMethod.Name,
    };

    /// <summary>The parsed e-invoice, or null when it does not parse: the pages' text layer then gives the signals.</summary>
    private Invoice? TryParse(Document document, byte[] xml, EInvoiceDetection detection)
    {
        try
        {
            return eInvoices.Parse(xml, detection);
        }
        catch (EInvoiceParseException ex)
        {
            Logger.LogWarning(ex, "Document {DocumentId}: the stored e-invoice XML could not be parsed; supplier signals come from the text layer.", document.Id);
            return null;
        }
    }

    private async Task<SupplierSignals> CollectSignalsAsync(Document document, CancellationToken cancellationToken)
    {
        var senderDomain = SupplierMatcher.DomainOf(document.IngestionItem?.Ingestion?.SourceFrom);

        if (document.EmbeddedXmlBlob is { } xmlBlob)
        {
            var xml = await blobs.ReadAsync(xmlBlob, cancellationToken).ConfigureAwait(false);
            if (eInvoices.Detect(xml) is { } detection && TryParse(document, xml, detection) is { } invoice)
            {
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
