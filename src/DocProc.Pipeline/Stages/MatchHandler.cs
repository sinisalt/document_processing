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
using DocProc.Mapping.Identification;
using DocProc.Pipeline.Layouts;
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
/// <para>
/// <c>match.layout</c> (docs/11 §8.4 "Where it runs"): a supplier found here by content, or one that replaced a source
/// supplier, has its layouts resolved here. A layout whose rule holds sets the layout and corrects a type the keyword
/// rules or the LLM gave (never one a human, the source or the e-invoice XML fixed); a layout-managed supplier whose
/// layouts all fail gets <c>NEW_LAYOUT</c> with the classified type as the proposal. Replacing a source supplier reverts
/// what <c>classify.identification</c> decided for it (its flags, its layout, a type it identified or proposed).
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
    LayoutIdentification layouts,
    IOptions<PipelineOptions> options,
    StagePipeline pipelines,
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
        // match.layout runs once VAT or IBAN names the supplier (confidence 0.95 or more, docs/11 §7.4): a supplier
        // guessed from its name or mail domain gets no NEW_LAYOUT, so no reviewer teaches it another supplier's design.
        string? layout = null;
        if (match is { Method: "vatId" or "iban" })
        {
            layout = await MatchLayoutAsync(document, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ClearMatchLayout(document);
        }

        Timeline.Transition(document, DocumentStatus.Matched, new
        {
            supplierId = match?.Supplier.Id,
            supplierName = match?.Supplier.Name,
            confidence = match?.Confidence,
            method = match?.Method,
            vatIdsFound = signals.VatIds.Order(StringComparer.Ordinal).ToList(),
            senderDomain = signals.SenderDomain,
            layout,
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

        // What classify.identification decided for the old supplier goes with it (docs/11 §8.5): its flags, its layout
        // and a type its layout identified or proposed, which is classified again for the new supplier.
        layouts.Clear(document, LayoutIdentification.ClassifyStep);
        document.SupplierLayoutVersionId = null;
        if (document.TypeSource is TypeSource.Supplier or TypeSource.Identification or TypeSource.Proposed)
        {
            var allowed = await ClassifyHandler.AllowedTypesAsync(Db, evidence.Supplier.Id, cancellationToken).ConfigureAwait(false);
            var (outcome, _, budget) = await ClassifyHandler.ClassifyPagesAsync(document, blobs, keywords, services, options.Value, cancellationToken, allowed).ConfigureAwait(false);
            ClassifyHandler.SetType(document, outcome);
            pipelines.RecordBudget(document, DocProc.Definitions.Pipelines.StepKindCatalogue.ClassifyLlm, budget);
            flags.Replace(document, [FlagCodes.TypeUnknown], outcome.Type == DocumentTypeKey.Unknown
                ? [FlagCodes.Create(FlagCodes.TypeUnknown, "The document type could not be determined.")]
                : []);
        }

        flags.Replace(document, OwnedFlags, []);
        var layout = await MatchLayoutAsync(document, cancellationToken).ConfigureAwait(false);
        Timeline.Transition(document, DocumentStatus.Matched, new
        {
            supplierId = evidence.Supplier.Id,
            supplierName = evidence.Supplier.Name,
            confidence = evidence.Confidence,
            method = evidence.Method,
            replacedSourceSupplier = routed.Id,
            type = document.Type,
            layout,
        });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Advanced;
    }

    /// <summary>
    /// <c>match.layout</c> for a supplier found by its VAT id or IBAN (docs/11 §7.4, §8.4): resolves its layouts, corrects a classified type
    /// to the identified layout's (<see cref="TypeSource.Identification"/>), turns it into a proposal under
    /// <c>NEW_LAYOUT</c>, and records the step's outcome. Without a supplier (or without <c>supplierLayouts</c>) it clears
    /// what an earlier run of the step decided. Returns the resolution for the timeline.
    /// </summary>
    private async Task<string?> MatchLayoutAsync(Document document, CancellationToken cancellationToken)
    {
        var fixedType = LayoutIdentification.IsTypeFixed(document) ? document.Type.Value : null;
        var identification = await layouts.ResolveAsync(document, fixedType, cancellationToken).ConfigureAwait(false);
        if (identification is not { } found)
        {
            ClearMatchLayout(document);
            return null;
        }

        if (fixedType is null && found.Result is { Resolution: LayoutResolution.Identified, Selected: { } selected })
        {
            var type = DocumentTypeKey.FromStored(selected.Candidate.DocumentTypeKey);
            if (type != document.Type)
            {
                Timeline.Record(document, LayoutEventTypes.TypeCorrected, null, null, new { from = document.Type.Value, to = type.Value, layoutKey = selected.Candidate.LayoutKey, version = selected.Candidate.Version });
            }

            ClassifyHandler.SetType(document, new ClassificationOutcome(type, 1.0, TypeSource.Identification));
            flags.Replace(document, [FlagCodes.TypeUnknown], []);
        }
        else if (fixedType is null && found.Result.Resolution == LayoutResolution.NewLayout && !document.Type.IsUnknown)
        {
            document.TypeSource = TypeSource.Proposed;
        }

        var proposed = document.TypeSource == TypeSource.Proposed || fixedType is not null ? document.Type.Value : null;
        await layouts.RecordAsync(document, LayoutIdentification.MatchStep, found.Result, found.Setting, found.Words, proposed, cancellationToken).ConfigureAwait(false);
        return found.Result.Resolution.ToString();
    }

    /// <summary>Clears what an earlier run of <c>match.layout</c> decided (its layout version, flags and step record).</summary>
    private void ClearMatchLayout(Document document)
    {
        document.SupplierLayoutVersionId = null;
        layouts.Clear(document, LayoutIdentification.MatchStep);
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
