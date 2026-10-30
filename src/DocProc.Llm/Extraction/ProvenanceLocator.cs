using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Text;

namespace DocProc.Llm.Extraction;

/// <summary>
/// Finds returned values in the positioned text layer: exact run of words first, then a normalised comparison
/// (whitespace, case, edge punctuation, number and date formats), then fuzzy (diacritics folded, small edit
/// distance, containment). The match gives page, box and raw text for the review UI and the locatability signal
/// behind the field confidence (docs/06 "Provenance without citations").
/// </summary>
public sealed class ProvenanceLocator
{
    private const int MaxWindowWords = 12;
    private const int MinFuzzyLength = 4;
    private const double MaxEditRatio = 0.2;
    private static readonly char[] EdgePunctuation = ['.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '«', '»', '€', '$', '£', '%', '*', '#', '-', '+'];

    private readonly IReadOnlyList<IndexedPage> _pages;

    public static ProvenanceLocator Empty { get; } = new(null);

    public ProvenanceLocator(TextLayer? layer)
    {
        _pages = layer is null
            ? []
            : layer.Pages.OrderBy(p => p.Number).Select(IndexedPage.From).ToList();
    }

    public bool HasText => _pages.Any(p => p.Words.Length > 0);

    public ProvenanceMatch LocateText(string? value, int? preferredPage = null) =>
        string.IsNullOrWhiteSpace(value) ? ProvenanceMatch.None : Locate([value.Trim()], preferredPage, fuzzy: true);

    public ProvenanceMatch LocateNumber(decimal? value, int? preferredPage = null) =>
        value is null ? ProvenanceMatch.None : Locate(ValueVariants.Number(value.Value), preferredPage, fuzzy: false);

    public ProvenanceMatch LocateDate(DateOnly? value, int? preferredPage = null) =>
        value is null ? ProvenanceMatch.None : Locate(ValueVariants.Date(value.Value), preferredPage, fuzzy: false);

    /// <summary>Locates the first of several spellings (currency symbols, title keywords), best quality first.</summary>
    public ProvenanceMatch LocateAny(IReadOnlyList<string> variants, int? preferredPage = null, bool fuzzy = false) =>
        Locate(variants, preferredPage, fuzzy);

    private ProvenanceMatch Locate(IReadOnlyList<string> variants, int? preferredPage, bool fuzzy)
    {
        ArgumentNullException.ThrowIfNull(variants);
        if (!HasText || variants.Count == 0)
        {
            return ProvenanceMatch.None;
        }

        var pages = preferredPage is { } p
            ? _pages.OrderBy(page => page.Number == p ? 0 : 1).ThenBy(page => page.Number).ToList()
            : _pages;

        foreach (var page in pages)
        {
            foreach (var variant in variants)
            {
                var tokens = variant.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length > 0 && page.FindExact(tokens) is { } exact)
                {
                    return page.ToMatch(MatchQuality.Exact, exact);
                }
            }
        }

        foreach (var page in pages)
        {
            foreach (var variant in variants)
            {
                var compact = Compact(variant);
                if (compact.Length > 0 && page.FindCompact(compact) is { } normalized)
                {
                    return page.ToMatch(MatchQuality.Normalized, normalized);
                }
            }
        }

        if (!fuzzy)
        {
            return ProvenanceMatch.None;
        }

        foreach (var page in pages)
        {
            foreach (var variant in variants)
            {
                if (page.FindFuzzy(variant) is { } fuzzyWindow)
                {
                    return page.ToMatch(MatchQuality.Fuzzy, fuzzyWindow);
                }
            }
        }

        return ProvenanceMatch.None;
    }

    /// <summary>Lower-case, no whitespace, punctuation and symbols stripped from both ends only (number formats stay intact).</summary>
    internal static string Compact(string value)
    {
        var chars = value.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars).Trim(EdgePunctuation).ToLowerInvariant();
    }

    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private readonly record struct Window(int Start, int Length);

    private sealed record IndexedWord(string Text, string Compact, string Folded, BoundingBox Box);

    private sealed class IndexedPage(int number, IndexedWord[] words)
    {
        public int Number { get; } = number;

        public IndexedWord[] Words { get; } = words;

        public static IndexedPage From(TextPage page)
        {
            var normalized = page.Width > 0 && page.Height > 0 ? page.Normalize() : page;
            var words = normalized.Words
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .Select(w => new IndexedWord(w.Text.Trim(), Compact(w.Text), Identifiers.NormalizeName(w.Text), w.Box))
                .ToArray();
            return new IndexedPage(page.Number, words);
        }

        public Window? FindExact(string[] tokens)
        {
            for (var i = 0; i + tokens.Length <= Words.Length; i++)
            {
                var all = true;
                for (var k = 0; k < tokens.Length && all; k++)
                {
                    all = string.Equals(Words[i + k].Text, tokens[k], StringComparison.Ordinal);
                }

                if (all)
                {
                    return new Window(i, tokens.Length);
                }
            }

            return null;
        }

        public Window? FindCompact(string compact)
        {
            for (var i = 0; i < Words.Length; i++)
            {
                var length = 0;
                for (var j = i; j < Words.Length && j - i < MaxWindowWords; j++)
                {
                    var piece = Words[j].Compact;
                    if (length + piece.Length > compact.Length)
                    {
                        break;
                    }

                    if (!compact.AsSpan(length, piece.Length).Equals(piece, StringComparison.Ordinal))
                    {
                        break;
                    }

                    length += piece.Length;
                    if (length == compact.Length && piece.Length > 0)
                    {
                        return new Window(i, j - i + 1);
                    }
                }
            }

            return null;
        }

        public Window? FindFuzzy(string variant)
        {
            var folded = Identifiers.NormalizeName(variant);
            if (folded.Length < MinFuzzyLength)
            {
                return null;
            }

            var tokenCount = folded.Count(c => c == ' ') + 1;
            var minWindow = Math.Max(1, tokenCount - 1);
            var maxWindow = Math.Min(MaxWindowWords, tokenCount + 1);
            var maxEdits = (int)Math.Floor(folded.Length * MaxEditRatio);

            for (var i = 0; i < Words.Length; i++)
            {
                if (tokenCount == 1 && Words[i].Folded.Length > folded.Length && Words[i].Folded.Contains(folded, StringComparison.Ordinal))
                {
                    return new Window(i, 1);
                }

                for (var length = minWindow; length <= maxWindow && i + length <= Words.Length; length++)
                {
                    var candidate = string.Join(' ', Words.Skip(i).Take(length).Select(w => w.Folded).Where(f => f.Length > 0));
                    if (candidate.Length == 0 || Math.Abs(candidate.Length - folded.Length) > maxEdits)
                    {
                        continue;
                    }

                    if (string.Equals(candidate, folded, StringComparison.Ordinal) || Levenshtein(candidate, folded) <= maxEdits)
                    {
                        return new Window(i, length);
                    }
                }
            }

            return null;
        }

        public ProvenanceMatch ToMatch(MatchQuality quality, Window window)
        {
            var words = Words.AsSpan(window.Start, window.Length);
            var box = words[0].Box;
            foreach (var word in words[1..])
            {
                box = new BoundingBox(
                    Math.Min(box.X0, word.Box.X0),
                    Math.Min(box.Y0, word.Box.Y0),
                    Math.Max(box.X1, word.Box.X1),
                    Math.Max(box.Y1, word.Box.Y1));
            }

            var raw = string.Join(' ', Words.Skip(window.Start).Take(window.Length).Select(w => w.Text));
            return new ProvenanceMatch(quality, Number, box, raw);
        }
    }
}
