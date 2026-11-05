using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Pipeline;
using DocProc.Definitions.Pipelines;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Pdf;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm.Extraction;
using DocProc.Mapping.Identification;
using DocProc.Pipeline.Layouts;
using DocProc.Pipeline.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 3 (docs/03 "Split"): heuristics first, the LLM splitter when they are uncertain or the attachment is long,
/// then one <c>Document</c> per page range. The current row keeps the first range (its page images and a sliced text
/// layer); every further range becomes a new row at <c>Split</c>, queued straight for classification. No part keeps
/// the normalised PDF, which holds every range: later stages read a part from its own pages. Single-page files,
/// e-invoices and a part of an earlier split (reprocessed from Normalize) skip all of it.
/// <para>
/// The split prefilter (docs/11 §5.8): for a supplier known from the source with <c>supplierLayouts</c> on, the
/// supplier's layouts are evaluated on the whole item before any cut; when exactly one holds and its type's
/// <c>processing.split.mode</c> is <c>never</c> (a price list), the item stays one document. The prefilter writes no
/// layout and no type: Classify evaluates again on the resulting document.
/// </para>
/// </summary>
internal sealed class SplitHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    PipelineBlobs blobs,
    IHeuristicSplitter heuristics,
    IOptions<HeuristicSplitterOptions> splitterOptions,
    IOptions<PdfProcessingOptions> pdfOptions,
    IServiceProvider services,
    IPipelineQueue queue,
    LayoutIdentification layouts,
    TenantIdentification tenantRules,
    IEffectiveConfigResolver configResolver,
    StagePipeline pipelines,
    ILogger<SplitHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    // LLM_PROVIDER_UNAVAILABLE is written by the failure recorder when this stage's LLM call gives up; a later run that
    // succeeds owns it too, so the flag does not outlive the outage.
    private static readonly string[] OwnedFlags = [FlagCodes.MultiDocumentSplit, FlagCodes.LlmProviderUnavailable];

    // Once the file is split, stage 2's "no text layer" flag is decided per part (see PartFlags), so the parts own it.
    private static readonly string[] OwnedFlagsWhenSplit = [.. OwnedFlags, FlagCodes.UnreadablePages];

    public override PipelineStage Stage => PipelineStage.Split;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Normalized;

    /// <summary>
    /// The split prefilter (docs/11 §5.8 step 2): the reason to keep the item whole when exactly one of the supplier's
    /// layouts holds on it and its type's split mode is <c>never</c>; null otherwise.
    /// </summary>
    private async Task<string?> PrefilterAsync(Document document, CancellationToken cancellationToken)
    {
        var resolved = ClassifyHandler.KnownFromSource(document) ? await layouts.ResolveAsync(document, fixedType: null, cancellationToken).ConfigureAwait(false) : null;
        if (resolved is { Result: { Resolution: LayoutResolution.Identified, Selected: { } selected } })
        {
            var type = DocumentTypeKey.FromStored(selected.Candidate.DocumentTypeKey);
            return await NeverSplitAsync(document, type, cancellationToken).ConfigureAwait(false)
                ? $"split prefilter: layout {selected.Candidate.LayoutKey} holds and {type} is never split"
                : null;
        }

        // The tenant fallback rules (docs/11 §5.8 step 2, P5): when the supplier is unknown, or has no layouts and they are
        // not required, rules of exactly one type that hold on the whole item decide its split mode the same way.
        if (resolved is not null && resolved.Value.Result.Resolution != LayoutResolution.NotManaged)
        {
            return null;
        }

        async Task<DocProc.Mapping.Identification.DocumentWords> Words() =>
            resolved?.Words ?? DocProc.Mapping.Identification.DocumentWords.From(await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false));
        if (await tenantRules.ResolveAsync(document, Words, cancellationToken).ConfigureAwait(false) is { Resolution: DocProc.Mapping.Identification.TenantRuleResolution.Identified, Selected: { } rule })
        {
            var type = DocumentTypeKey.FromStored(rule.Candidate.DocumentTypeKey);
            return await NeverSplitAsync(document, type, cancellationToken).ConfigureAwait(false)
                ? $"split prefilter: tenant rule '{rule.Candidate.Name}' holds and {type} is never split"
                : null;
        }

        return null;
    }

    private async Task<bool> NeverSplitAsync(Document document, DocumentTypeKey type, CancellationToken cancellationToken)
    {
        var config = await configResolver.ResolveAsync(new EffectiveConfigRequest(document.TenantId, type, document.SupplierId), cancellationToken).ConfigureAwait(false);
        return string.Equals(config.Type?.Definition.Processing?.Split?.Mode, "never", StringComparison.Ordinal);
    }

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        // A part of an earlier split (a reprocess from Normalize) keeps its range: splitting it again would create
        // its siblings a second time.
        // A type the source pinned (docs/11 §7.5: a price list dropped in its folder) keeps the file whole: the heuristic
        // splitter would cut a 30-page list at its page breaks.
        // The split steps of the document's pipeline (docs/11 §10.1): a supplier the source named may keep its files whole
        // (split.heuristic mode never), cut them per page (perPage) or always ask the LLM (llm), or not ask it (split.llm off).
        var pipeline = await pipelines.ResolveAsync(document, null, PipelinePhases.Split, cancellationToken).ConfigureAwait(false);
        var mode = pipeline.Enabled(StepKindCatalogue.SplitHeuristic)?.Text("mode") ?? "auto";
        var skipReason = document.EmbeddedXmlBlob is not null ? "e-invoice"
            : document.PageCount <= 1 ? "single page"
            : document.TypeSource == TypeSource.Supplier ? "type pinned by the supplier configuration"
            : await IsSplitPartAsync(document, cancellationToken).ConfigureAwait(false) ? "already split"
            : mode == "never" ? "split mode never (pipeline)"
            : await PrefilterAsync(document, cancellationToken).ConfigureAwait(false);
        if (skipReason is not null)
        {
            pipelines.RecordBudget(document, StepKindCatalogue.SplitLlm, null);
            Timeline.Transition(document, DocumentStatus.Split, new
            {
                ranges = new[] { new { start = document.PageStart, end = document.PageEnd } },
                reason = skipReason,
            });
            await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return StageOutcome.Advanced;
        }

        var textLayer = await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false);
        var pages = await blobs.ReadPageImagesAsync(document, cancellationToken).ConfigureAwait(false);
        var analysis = await heuristics.AnalyzeAsync(document.TenantId, textLayer, pages, cancellationToken).ConfigureAwait(false);

        var ranges = analysis.Ranges;
        var usedLlm = false;
        var tooLong = document.PageCount > splitterOptions.Value.MaxConfidentPageCount;
        DocProc.Llm.Gateway.LlmBudgetExceededException? budget = null;
        if (mode == "perPage")
        {
            ranges = [.. Enumerable.Range(1, document.PageCount).Select(p => new PageRange(p, p))];
        }
        else if ((!analysis.IsConfident || tooLong || mode == "llm") && pipeline.Runs(StepKindCatalogue.SplitLlm) && services.GetService<LlmDocumentSplitter>() is { } llm)
        {
            try
            {
                ranges = await llm.SplitAsync(document.TenantId, document.Id, textLayer, pages, cancellationToken).ConfigureAwait(false);
                usedLlm = true;
            }
            catch (DocProc.Llm.Gateway.LlmBudgetExceededException exception)
            {
                // The tenant's LLM budget (docs/11 §16): the heuristics' ranges stand, and the flag sends the document to review.
                budget = exception;
            }
        }

        pipelines.RecordBudget(document, StepKindCatalogue.SplitLlm, budget);

        ranges = SplitRangeNormalizer.Normalize(ranges, document.PageCount);
        var details = new
        {
            ranges = ranges.Select(r => new { start = r.StartPage, end = r.EndPage }).ToList(),
            heuristicConfidence = analysis.Confidence,
            heuristicConfident = analysis.IsConfident,
            tooLongForHeuristics = tooLong,
            usedLlm,
            mode = mode == "auto" ? null : mode,
            llmBudgetExceeded = budget is null ? (bool?)null : true,
            signals = analysis.Signals.Select(s => new { s.Page, kind = s.Kind, s.Evidence }).ToList(),
        };

        if (ranges.Count <= 1)
        {
            flags.Replace(document, OwnedFlags, []);
            Timeline.Transition(document, DocumentStatus.Split, details);
            await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return StageOutcome.Advanced;
        }

        var now = Timeline.Now;
        var pageStart = document.PageStart;
        var imageKeys = document.PageImageKeys.ToList();
        var siblings = new List<ProcessDocument>();
        var splitFlag = new DocumentFlag(FlagCodes.MultiDocumentSplit, FlagSeverity.Info, $"The attachment was split into {ranges.Count} documents.", Params: FlagParams.Of(("count", FlagParams.Number(ranges.Count))));

        // Stage 2 decides "no text layer" for the whole file: a mixed scan with text on some pages gets no flag. Each
        // part is decided from its own pages instead, as stage 2 does for a part on a reprocess; without a text layer
        // to slice, a part carries the file's flag on.
        var inherited = document.Flags
            .Where(f => f.Code == FlagCodes.UnreadablePages && f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null)
            .Select(f => f.ToDomain())
            .ToList();
        var routed = document.Flags
            .Where(f => Routing.RoutingFlags.IngestOwned.Contains(f.Code) && f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null)
            .Select(f => f.ToDomain())
            .ToList();

        foreach (var range in ranges.Skip(1))
        {
            var child = new Document
            {
                Id = Guid.CreateVersion7(),
                TenantId = document.TenantId,
                IngestionItemId = document.IngestionItemId,
                PageStart = pageStart + range.StartPage - 1,
                PageEnd = pageStart + range.EndPage - 1,
                PageCount = range.EndPage - range.StartPage + 1,
                // The original file holds every range; a part is read from its own page images and text only.
                NormalizedBlob = null,
                PageImageKeys = Slice(imageKeys, range),
                // A part inherits its item's route (docs/11 §6.3); Match re-checks it against the part's content.
                SupplierId = document.SupplierId,
                SupplierMatchMethod = document.SupplierMatchMethod,
                SupplierMatchConfidence = document.SupplierMatchConfidence,
                SupplierSourceRuleId = document.SupplierSourceRuleId,
                ReceivingEntityId = document.ReceivingEntityId,
                Status = DocumentStatus.Received,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var (childLayer, childText) = await SliceTextLayerAsync(document.TenantId, textLayer, range, cancellationToken).ConfigureAwait(false);
            child.TextLayerBlob = childLayer;
            Db.Documents.Add(child);
            Timeline.Record(child, DocumentEventTypes.Received, null, DocumentStatus.Received, new { splitFrom = document.Id, pageStart = child.PageStart, pageEnd = child.PageEnd });
            Timeline.Transition(child, DocumentStatus.Normalized, new { inheritedFrom = document.Id });
            Timeline.Transition(child, DocumentStatus.Split, details);
            flags.Replace(child, OwnedFlagsWhenSplit, [splitFlag, .. PartFlags(childText, inherited), .. routed]);
            siblings.Add(new ProcessDocument(document.TenantId, child.Id, PipelineStage.Classify));
        }

        var first = ranges[0];
        document.PageEnd = pageStart + first.EndPage - 1;
        document.PageCount = first.EndPage - first.StartPage + 1;
        document.NormalizedBlob = null;
        document.PageImageKeys = Slice(imageKeys, first);
        var (firstLayer, firstText) = await SliceTextLayerAsync(document.TenantId, textLayer, first, cancellationToken).ConfigureAwait(false);
        document.TextLayerBlob = firstLayer;
        flags.Replace(document, OwnedFlagsWhenSplit, [splitFlag, .. PartFlags(firstText, inherited)]);
        Timeline.Transition(document, DocumentStatus.Split, details);
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var sibling in siblings)
        {
            await queue.EnqueueAsync(sibling, cancellationToken).ConfigureAwait(false);
        }

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation("Document {DocumentId} split into {Count} documents (LLM: {UsedLlm}).", document.Id, ranges.Count, usedLlm);
        }

        return StageOutcome.Advanced;
    }

    private static List<string> Slice(List<string> keys, PageRange range)
    {
        var start = Math.Clamp(range.StartPage - 1, 0, keys.Count);
        var count = Math.Clamp(range.EndPage - range.StartPage + 1, 0, keys.Count - start);
        return keys.GetRange(start, count);
    }

    /// <summary>
    /// A part's "no text layer" flag, by stage 2's rule (<see cref="PdfProcessingOptions.HasTextLayer"/>): set unless
    /// one of its own pages carries more than <see cref="PdfProcessingOptions.TextLayerMinimumWordsPerPage"/> words,
    /// as stage 2 decides for a part on a reprocess. A scanner footer of a few words is no text layer. Without a text
    /// layer to slice, the file's flag is carried on.
    /// </summary>
    private IEnumerable<DocumentFlag> PartFlags(TextLayer? part, IReadOnlyList<DocumentFlag> inherited) =>
        part is null ? inherited
        : pdfOptions.Value.HasTextLayer(part) ? []
        : [NormalizeHandler.NoTextLayer()];

    /// <summary>The pages of the range renumbered from 1, so provenance and the viewer count within the new document.</summary>
    private async Task<(BlobReference? Blob, TextLayer? Layer)> SliceTextLayerAsync(Guid tenantId, TextLayer? layer, PageRange range, CancellationToken cancellationToken)
    {
        if (layer is null)
        {
            return (null, null);
        }

        var sliced = Slice(layer, range.StartPage, range.EndPage);
        return (PipelineBlobs.ToEntity(await blobs.PutTextLayerAsync(tenantId, sliced, cancellationToken).ConfigureAwait(false)), sliced);
    }

    /// <summary>The pages <paramref name="startPage"/> to <paramref name="endPage"/> of the layer, renumbered from 1.</summary>
    internal static TextLayer Slice(TextLayer layer, int startPage, int endPage)
    {
        var pages = layer.Pages
            .Where(p => p.Number >= startPage && p.Number <= endPage)
            .OrderBy(p => p.Number)
            .Select((p, index) => p with { Number = index + 1 })
            .ToList();
        return new TextLayer(layer.Source, pages);
    }
}
