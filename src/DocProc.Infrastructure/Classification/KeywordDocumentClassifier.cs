using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Domain.Text;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Classification;

/// <summary>
/// The cheap classifier of docs/03 stage 4: multilingual keyword rules over the text layer, weighted by position
/// (first page, header third), with references to other documents demoted and negative phrases subtracted. Below
/// <see cref="KeywordClassifierOptions.ConfidenceThreshold"/> it answers <see cref="DocumentType.Unknown"/> so the
/// pipeline can spend an LLM call with the first page image; it never looks at the image itself.
/// </summary>
/// <remarks>
/// Scoring: each rule counts at most once per page (its best-placed hit), so a statement listing twenty invoice
/// numbers does not become an invoice. Confidence is the winner's share of all positive evidence, scaled down when
/// the winner has less than <see cref="KeywordClassifierOptions.StrongEvidenceScore"/> points.
/// </remarks>
public sealed class KeywordDocumentClassifier(IOptions<KeywordClassifierOptions> options) : IDocumentClassifier
{
    private const double HeaderFraction = 1d / 3d;
    private readonly KeywordClassifierOptions _options = options.Value;

    public Task<ClassificationOutcome> ClassifyAsync(Guid tenantId, TextLayer? textLayer, ReadOnlyMemory<byte>? firstPagePng, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Classify(textLayer));
    }

    /// <summary>Synchronous core, also used by tests and by the splitter's header detection.</summary>
    public ClassificationOutcome Classify(TextLayer? textLayer)
    {
        if (textLayer is null || textLayer.WordCount == 0)
        {
            return new ClassificationOutcome(DocumentType.Unknown, 0, TypeSource.Rule);
        }

        var pages = textLayer.Pages.Select(IndexedPageText.From).ToList();
        var scores = new Dictionary<DocumentType, double>();
        var allHits = new List<KeywordHit>();

        foreach (var page in pages)
        {
            var hits = KeywordMatcher.FindHits(page);
            allHits.AddRange(hits);

            var isFirstPage = page.Page.Number == textLayer.Pages[0].Number;
            foreach (var perRule in hits.GroupBy(h => h.Rule))
            {
                // Positive rules count once per page (their best-placed hit); every distinct negative phrase counts.
                var contribution = perRule.Key.Kind == KeywordKind.Negative
                    ? perRule.DistinctBy(h => h.Text.ToUpperInvariant()).Sum(h => Score(h, isFirstPage))
                    : perRule.Max(h => Score(h, isFirstPage));
                scores[perRule.Key.Type] = scores.GetValueOrDefault(perRule.Key.Type) + contribution;
            }
        }

        var language = LanguageDetector.Detect(pages, allHits);
        var positive = scores.Where(s => s.Value > 0).ToList();
        if (positive.Count == 0)
        {
            return new ClassificationOutcome(DocumentType.Unknown, 0, TypeSource.Rule, language);
        }

        var winner = positive.OrderByDescending(s => s.Value).ThenBy(s => s.Key).First();
        var share = winner.Value / positive.Sum(s => s.Value);
        var strength = Math.Min(1, winner.Value / _options.StrongEvidenceScore);
        var confidence = Math.Round(Math.Clamp(share * strength, 0, 1), 3);

        return confidence >= _options.ConfidenceThreshold
            ? new ClassificationOutcome(winner.Key, confidence, TypeSource.Rule, language)
            : new ClassificationOutcome(DocumentType.Unknown, confidence, TypeSource.Rule, language);
    }

    private double Score(KeywordHit hit, bool isFirstPage)
    {
        var weight = hit.Kind switch
        {
            KeywordKind.Title => KeywordRuleSet.TitleWeight,
            KeywordKind.Label => KeywordRuleSet.LabelWeight,
            KeywordKind.Reference => KeywordRuleSet.ReferenceWeight,
            KeywordKind.Negative => KeywordRuleSet.NegativeWeight,
            _ => 0,
        };

        var factor = !isFirstPage
            ? _options.LaterPageFactor
            : hit.RelativeY < HeaderFraction ? _options.FirstPageHeaderFactor : _options.FirstPageFactor;

        // Negative evidence is not amplified by position: "keine Rechnung" counts the same wherever it is.
        return weight < 0 ? weight : weight * factor;
    }
}
