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
/// Scoring: each positive rule counts at most once per page (its best-placed hit), so a statement listing twenty
/// invoice numbers does not become an invoice; each distinct negative phrase counts once per page across languages
/// ("Proforma" is in four rule tables but is one piece of evidence) and is scaled by position like a title, so one
/// explicit negation in the header offsets a header title. A title found below the header third of the first page
/// while another type already has a title in the header is most likely a reference ("Delivery note DN-5" on an
/// invoice) and is scaled down by <see cref="KeywordClassifierOptions.BodyTitleFactorWhenHeaderTaken"/>. Confidence
/// is the winner's share of all positive evidence, scaled down when the winner has less than
/// <see cref="KeywordClassifierOptions.StrongEvidenceScore"/> points.
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
            var headerOwners = isFirstPage
                ? hits.Where(h => h.Kind == KeywordKind.Title && h.RelativeY < HeaderFraction).Select(h => h.Rule.Type).ToHashSet()
                : [];

            foreach (var perType in hits.GroupBy(h => h.Rule.Type))
            {
                // Positive rules count once per page (their best-placed hit); every distinct negative phrase counts
                // once per page, whichever language tables list it.
                var positiveScore = perType
                    .Where(h => h.Kind != KeywordKind.Negative)
                    .GroupBy(h => h.Rule)
                    .Sum(perRule => perRule.Max(h => Score(h, isFirstPage, headerOwners)));
                var negativeScore = perType
                    .Where(h => h.Kind == KeywordKind.Negative)
                    .GroupBy(h => h.Text.ToUpperInvariant())
                    .Sum(perPhrase => perPhrase.Min(h => Score(h, isFirstPage, headerOwners)));
                scores[perType.Key] = scores.GetValueOrDefault(perType.Key) + positiveScore + negativeScore;
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

    /// <summary>Weight of one hit by kind and position; <paramref name="headerOwners"/> are the types with a title in the header third of this page (first page only).</summary>
    private double Score(KeywordHit hit, bool isFirstPage, HashSet<DocumentType> headerOwners)
    {
        var weight = hit.Kind switch
        {
            KeywordKind.Title => KeywordRuleSet.TitleWeight,
            KeywordKind.Label or KeywordKind.DateLabel => KeywordRuleSet.LabelWeight,
            KeywordKind.Reference => KeywordRuleSet.ReferenceWeight,
            KeywordKind.Negative => KeywordRuleSet.NegativeWeight,
            _ => 0,
        };

        if (!isFirstPage)
        {
            return weight * _options.LaterPageFactor;
        }

        if (hit.RelativeY < HeaderFraction)
        {
            return weight * _options.FirstPageHeaderFactor;
        }

        // "Delivery note DN-5" in the body of a page whose header says "Invoice" refers to another document.
        var referencesAnotherDocument = hit.Kind == KeywordKind.Title && headerOwners.Count > 0 && !headerOwners.Contains(hit.Rule.Type);
        return weight * _options.FirstPageFactor * (referencesAnotherDocument ? _options.BodyTitleFactorWhenHeaderTaken : 1);
    }
}
