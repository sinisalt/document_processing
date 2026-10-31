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

    /// <summary>How far (in the 1000-wide page space) a label may be printed from its value on the same line.</summary>
    private const double MaxLabelGap = 350;
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
    public ProvenanceMatch LocateText(string? value, int? preferredPage = null, LocateHints? hints = null) =>
        string.IsNullOrWhiteSpace(value) ? ProvenanceMatch.None : Locate([value.Trim()], preferredPage, fuzzy: true, hints: hints);

    /// <summary>
    /// Identifier-like values (document numbers, VAT and tax ids, SKU, GTIN, lot numbers, order references):
    /// exact or normalised match only. A value that differs from the printed one by a single character is not found.
    /// </summary>
    public ProvenanceMatch LocateIdentifier(string? value, int? preferredPage = null, LocateHints? hints = null) =>
        string.IsNullOrWhiteSpace(value) ? ProvenanceMatch.None : Locate([value.Trim()], preferredPage, fuzzy: false, hints: hints);

    /// <summary>
    /// Amounts in their printed formats. A negative amount (credit notes, discounts) is searched with its sign first:
    /// a credit note quotes the original invoice amount next to the credited one, so the unsigned token must not win
    /// when a signed one is on the page. Only when no signed token exists does the unsigned spelling count, and then
    /// at most as a normalised match, since the printed text lacks the sign.
    /// </summary>
    public ProvenanceMatch LocateNumber(decimal? value, int? preferredPage = null, LocateHints? hints = null)
    {
        if (value is not { } amount)
        {
            return ProvenanceMatch.None;
        }

        var signed = Locate(ValueVariants.Number(amount), preferredPage, fuzzy: false, requireNegativeSign: amount < 0, hints: hints);
        if (amount >= 0 || signed.IsFound)
        {
            return signed;
        }

        var unsigned = Locate(ValueVariants.Number(Math.Abs(amount)), preferredPage, fuzzy: false, hints: hints);
        return unsigned.Quality == MatchQuality.Exact ? unsigned with { Quality = MatchQuality.Normalized } : unsigned;
    }

    public ProvenanceMatch LocateDate(DateOnly? value, int? preferredPage = null, LocateHints? hints = null) =>
        value is null ? ProvenanceMatch.None : Locate(ValueVariants.Date(value.Value), preferredPage, fuzzy: false, hints: hints);

    /// <summary>Locates the first of several spellings (currency symbols, title keywords), best quality first.</summary>
    public ProvenanceMatch LocateAny(IReadOnlyList<string> variants, int? preferredPage = null, bool fuzzy = false) =>
        Locate(variants, preferredPage, fuzzy);

    /// <summary>
    /// Locates a line item's description and returns, with it, the anchor its other values are searched under: the
    /// description's row on its page (docs/06 "Provenance without citations"). A quantity of 5 is then the 5 printed
    /// in that row, not the first 5 on the page (a position number, a house number in the address). The row's own
    /// position number (the line number printed left of the description) is never a candidate, and the words the
    /// description matched are claimed, so a number inside the description ("Apfelsaft 1 l") is taken only when the
    /// row has no other. Without a located description there is no anchor and the values are searched page-wide.
    /// </summary>
    public LineLocation LocateLine(string? description, int? preferredPage, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return new LineLocation(ProvenanceMatch.None, null);
        }

        if (LocateCore([description.Trim()], preferredPage, fuzzy: true, requireNegativeSign: false, hints: null) is not { } hit)
        {
            return new LineLocation(ProvenanceMatch.None, null);
        }

        return new LineLocation(hit.Page.ToMatch(hit.Quality, hit.Window), hit.Page.AnchorRow(hit.Window, lineNumber));
    }

    /// <summary>
    /// Exact, then normalised, then (when asked) fuzzy, on the preferred page first. With <paramref name="requireNegativeSign"/>
    /// the variants spell a negative number: a normalised match must carry a minus (leading, trailing or as a word of
    /// its own) or parentheses, so an unsigned token with the same digits is skipped. Edge punctuation is stripped
    /// from both sides of that comparison, so without the flag the sign would not take part in the normalised pass.
    /// <paramref name="hints"/> narrow and rank the candidates of each pass (a line's row, a field's printed label).
    /// </summary>
    private ProvenanceMatch Locate(IReadOnlyList<string> variants, int? preferredPage, bool fuzzy, bool requireNegativeSign = false, LocateHints? hints = null)
    {
        ArgumentNullException.ThrowIfNull(variants);
        if (LocateCore(variants, preferredPage, fuzzy, requireNegativeSign, hints) is not { } hit)
        {
            return ProvenanceMatch.None;
        }

        hints?.Line?.Claim(hit.Window.Start, hit.Window.Length);
        return hit.Page.ToMatch(hit.Quality, hit.Window);
    }

    private Hit? LocateCore(IReadOnlyList<string> variants, int? preferredPage, bool fuzzy, bool requireNegativeSign, LocateHints? hints)
    {
        if (!HasText || variants.Count == 0)
        {
            return null;
        }

        var line = hints?.Line;
        var pages = line is null
            ? PagesInSearchOrder(preferredPage)
            : _pages.Where(p => p.Number == line.Page).ToArray();

        return Best(pages, MatchQuality.Exact, hints, page => variants
                .Select(v => v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(tokens => tokens.Length > 0)
                .SelectMany(page.FindExact))
            ?? Best(pages, MatchQuality.Normalized, hints, page => variants
                .Select(Compact)
                .Where(compact => compact.Length > 0)
                .SelectMany(compact => page.FindCompact(compact, requireNegativeSign)))
            ?? (fuzzy ? Best(pages, MatchQuality.Fuzzy, hints, page => variants.SelectMany(page.FindFuzzy)) : null);
    }

    /// <summary>
    /// The best candidate of one pass: the first in search order (pages, then variants, then position on the page)
    /// among those of the lowest rank. Candidates are produced lazily and the search stops at the first of rank 0, so
    /// a pass without hints costs what its first match costs.
    /// </summary>
    private static Hit? Best(IndexedPage[] pages, MatchQuality quality, LocateHints? hints, Func<IndexedPage, IEnumerable<Window>> candidates)
    {
        Hit? best = null;
        var bestRank = int.MaxValue;
        foreach (var page in pages)
        {
            foreach (var window in candidates(page))
            {
                var rank = page.Rank(window, hints);
                if (rank < 0 || rank >= bestRank)
                {
                    continue;
                }

                best = new Hit(page, window, quality);
                bestRank = rank;
                if (rank == 0)
                {
                    return best;
                }
            }
        }

        return best;
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

    private sealed record Hit(IndexedPage Page, Window Window, MatchQuality Quality);

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

        public IEnumerable<Window> FindExact(string[] tokens)
        {
            if (!_positionsByText.TryGetValue(tokens[0], out var starts))
            {
                yield break;
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
                    yield return new Window(start, tokens.Length);
                }
            }
        }

        public IEnumerable<Window> FindCompact(string compact, bool requireNegativeSign)
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
                        var window = new Window(i, j - i + 1);
                        if (!requireNegativeSign)
                        {
                            yield return window;
                        }
                        else if (WithNegativeSign(window) is { } signed)
                        {
                            yield return signed;
                        }

                        // The window ends where the value does (an unsigned token with the same digits is a different,
                        // positive amount): the next start is the next candidate.
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// The window when its text carries a negative marker (a leading or trailing minus, a minus word of its own,
        /// an opening parenthesis), extended by the closing sign word when one follows; null for an unsigned token.
        /// </summary>
        private Window? WithNegativeSign(Window window)
        {
            var end = window.Start + window.Length;
            var first = Words[window.Start].Text;
            var last = Words[end - 1].Text;

            // A minus or parenthesis that is a word of its own counts only on the amount's line: a dash bullet or a
            // dash at the end of the previous line is not the amount's sign.
            var separateLeadingSign = first.Length == 1 && window.Length > 1;
            if (separateLeadingSign && !SameLine(window.Start, window.Start + 1))
            {
                return null;
            }

            var negative = IsMinus(first[0]) || IsMinus(last[^1]) || first[0] == '(';
            if (end < Words.Length && SameLine(end - 1, end))
            {
                var following = Words[end].Text;

                // "100,00 - 120,00" is a range: a dash followed by another number on the same line is not a sign.
                var trailingMinus = following.Length == 1 && IsMinus(following[0]) && !NumberFollowsOnSameLine(end);
                var closingParenthesis = negative && following == ")";
                if (trailingMinus || closingParenthesis)
                {
                    return new Window(window.Start, window.Length + 1);
                }
            }

            return negative ? window : null;
        }

        private bool NumberFollowsOnSameLine(int signIndex) =>
            signIndex + 1 < Words.Length
            && SameLine(signIndex, signIndex + 1)
            && Words[signIndex + 1].Text.Length > 0
            && char.IsDigit(Words[signIndex + 1].Text[0]);

        /// <summary>Two words are on one line when their boxes overlap vertically.</summary>
        private bool SameLine(int a, int b)
        {
            var first = Words[a].Box;
            var second = Words[b].Box;
            return first.Y0 < second.Y1 && second.Y0 < first.Y1;
        }

        private static bool IsMinus(char c) => c is '-' or '−';

        public IEnumerable<Window> FindFuzzy(string variant)
        {
            var folded = Identifiers.NormalizeName(variant);
            if (folded.Length < MinFuzzyLength)
            {
                yield break;
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
                    yield return new Window(i, 1);
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
                        yield return new Window(i, length);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// How well a candidate fits the hints: -1 when it is ruled out (outside the line's row, the row's position
        /// number, no label where one is required), otherwise 0 for a labelled, unclaimed candidate and higher for an
        /// unlabelled (+2) or already claimed (+1) one.
        /// </summary>
        public int Rank(Window window, LocateHints? hints)
        {
            if (hints is null)
            {
                return 0;
            }

            var rank = 0;
            if (hints.Line is { } line)
            {
                if (line.Page != Number)
                {
                    return -1;
                }

                for (var i = window.Start; i < window.Start + window.Length; i++)
                {
                    if (line.IsExcluded(i) || !line.InRow(Words[i].Box))
                    {
                        return -1;
                    }
                }

                if (line.IsClaimed(window.Start, window.Length))
                {
                    rank += 1;
                }
            }

            if (hints.Labels.Count > 0 && !HasLabel(window, hints.Labels))
            {
                if (hints.LabelRequired)
                {
                    return -1;
                }

                rank += 2;
            }

            return rank;
        }

        /// <summary>
        /// Whether a label word is printed on the candidate's line within <see cref="MaxLabelGap"/> of it, on either
        /// side ("Lieferdatum 28.10.2026", "zzgl. 7 % MwSt."). Labels are folded (upper case, no diacritics); a word,
        /// or a word joined with its neighbour ("Due date" as DUEDATE), matches a label of five or more letters by
        /// prefix and a shorter one exactly.
        /// </summary>
        private bool HasLabel(Window window, IReadOnlyList<string> labels)
        {
            var box = Union(window);
            for (var i = 0; i < Words.Length; i++)
            {
                if (i >= window.Start && i < window.Start + window.Length)
                {
                    continue;
                }

                var word = Words[i];
                if (word.Folded.Length == 0 || !OnLineOf(word.Box, box) || HorizontalGap(word.Box, box) > MaxLabelGap)
                {
                    continue;
                }

                var single = word.Folded.Replace(" ", string.Empty, StringComparison.Ordinal);
                var joined = i + 1 < Words.Length && OnLineOf(Words[i + 1].Box, box)
                    ? single + Words[i + 1].Folded.Replace(" ", string.Empty, StringComparison.Ordinal)
                    : null;
                if (labels.Any(label => IsLabel(single, label) || (joined is not null && IsLabel(joined, label))))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsLabel(string folded, string label) =>
            label.Length >= 5 ? folded.StartsWith(label, StringComparison.Ordinal) : string.Equals(folded, label, StringComparison.Ordinal);

        private static bool OnLineOf(BoundingBox word, BoundingBox line)
        {
            var middle = (word.Y0 + word.Y1) / 2;
            return middle >= line.Y0 && middle <= line.Y1;
        }

        private static double HorizontalGap(BoundingBox a, BoundingBox b) =>
            a.X1 <= b.X0 ? b.X0 - a.X1 : b.X1 <= a.X0 ? a.X0 - b.X1 : 0;

        /// <summary>
        /// The anchor of a line item whose description matched <paramref name="window"/>: its vertical band, the
        /// description's words claimed, and the position number printed left of it ruled out.
        /// </summary>
        public LineAnchor AnchorRow(Window window, int lineNumber)
        {
            var box = Union(window);
            var anchor = new LineAnchor(Number, box.Y0, box.Y1);
            anchor.Claim(window.Start, window.Length);
            var position = lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (var i = 0; i < Words.Length; i++)
            {
                var word = Words[i];
                if (word.Box.X1 <= box.X0 && anchor.InRow(word.Box) && string.Equals(word.Text.TrimEnd('.', ')'), position, StringComparison.Ordinal))
                {
                    anchor.Exclude(i);
                }
            }

            return anchor;
        }

        private BoundingBox Union(Window window)
        {
            var box = Words[window.Start].Box;
            for (var i = window.Start + 1; i < window.Start + window.Length; i++)
            {
                var word = Words[i].Box;
                box = new BoundingBox(Math.Min(box.X0, word.X0), Math.Min(box.Y0, word.Y0), Math.Max(box.X1, word.X1), Math.Max(box.Y1, word.Y1));
            }

            return box;
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
            var raw = new System.Text.StringBuilder(words[0].Text);
            foreach (var word in words[1..])
            {
                raw.Append(' ').Append(word.Text);
            }

            return new ProvenanceMatch(quality, Number, Union(window), raw.ToString());
        }
    }
}

/// <summary>A line item's description match and the anchor its other values are located under (null when not found).</summary>
public sealed record LineLocation(ProvenanceMatch Description, LineAnchor? Anchor);

/// <summary>
/// Narrows and ranks the candidates of one lookup: <see cref="Line"/> confines it to a line item's row,
/// <see cref="Labels"/> prefer (or, with <see cref="LabelRequired"/>, require) a value printed beside one of the
/// field's labels, so the delivery date is the date after "Lieferdatum" rather than the first equal date on the page.
/// </summary>
public sealed record LocateHints
{
    public LineAnchor? Line { get; init; }

    /// <summary>Folded label words (upper case, no diacritics or spaces), e.g. LIEFERDATUM or DUEDATE.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    public bool LabelRequired { get; init; }

    public static LocateHints ForLine(LineAnchor? line) => new() { Line = line };

    public static LocateHints WithLabels(IReadOnlyList<string> labels) => new() { Labels = labels };
}

/// <summary>
/// A line item's row on its page, from its located description: the vertical band its values must sit in, word
/// positions ruled out (the row's position number) and word positions already taken by the line's earlier values,
/// which later values avoid when the row offers another candidate (a unit price and a line net printing one amount).
/// </summary>
public sealed class LineAnchor
{
    private readonly HashSet<int> _excluded = [];
    private readonly HashSet<int> _claimed = [];

    internal LineAnchor(int page, double y0, double y1)
    {
        Page = page;
        Y0 = y0;
        Y1 = y1;
    }

    public int Page { get; }

    public double Y0 { get; }

    public double Y1 { get; }

    /// <summary>A word belongs to the row when its vertical middle lies within the description's band.</summary>
    internal bool InRow(BoundingBox box)
    {
        var middle = (box.Y0 + box.Y1) / 2;
        return middle >= Y0 && middle <= Y1;
    }

    internal void Exclude(int index) => _excluded.Add(index);

    internal bool IsExcluded(int index) => _excluded.Contains(index);

    internal void Claim(int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            _claimed.Add(i);
        }
    }

    internal bool IsClaimed(int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            if (_claimed.Contains(i))
            {
                return true;
            }
        }

        return false;
    }
}
