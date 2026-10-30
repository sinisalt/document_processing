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
/// <remarks>
/// The text layer is indexed once per document (word positions by exact text, compact and folded forms per word)
/// so that the mapper can run hundreds of lookups per invoice. The fuzzy pass is bounded: only windows whose first
/// word is within one edit of the value's first token, or whose last word is within one edit of its last token, are
/// compared, and a window is only built when its folded length is within the edit budget. Identifier-like values
/// (document numbers, VAT ids, IBAN, SKU, GTIN, lot numbers) never take the fuzzy pass: a one-digit-off identifier is
/// a different identifier, so it is <see cref="MatchQuality.NotFound"/> rather than a confident match on the wrong text.
/// </remarks>
public sealed class ProvenanceLocator
{
    private const int MaxWindowWords = 12;
    private const int MinFuzzyLength = 4;
    private const int MinAnchorLength = 3;
    private const double MaxEditRatio = 0.2;
    private static readonly char[] EdgePunctuation = ['.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '«', '»', '€', '$', '£', '%', '*', '#', '-', '+'];

    private readonly IndexedPage[] _pages;

    public static ProvenanceLocator Empty { get; } = new(null);

    public ProvenanceLocator(TextLayer? layer)
    {
        _pages = layer is null
            ? []
            : layer.Pages.OrderBy(p => p.Number).Select(IndexedPage.From).ToArray();
    }

    public bool HasText => _pages.Any(p => p.Words.Length > 0);

    /// <summary>Free text (names, descriptions, notes): exact, normalised, then fuzzy.</summary>
    public ProvenanceMatch LocateText(string? value, int? preferredPage = null) =>
        string.IsNullOrWhiteSpace(value) ? ProvenanceMatch.None : Locate([value.Trim()], preferredPage, fuzzy: true);

    /// <summary>
    /// Identifier-like values (document numbers, VAT and tax ids, IBAN/BIC, SKU, GTIN, lot numbers, order references):
    /// exact or normalised match only. A value that differs from the printed one by a single character is not found.
    /// </summary>
    public ProvenanceMatch LocateIdentifier(string? value, int? preferredPage = null) =>
        string.IsNullOrWhiteSpace(value) ? ProvenanceMatch.None : Locate([value.Trim()], preferredPage, fuzzy: false);

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

        var pages = PagesInSearchOrder(preferredPage);

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

    private IndexedPage[] PagesInSearchOrder(int? preferredPage)
    {
        if (preferredPage is not { } p)
        {
            return _pages;
        }

        var index = -1;
        for (var i = 0; i < _pages.Length; i++)
        {
            if (_pages[i].Number == p)
            {
                index = i;
                break;
            }
        }

        if (index <= 0)
        {
            return _pages;
        }

        var ordered = new IndexedPage[_pages.Length];
        ordered[0] = _pages[index];
        var next = 1;
        for (var i = 0; i < _pages.Length; i++)
        {
            if (i != index)
            {
                ordered[next++] = _pages[i];
            }
        }

        return ordered;
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

    /// <summary>Whether two strings are within one insertion, deletion or substitution of each other; linear, no allocation.</summary>
    public static bool WithinOneEdit(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        if (a.Length == b.Length)
        {
            var mismatches = 0;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i] && ++mismatches > 1)
                {
                    return false;
                }
            }

            return true;
        }

        var longer = a.Length > b.Length ? a : b;
        var shorter = a.Length > b.Length ? b : a;
        var skipped = false;
        var li = 0;
        var si = 0;
        while (li < longer.Length && si < shorter.Length)
        {
            if (longer[li] == shorter[si])
            {
                li++;
                si++;
                continue;
            }

            if (skipped)
            {
                return false;
            }

            skipped = true;
            li++;
        }

