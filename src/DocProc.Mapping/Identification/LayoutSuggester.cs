using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocProc.Mapping.Identification;

/// <summary>
/// A layout version's similarity profile (docs/11 §6.6 <c>SimilarityProfileJson</c>): the distinct words of page 1 (as
/// identification compares them, at least three characters, at most <see cref="MaxTokens"/>), the page count and the
/// item kind. Used only by <see cref="LayoutSuggester"/> to rank suggestions for a reviewer; it never selects a layout.
/// </summary>
public sealed record SimilarityProfile(IReadOnlyList<string> Tokens, int? PageCount = null, string? ItemKind = null)
{
    public const int MaxTokens = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The profile of a document: its first page's words.</summary>
    public static SimilarityProfile Of(DocumentWords words, string? itemKind = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        var first = words.Pages.Count > 0 ? words.Pages[0] : null;
        return new SimilarityProfile(TokensOf(first?.Words.Select(w => w.Text) ?? []), words.Pages.Count, itemKind);
    }

    /// <summary>The distinct comparable tokens of some words, in first-seen order.</summary>
    public static IReadOnlyList<string> TokensOf(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        return [.. words
            .SelectMany(w => w.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(w => IdentificationRuleEvaluator.Token(w))
            .Where(t => t.Length >= 3 && t.Any(char.IsLetter))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTokens)];
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SimilarityProfile? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SimilarityProfile>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A layout a reviewer may look at for a document that no layout identified (docs/11 §8.4 step 5).</summary>
public sealed record LayoutSuggestion(Guid LayoutId, string LayoutKey, Guid VersionId, string DocumentTypeKey, double Similarity, IReadOnlyList<string> Differences);

/// <summary>
/// Ranks the supplier's existing layouts by similarity to a document none of them identified (docs/11 §8.4 step 5,
/// §12.10): the overlap (Jaccard) of the document's page-1 words with each version's <see cref="SimilarityProfile"/>,
/// plus what of its identification rule failed ("text "Rechnung" (page 1, title area) is not present"). Suggestions
/// are shown, never applied.
/// </summary>
public static class LayoutSuggester
{
    public const int MaxSuggestions = 3;

    /// <summary>The most similar layouts, best first, at most <see cref="MaxSuggestions"/>, one per layout; layouts with no word in common are left out.</summary>
    public static IReadOnlyList<LayoutSuggestion> Suggest(SimilarityProfile document, IEnumerable<(LayoutEvaluation Evaluation, SimilarityProfile? Profile)> layouts)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(layouts);
        var tokens = document.Tokens.ToHashSet(StringComparer.Ordinal);
        return [.. layouts
            .Select(l =>
            {
                var profile = l.Profile ?? new SimilarityProfile(SimilarityProfile.TokensOf(l.Evaluation.Candidate.Rule.Identifiers
                    .Where(i => i.Positive && i.Identifier.Kind == IdentifierKind.Text)
                    .Select(i => i.Identifier.Value)));
                var other = profile.Tokens.ToHashSet(StringComparer.Ordinal);
                var union = tokens.Union(other).Count();
                var similarity = union == 0 ? 0 : (double)tokens.Intersect(other).Count() / union;
                return new LayoutSuggestion(
                    l.Evaluation.Candidate.LayoutId,
                    l.Evaluation.Candidate.LayoutKey,
                    l.Evaluation.Candidate.VersionId,
                    l.Evaluation.Candidate.DocumentTypeKey,
                    Math.Round(similarity, 2),
                    [.. l.Evaluation.Result.Failed.Take(5)]);
            })
            .Where(s => s.Similarity > 0)
            .OrderByDescending(s => s.Similarity)
            .ThenBy(s => s.LayoutKey, StringComparer.Ordinal)
            .DistinctBy(s => s.LayoutId)
            .Take(MaxSuggestions)];
    }
}
