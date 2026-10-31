using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm.Extraction;
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
/// </summary>
internal sealed class SplitHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    PipelineBlobs blobs,
    IHeuristicSplitter heuristics,
    IOptions<HeuristicSplitterOptions> splitterOptions,
    IServiceProvider services,
    IPipelineQueue queue,
    ILogger<SplitHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    // LLM_PROVIDER_UNAVAILABLE is written by the failure recorder when this stage's LLM call gives up; a later run that
    // succeeds owns it too, so the flag does not outlive the outage.
    private static readonly string[] OwnedFlags = [FlagCodes.MultiDocumentSplit, FlagCodes.LlmProviderUnavailable];

    public override PipelineStage Stage => PipelineStage.Split;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Normalized;

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        // A part of an earlier split (a reprocess from Normalize) keeps its range: splitting it again would create
        // its siblings a second time.
        var skipReason = document.EmbeddedXmlBlob is not null ? "e-invoice"
            : document.PageCount <= 1 ? "single page"
            : await IsSplitPartAsync(document, cancellationToken).ConfigureAwait(false) ? "already split"
            : null;
        if (skipReason is not null)
        {
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
        if ((!analysis.IsConfident || tooLong) && services.GetService<LlmDocumentSplitter>() is { } llm)
        {
            ranges = await llm.SplitAsync(document.TenantId, document.Id, textLayer, pages, cancellationToken).ConfigureAwait(false);
            usedLlm = true;
        }

        ranges = SplitRangeNormalizer.Normalize(ranges, document.PageCount);
        var details = new
        {
            ranges = ranges.Select(r => new { start = r.StartPage, end = r.EndPage }).ToList(),
            heuristicConfidence = analysis.Confidence,
            heuristicConfident = analysis.IsConfident,
            tooLongForHeuristics = tooLong,
            usedLlm,
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

        // Stage 2's "no text layer" flag is about every page of the file, so each part carries it on.
        var inherited = document.Flags
            .Where(f => f.Code == FlagCodes.UnreadablePages && f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null)
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
                TextLayerBlob = await SliceTextLayerAsync(document.TenantId, textLayer, range, cancellationToken).ConfigureAwait(false),
                Status = DocumentStatus.Received,
                CreatedAt = now,
                UpdatedAt = now,
            };
            Db.Documents.Add(child);
            Timeline.Record(child, DocumentEventTypes.Received, null, DocumentStatus.Received, new { splitFrom = document.Id, pageStart = child.PageStart, pageEnd = child.PageEnd });
            Timeline.Transition(child, DocumentStatus.Normalized, new { inheritedFrom = document.Id });
            Timeline.Transition(child, DocumentStatus.Split, details);
            flags.Replace(child, OwnedFlags, [splitFlag, .. inherited]);
            siblings.Add(new ProcessDocument(document.TenantId, child.Id, PipelineStage.Classify));
        }

        var first = ranges[0];
        document.PageEnd = pageStart + first.EndPage - 1;
        document.PageCount = first.EndPage - first.StartPage + 1;
        document.NormalizedBlob = null;
        document.PageImageKeys = Slice(imageKeys, first);
        document.TextLayerBlob = await SliceTextLayerAsync(document.TenantId, textLayer, first, cancellationToken).ConfigureAwait(false);
        flags.Replace(document, OwnedFlags, [splitFlag]);
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

    /// <summary>The pages of the range renumbered from 1, so provenance and the viewer count within the new document.</summary>
    private async Task<BlobReference?> SliceTextLayerAsync(Guid tenantId, TextLayer? layer, PageRange range, CancellationToken cancellationToken)
    {
        if (layer is null)
        {
            return null;
        }

        var sliced = Slice(layer, range.StartPage, range.EndPage);
        return PipelineBlobs.ToEntity(await blobs.PutTextLayerAsync(tenantId, sliced, cancellationToken).ConfigureAwait(false));
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