        return true;
    }

    private readonly record struct Window(int Start, int Length);

    private sealed record IndexedWord(string Text, string Compact, string Folded, BoundingBox Box);

    private sealed class IndexedPage
    {
        private readonly Dictionary<string, List<int>> _positionsByText;

        private IndexedPage(int number, IndexedWord[] words)
        {
            Number = number;
            Words = words;
            _positionsByText = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < words.Length; i++)
            {
                if (!_positionsByText.TryGetValue(words[i].Text, out var positions))
                {
                    positions = [];
                    _positionsByText[words[i].Text] = positions;
                }

                positions.Add(i);
            }
        }

        public int Number { get; }

        public IndexedWord[] Words { get; }

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
            if (!_positionsByText.TryGetValue(tokens[0], out var starts))
            {
                return null;
            }

            foreach (var start in starts)
            {
                if (start + tokens.Length > Words.Length)
                {
                    break;
                }

                var all = true;
                for (var k = 1; k < tokens.Length && all; k++)
                {
                    all = string.Equals(Words[start + k].Text, tokens[k], StringComparison.Ordinal);
                }

                if (all)
                {
                    return new Window(start, tokens.Length);
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

            var firstSpace = folded.IndexOf(' ', StringComparison.Ordinal);
            var lastSpace = folded.LastIndexOf(' ');
            var firstToken = firstSpace < 0 ? folded : folded[..firstSpace];
            var lastToken = lastSpace < 0 ? folded : folded[(lastSpace + 1)..];
            var tokenCount = folded.Count(c => c == ' ') + 1;
            var minWindow = Math.Max(1, tokenCount - 1);
            var maxWindow = Math.Min(MaxWindowWords, tokenCount + 1);
            var maxEdits = (int)Math.Floor(folded.Length * MaxEditRatio);

            // Anchors: a window is a candidate only when it starts at a word close to the first token or ends at a word
            // close to the last token. One linear pass over the page marks both.
            var startsHere = new bool[Words.Length];
            var endsHere = new bool[Words.Length];
            for (var i = 0; i < Words.Length; i++)
            {
                var word = Words[i].Folded;
                if (word.Length == 0)
                {
                    continue;
                }

                if (tokenCount == 1 && word.Length > folded.Length && word.Contains(folded, StringComparison.Ordinal))
                {
                    return new Window(i, 1);
                }

                startsHere[i] = Anchors(word, firstToken);
                endsHere[i] = Anchors(word, lastToken);
            }

            for (var i = 0; i < Words.Length; i++)
            {
                var candidateLength = 0;
                var pieces = 0;
                for (var length = 1; length <= maxWindow && i + length <= Words.Length; length++)
                {
                    var last = i + length - 1;
                    var piece = Words[last].Folded;
                    if (piece.Length > 0)
                    {
                        candidateLength += pieces == 0 ? piece.Length : piece.Length + 1;
                        pieces++;
                    }

                    if (length < minWindow || candidateLength == 0)
                    {
                        continue;
                    }

                    if (candidateLength - folded.Length > maxEdits)
                    {
                        break;
                    }

                    if (folded.Length - candidateLength > maxEdits || !(startsHere[i] || endsHere[last]))
                    {
                        continue;
                    }

                    var candidate = JoinFolded(i, length);
                    if (string.Equals(candidate, folded, StringComparison.Ordinal) || Levenshtein(candidate, folded) <= maxEdits)
                    {
                        return new Window(i, length);
                    }
                }
            }

            return null;
        }

        /// <summary>A word anchors a token when it is within one edit of it, or one is a prefix of the other (merged or split tokens).</summary>
        private static bool Anchors(string word, string token)
        {
            if (WithinOneEdit(word, token))
            {
                return true;
            }

            var shorter = Math.Min(word.Length, token.Length);
            return shorter >= MinAnchorLength
                && (word.StartsWith(token, StringComparison.Ordinal) || token.StartsWith(word, StringComparison.Ordinal));
        }

        private string JoinFolded(int start, int length)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = start; i < start + length; i++)
            {
                var piece = Words[i].Folded;
                if (piece.Length == 0)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(piece);
            }

            return sb.ToString();
        }

        public ProvenanceMatch ToMatch(MatchQuality quality, Window window)
        {
            var words = Words.AsSpan(window.Start, window.Length);
            var box = words[0].Box;
            var raw = new System.Text.StringBuilder(words[0].Text);
            foreach (var word in words[1..])
            {
                box = new BoundingBox(
                    Math.Min(box.X0, word.Box.X0),
                    Math.Min(box.Y0, word.Box.Y0),
                    Math.Max(box.X1, word.Box.X1),
                    Math.Max(box.Y1, word.Box.Y1));
                raw.Append(' ').Append(word.Text);
            }

            return new ProvenanceMatch(quality, Number, box, raw.ToString());
        }
    }
}
