using System.Text.RegularExpressions;
using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Domain.Text;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Classification;

/// <summary>
/// The cheap splitter of docs/03 stage 3, step 1. Every page after the first collects "document start" signals
/// (page numbering reset, a title in the header, a document number that changed, the supplier header repeating
/// together with a title, a blank separator page before it) and "continuation" signals (page numbering continued,
/// the same document number again, a carry-over marker). A page whose net score reaches
/// <see cref="HeuristicSplitterOptions.StartThreshold"/> starts a new range; ranges are contiguous and cover every
/// page. The result's confidence is the least certain page decision, and <see cref="SplitAnalysis.IsConfident"/>
/// tells the pipeline whether to skip the LLM split (step 2).
/// </summary>
/// <remarks>Without a text layer the splitter cannot see anything: it returns one range with confidence 0.</remarks>
public sealed partial class HeuristicDocumentSplitter(IOptions<HeuristicSplitterOptions> options) : IHeuristicSplitter
{
    private const double HeaderFraction = 1d / 3d;
    private const double NumberingResetScore = 3;
    private const double NumberingContinuedScore = -3;
    private const double HeaderRepeatedScore = 3;
    private const double NumberChangedScore = 3;
    private const double NumberRepeatedScore = -3;
    private const double SupplierHeaderScore = 1;
    private const double ContinuationMarkerScore = -2;

    private readonly HeuristicSplitterOptions _options = options.Value;

