using DocProc.Definitions;
using DocProc.Domain.Documents;

namespace DocProc.Infrastructure.Classification;

/// <summary>
/// What a keyword rule table is built from for one type (docs/11 §9.4): the type key, its definition's
/// <c>classification</c> block, and whether it is a standard type whose block equals its template's, in which case the
/// built-in rules of that type are taken as they are (they are what the template's block was seeded from, §21 step 2).
/// </summary>
public sealed record KeywordTypeSource(DocumentTypeKey Type, ClassificationDefinition? Classification, bool UsesStandardRules);

/// <summary>
/// A set of keyword rules the classifier scores (docs/11 §9.4 <c>KeywordRuleSet</c>): the built-in table
/// (<see cref="Default"/>), or one built per tenant from its published types' <c>classification</c> blocks
/// (<see cref="FromTypes"/>): titles from <c>keywords</c>, number labels from <c>labels</c>, date labels from
/// <c>dateLabels</c> and negative phrases from <c>negatives</c>, per language, with the built-in weights. A type the
/// tenant does not have contributes nothing; a standard type with its template's block keeps the built-in rules.
/// Phrases are matched as escaped literals (whole words, spaces as any white space, hyphens optional), so a tenant's
/// phrase cannot be a pattern; a table holds at most <see cref="MaxPhrasesPerRule"/> phrases of at most
/// <see cref="MaxPhraseLength"/> characters per type, language and kind.
/// </summary>
public sealed class KeywordRuleTable
{
    public const int MaxPhrasesPerRule = 50;
    public const int MaxPhraseLength = 100;

    /// <summary>
    /// The match timeout of one tenant rule on one page. The phrases compile to linear patterns
    /// (<see cref="KeywordRuleSet.PhrasePattern"/>), so this is a backstop, kept short: every rule of a table runs on every
    /// page, on the worker every tenant shares.
    /// </summary>
    internal static readonly TimeSpan TenantTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long the rules of one table may take on one page in all (<see cref="KeywordMatcher"/>): the rules after that
    /// are not run on the page, so a large table cannot hold the worker for rules × <see cref="TenantTimeout"/>.
    /// </summary>
    internal static readonly TimeSpan PageBudget = TimeSpan.FromMilliseconds(500);

    internal KeywordRuleTable(IReadOnlyList<KeywordRule> rules)
    {
        Rules = rules;
    }

    /// <summary>The built-in rules (the seed of the standard templates' classification blocks).</summary>
    public static KeywordRuleTable Default { get; } = new(KeywordRuleSet.Rules);

    internal IReadOnlyList<KeywordRule> Rules { get; }

    /// <summary>The types the table scores.</summary>
    public IReadOnlyCollection<DocumentTypeKey> Types => [.. Rules.Select(r => r.Type).Distinct()];

    /// <summary>The number of rules (type, language and kind).</summary>
    public int Count => Rules.Count;

    /// <summary>A table built from <paramref name="types"/>, in their order.</summary>
    public static KeywordRuleTable FromTypes(IEnumerable<KeywordTypeSource> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var rules = new List<KeywordRule>();
        foreach (var source in types)
        {
            if (source.UsesStandardRules)
            {
                rules.AddRange(KeywordRuleSet.Rules.Where(r => r.Type == source.Type));
                continue;
            }

            var block = source.Classification;
            if (block is null)
            {
                continue;
            }

            Add(rules, source.Type, block.Keywords, KeywordKind.Title, KeywordRuleSet.TitleWeight);
            Add(rules, source.Type, block.Labels, KeywordKind.Label, KeywordRuleSet.LabelWeight);
            Add(rules, source.Type, block.DateLabels, KeywordKind.DateLabel, KeywordRuleSet.LabelWeight);
            Add(rules, source.Type, block.Negatives, KeywordKind.Negative, KeywordRuleSet.NegativeWeight);
        }

        return new KeywordRuleTable(rules);
    }

    private static void Add(List<KeywordRule> rules, DocumentTypeKey type, IReadOnlyDictionary<string, IReadOnlyList<string>>? byLanguage, KeywordKind kind, double weight)
    {
        foreach (var (language, phrases) in byLanguage ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            var usable = phrases
                .Select(p => p.Trim())
                .Where(p => p.Length is > 0 and <= MaxPhraseLength)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxPhrasesPerRule)
                .ToList();
            if (usable.Count > 0)
            {
                rules.Add(new KeywordRule(type, language, kind, KeywordRuleSet.Compile(usable, compiled: false, TenantTimeout), weight, usable));
            }
        }
    }

    /// <summary>Whether a phrase list compiles the way the table compiles it (used by tests and checks).</summary>
    public static bool Matches(string phrase, string text) =>
        KeywordRuleSet.Compile([phrase], compiled: false, TenantTimeout).IsMatch(text);
}