    [GeneratedRegex(
        @"(?<!\p{L})(?:Übertrag|Uebertrag|Fortsetzung|continued|carried forward|carry-over|prijenos|riporto|à reporter|report de)(?!\p{L})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationMarker();

    public async Task<IReadOnlyList<PageRange>> SplitAsync(Guid tenantId, TextLayer? textLayer, IReadOnlyList<ReadOnlyMemory<byte>> pagePngs, CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeAsync(tenantId, textLayer, pagePngs, cancellationToken).ConfigureAwait(false);
        return analysis.Ranges;
    }

    public Task<SplitAnalysis> AnalyzeAsync(Guid tenantId, TextLayer? textLayer, IReadOnlyList<ReadOnlyMemory<byte>> pagePngs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Analyze(textLayer, pagePngs.Count));
    }

    /// <summary>Synchronous core. <paramref name="pageCountHint"/> (the number of page images) is used when there is no text layer.</summary>
    public SplitAnalysis Analyze(TextLayer? textLayer, int pageCountHint)
    {
        var pageCount = textLayer?.Pages.Count > 0 ? textLayer.Pages.Count : pageCountHint;
        if (pageCount <= 0)
        {
            return new SplitAnalysis([], 0, false, []);
        }

        if (textLayer is null || textLayer.Pages.Count == 0 || textLayer.WordCount == 0)
        {
            return new SplitAnalysis([new PageRange(1, pageCount)], 0, false, []);
        }

        var pages = textLayer.Pages.OrderBy(p => p.Number).Select(IndexedPageText.From).ToList();
        if (pages.Count == 1)
        {
            return new SplitAnalysis([new PageRange(pages[0].Page.Number, pages[0].Page.Number)], 1, true, []);
        }

        var ranges = new List<PageRange>();
        var signals = new List<SplitSignal>();
        var certainties = new List<double>();
        var state = new RangeState(pages[0], PageEvidence.Of(pages[0]), _options.HeaderBandFraction);
        var rangeStart = 0;
        var pendingStart = false;

        for (var i = 1; i < pages.Count; i++)
        {
            var page = pages[i];

            if (page.WordCount <= _options.BlankPageMaxWords)
            {
                signals.Add(new SplitSignal(page.Page.Number, SplitSignalKind.BlankSeparatorPage, $"{page.WordCount} words"));
                certainties.Add(1);
                pendingStart = true;
                continue;
            }

            var evidence = PageEvidence.Of(page);
            var pageSignals = new List<SplitSignal>();
            var score = ScorePage(page, evidence, state, pageSignals);
            signals.AddRange(pageSignals);

            var isStart = pendingStart || score >= _options.StartThreshold;
            certainties.Add(pendingStart
                ? 1
                : pageSignals.Count == 0 ? _options.NoSignalCertainty : Math.Min(1, Math.Abs(score) / _options.CertainScore));

            if (isStart)
            {
                ranges.Add(new PageRange(pages[rangeStart].Page.Number, pages[i - 1].Page.Number));
                rangeStart = i;
                state = new RangeState(page, evidence, _options.HeaderBandFraction);
            }
            else
            {
                state.Absorb(evidence);
            }

            pendingStart = false;
        }

        ranges.Add(new PageRange(pages[rangeStart].Page.Number, pages[^1].Page.Number));

        var confidence = Math.Round(certainties.Count == 0 ? 1 : certainties.Min(), 3);
        var isConfident = confidence >= _options.ConfidenceThreshold && pages.Count <= _options.MaxConfidentPageCount;
        return new SplitAnalysis(ranges, confidence, isConfident, signals);
    }

    private double ScorePage(IndexedPageText page, PageEvidence evidence, RangeState state, List<SplitSignal> pageSignals)
    {
        var number = page.Page.Number;
        var score = 0d;

        if (evidence.Numbering is { } numbering)
        {
            if (numbering.Current == 1)
            {
                score += NumberingResetScore;
                pageSignals.Add(new SplitSignal(number, SplitSignalKind.PageNumberingReset, numbering.Text));
            }
            else if (state.LastNumbering is not null && numbering.Current == state.LastNumbering.Current + 1)
            {
                score += NumberingContinuedScore;
                pageSignals.Add(new SplitSignal(number, SplitSignalKind.PageNumberingContinued, numbering.Text));
            }
        }

        // A title that carries a number ("Rechnung RE-2026-0042") is a continuation header; its value is judged below instead.
        var headerTitle = evidence.Hits.FirstOrDefault(h => h.Kind == KeywordKind.Title && h.NumberValue is null && h.RelativeY < HeaderFraction);
        if (headerTitle is not null)
        {
            score += HeaderRepeatedScore;
            pageSignals.Add(new SplitSignal(number, SplitSignalKind.DocumentHeaderRepeated, headerTitle.Text));

            var similarity = Jaccard(state.HeaderBand, page.WordsInTopBand(_options.HeaderBandFraction));
            if (similarity >= _options.HeaderSimilarityThreshold)
            {
                score += SupplierHeaderScore;
                pageSignals.Add(new SplitSignal(number, SplitSignalKind.SupplierHeaderRepeated, $"header similarity {similarity:0.00}"));
            }
        }

        var repeated = evidence.Numbers.FirstOrDefault(h => state.HasNumber(h.Rule.Type, h.NumberValue!));
        var changed = evidence.Numbers.FirstOrDefault(h => state.HasOtherNumber(h.Rule.Type, h.NumberValue!));
        if (repeated is not null)
        {
            score += NumberRepeatedScore;
            pageSignals.Add(new SplitSignal(number, SplitSignalKind.DocumentNumberRepeated, $"{repeated.Text} {repeated.NumberValue}"));
        }
        else if (changed is not null)
        {
            score += NumberChangedScore;
            pageSignals.Add(new SplitSignal(number, SplitSignalKind.DocumentNumberChanged, $"{changed.Text} {changed.NumberValue}"));
        }

        var marker = ContinuationMarker().Match(page.Text);
        if (marker.Success && page.RelativeY(page.WordIndexAt(marker.Index)) < HeaderFraction)
        {
            score += ContinuationMarkerScore;
            pageSignals.Add(new SplitSignal(number, SplitSignalKind.ContinuationMarker, marker.Value));
        }

        return score;
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var intersection = left.Count(right.Contains);
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>Everything the splitter reads off one page, computed once.</summary>
    private sealed record PageEvidence(IReadOnlyList<KeywordHit> Hits, IReadOnlyList<KeywordHit> Numbers, PageNumber? Numbering)
    {
        public static PageEvidence Of(IndexedPageText page)
        {
            var hits = KeywordMatcher.FindHits(page);
            var numbers = hits.Where(h => h.Kind is KeywordKind.Label or KeywordKind.Title && h.NumberValue is not null).ToList();
            return new PageEvidence(hits, numbers, PageNumbering.Find(page.Text));
        }
    }

    /// <summary>What the splitter knows about the document range it is currently inside.</summary>
    private sealed class RangeState
    {
        private readonly Dictionary<DocumentType, HashSet<string>> _numbers = [];

        public RangeState(IndexedPageText firstPage, PageEvidence evidence, double headerBandFraction)
        {
            HeaderBand = firstPage.WordsInTopBand(headerBandFraction);
            Absorb(evidence);
        }

        public HashSet<string> HeaderBand { get; }

        public PageNumber? LastNumbering { get; private set; }

        public bool HasNumber(DocumentType type, string value) =>
            _numbers.TryGetValue(type, out var values) && values.Contains(value);

        public bool HasOtherNumber(DocumentType type, string value) =>
            _numbers.TryGetValue(type, out var values) && values.Count > 0 && !values.Contains(value);

        public void Absorb(PageEvidence evidence)
        {
            LastNumbering = evidence.Numbering ?? LastNumbering;
            foreach (var hit in evidence.Numbers)
            {
                if (!_numbers.TryGetValue(hit.Rule.Type, out var values))
                {
                    values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _numbers[hit.Rule.Type] = values;
                }

                values.Add(hit.NumberValue!);
            }
        }
    }
}
