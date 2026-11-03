using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Definitions;
using DocProc.Domain.Fields;
using DocProc.Mapping.Text;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Tables;

/// <summary>
/// The <c>column-table</c> strategy over the normalised text layer (docs/11 §8.6), as the review table tool runs it
/// (§12.4 "apply to all pages"): the columns are mapped once, on one page, and applied to every page in the range.
/// <list type="bullet">
/// <item><description><b>Header repetition.</b> With <see cref="RepeatHeader.Auto"/> the header row is looked for on
/// every other page (a line holding at least <see cref="ColumnTableSpec.MinAnchors"/> of the column headers). Where it
/// is found, the dividers are measured again from where the headers are printed (a page printed shifted or scaled
/// still parses) and rows start below it; a page without one reuses the dividers of the last page that had one.</description></item>
/// <item><description><b>Rows.</b> A line with a value in the row anchor column starts a row. A line without one is a
/// continuation of the row above (also across a page break): its text is appended to the row's text fields.</description></item>
/// <item><description><b>Group headings.</b> A line with text but no value in the price column, printed as one run of
/// text (not spread over the columns), is a heading; it fills the heading field of the rows below it.</description></item>
/// <item><description><b>Tier columns.</b> A column that names its minimum quantity (<see cref="TableColumn.TierMinQuantity"/>,
/// which the proposal reads from a header such as <c>ab 10</c>) yields a tier entry per non-empty cell. A column without
/// a field and without a minimum quantity is left out, whatever its header says: its words take no part in any decision
/// (a line with words only there is skipped, not a heading, a continuation or a conflict).</description></item>
/// <item><description><b>Running lines.</b> With <see cref="ColumnTableSpec.SkipRepeatedLines"/> (the default) a line
/// printed at the same height with the same text (a number standing for any number) on at least half of the document's
/// pages, two at least, is a running title, page number or footer and is left out, unless it reads as a row (a value in
/// the row anchor column and a readable price) or, printed above the page's first row, as a group heading for another
/// category than the current one (a list may open every page with the same heading). A row with a value only in the row anchor column, while the mapping
/// fills other columns, is rejected (<see cref="RejectionReasons.AnchorOnly"/>), not taken for an article.</description></item>
/// <item><description><b>The band read.</b> On the mapping page, the box below its header row; a box drawn only around
/// the header row (nothing printed below the header inside it) reads on down to the bottom page margin, or the foot of the
/// page. On the other pages, from the top page margin (or the top of the page) to the bottom one (or the box's bottom,
/// or the foot of the page for a box around the header only); a header row found there moves the top below it.</description></item>
/// <item><description><b>Report.</b> Per page: rows, rejected lines with their text and reason, header found, the
/// dividers used, the first three rows, the group headings read, the running lines left out (with their text); pages without rows; where
/// a <c>stopAt</c> line ended the table.</description></item>
/// </list>
/// Linear in the number of words; deterministic. A <c>skipRows</c> or <c>stopAt</c> pattern that runs out of time on a
/// line stops the run (<see cref="TablePatternTimeoutException"/>), and so does the cancellation token, which is checked
/// on every line, so a caller can bound the whole run.
/// </summary>
public static partial class ColumnTableEngine
{
    /// <summary>How far outside a column a word's centre may lie and still be read into the nearest column, in page units.</summary>
    public const double ColumnTolerance = 8;

    /// <summary>The widest gap between two words of one run of text (a heading), in page units; columns are further apart.</summary>
    public const double RunGap = 12;

    /// <summary>The first rows of each page the report shows.</summary>
    public const int FirstRowsPerPage = 3;

    /// <summary>The most group headings a page report lists with their text.</summary>
    public const int MaxHeadingsListed = 20;

    /// <summary>How far apart two lines may be printed, in page units, and still be the same running line of two pages.</summary>
    public const double RepeatTolerance = 6;

    /// <exception cref="TablePatternTimeoutException">A <c>skipRows</c> or <c>stopAt</c> pattern ran out of time on a line.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static TableResult Extract(IReadOnlyList<WordPage> pages, ColumnTableSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(spec);
        var run = new Run(spec, pages, cancellationToken);
        return run.Execute();
    }

    /// <summary>
    /// A line's text as compared across pages for running lines, as tokens: lower case, white space collapsed to one
    /// <c>" "</c> token, every run of digits one token, every other run of characters one token (<c>Stand 01.11.2026</c>
    /// is <c>stand</c>, <c>" "</c>, <c>01</c>, <c>.</c>, <c>11</c>, <c>.</c>, <c>2026</c>).
    /// </summary>
    public static IReadOnlyList<string> RepeatTokens(string? text)
    {
        var tokens = new List<string>();
        var source = text ?? string.Empty;
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var end = i + 1;
            if (char.IsWhiteSpace(c))
            {
                while (end < source.Length && char.IsWhiteSpace(source[end]))
                {
                    end++;
                }

                if (tokens.Count > 0 && end < source.Length)
                {
                    tokens.Add(" ");
                }
            }
            else if (char.IsAsciiDigit(c))
            {
                while (end < source.Length && char.IsAsciiDigit(source[end]))
                {
                    end++;
                }

                tokens.Add(source[i..end]);
            }
            else
            {
                while (end < source.Length && !char.IsWhiteSpace(source[end]) && !char.IsAsciiDigit(source[end]))
                {
                    end++;
                }

                tokens.Add(source[i..end].ToLowerInvariant());
            }

            i = end;
        }

        return tokens;
    }

    /// <summary>
    /// Whether the line <paramref name="a"/> of page <paramref name="pageA"/> and the line <paramref name="b"/> of page
    /// <paramref name="pageB"/> are the same running line: the same <see cref="RepeatTokens"/>, where a number matches
    /// when it is the same on both pages or when on each page it is that page's own number (<c>Seite 2 von 14</c> on page
    /// 2 and <c>Seite 13 von 14</c> on page 13). Numbers are compared token by token, never rewritten per page, so a date
    /// whose day equals one page's number (<c>gültig ab 01.04.2026</c> on page 1) still matches the same date on the other
    /// pages; <c>Warengruppe 12</c> on page 5 and <c>Warengruppe 13</c> on page 6 are two texts.
    /// </summary>
    public static bool SameRunningLine(string? a, int pageA, string? b, int pageB) =>
        SameTokens(RepeatTokens(a), pageA, RepeatTokens(b), pageB);

    private static bool SameTokens(IReadOnlyList<string> a, int pageA, IReadOnlyList<string> b, int pageB)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsNumber(a[i]) || !IsNumber(b[i]) || !IsPageNumber(a[i], pageA) || !IsPageNumber(b[i], pageB))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNumber(string token) => token.Length > 0 && char.IsAsciiDigit(token[0]);

    private static bool IsPageNumber(string digits, int page) =>
        digits.Length <= 6 && int.Parse(digits, CultureInfo.InvariantCulture) == page;

    /// <summary>The tokens with every number as <c>#</c>: lines that may be the same running line share it.</summary>
    private static string RepeatKey(IReadOnlyList<string> tokens) =>
        string.Concat(tokens.Select(t => IsNumber(t) ? "#" : t));

    /// <summary>A header text as compared across pages: letters and digits only, lower case (<c>Art.-Nr.</c> is <c>artnr</c>).</summary>
    public static string NormalizeHeader(string? text)
    {
        var builder = new StringBuilder();
        foreach (var c in text ?? string.Empty)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>The foot of a page, in page units.</summary>
    public const double PageBottom = 1000;

    /// <summary>
    /// The minimum quantity a tier header names (<c>ab 10</c> with <c>ab\s+(\d+)</c> is 10), read in
    /// <paramref name="locale"/> (German when null), so <c>ab 1.000</c> is 1000 in German and <c>from 1,000</c> in English; or null.
    /// </summary>
    public static decimal? TierQuantity(Regex? pattern, string? header, ValueLocale? locale = null)
    {
        if (pattern is null || string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        try
        {
            var match = pattern.Match(header);
            var group = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            return match.Success && PrintedValues.TryDecimal(group.Trim(), locale ?? ValueLocale.German, out var quantity) && quantity >= 0 ? quantity : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static bool AnyMatch(IReadOnlyList<Regex> patterns, string text)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(text))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException ex)
            {
                // Not "no match": a pattern that runs out of time on one line would on the next, and the next, so the run
                // stops here instead of spending the time limit on every line of every page.
                throw new TablePatternTimeoutException(pattern.ToString(), ex);
            }
        }

        return false;
    }

    private sealed class Run
    {
        private readonly ColumnTableSpec _spec;
        private readonly Dictionary<int, WordPage> _pages;
        private readonly int _anchor;
        private readonly int? _when;
        private readonly decimal?[] _tiers;
        private readonly string[] _headers;
        private readonly double[] _mappingCentres;
        private readonly int _minAnchors;
        private readonly bool _headerOnly;
        private readonly bool[] _leftOut;
        private readonly Dictionary<int, PageRepeats> _repeated;
        private readonly CancellationToken _ct;
        private readonly List<TableRow> _rows = [];
        private readonly Dictionary<int, PageState> _reports = [];
        private RowBuilder? _current;
        private (string Text, int Page, BoundingBox Box)? _category;

        public Run(ColumnTableSpec spec, IReadOnlyList<WordPage> pages, CancellationToken cancellationToken)
        {
            _spec = spec;
            _ct = cancellationToken;
            _pages = pages.GroupBy(p => p.Number).ToDictionary(g => g.Key, g => g.First());
            if (spec.Columns.Count == 0)
            {
                throw new ArgumentException("A table needs at least one column.", nameof(spec));
            }

            _anchor = IndexOf(spec.RowAnchor) ?? throw new ArgumentException($"No column holds the row anchor '{spec.RowAnchor}'.", nameof(spec));
            var when = spec.GroupHeader?.When;
            _when = when is not null
                ? IndexOf(when) ?? throw new ArgumentException($"No column holds the group heading's '{when}'.", nameof(spec))
                : FirstNumericColumn();

            var mapping = _pages.GetValueOrDefault(spec.Page) ?? throw new ArgumentException($"Page {spec.Page} is not in the document.", nameof(spec));
            _headers = new string[spec.Columns.Count];
            _mappingCentres = new double[spec.Columns.Count];
            _tiers = new decimal?[spec.Columns.Count];
            var band = spec.HeaderRow is { } header
                ? mapping.Words.Where(w => w.Box.CenterY >= header.Y0 && w.Box.CenterY <= header.Y1 && w.Box.CenterX >= spec.Region.X0 - ColumnTolerance && w.Box.CenterX <= spec.Region.X1 + ColumnTolerance).ToList()
                : [];
            for (var i = 0; i < spec.Columns.Count; i++)
            {
                var column = spec.Columns[i];
                var inColumn = band.Where(w => w.Box.CenterX >= column.X0 && w.Box.CenterX < column.X1).ToList();
                var printed = column.Header ?? PageGeometry.ReadingText(inColumn);
                _headers[i] = NormalizeHeader(printed);
                _mappingCentres[i] = PageGeometry.Union(inColumn) is { } union ? union.CenterX : column.Center;
                // Only a column that names its minimum quantity is a tier: one the reviewer left out (no field, no quantity)
                // is left out even when its header reads like "ab 10". The header pattern is the proposal's (ColumnProposer).
                _tiers[i] = column.Field is null ? column.TierMinQuantity : null;
                if (column.Field is null && column.TierMinQuantity is not null && spec.TierColumns is null)
                {
                    throw new ArgumentException("A tier column needs tierColumns (the list and fields its entries go to).", nameof(spec));
                }
            }

            // A column without a field and without a tier is left out: its words count for nothing (docs/11 §12.4 step 3).
            _leftOut = [.. Enumerable.Range(0, spec.Columns.Count).Select(i => spec.Columns[i].Field is null && _tiers[i] is null)];

            var anchors = _headers.Count(h => h.Length > 0);
            _minAnchors = spec.MinAnchors > 0 ? Math.Min(spec.MinAnchors, Math.Max(anchors, 1)) : Math.Min(3, anchors);

            // A box drawn only around the header row (docs/11 §12.4 step 1): nothing is printed below the header inside it.
            _headerOnly = spec.HeaderRow is { } headerBand && !mapping.Words.Any(w =>
                w.Box.CenterY > headerBand.Y1 && w.Box.CenterY <= spec.Region.Y1
                && w.Box.CenterX >= spec.Region.X0 && w.Box.CenterX <= spec.Region.X1);
            _repeated = spec.SkipRepeatedLines ? RepeatedWords() : [];
        }

        /// <summary>
        /// The words of running lines, by page (their indexes): lines above the first or below the last line of the page
        /// that reads as a row (by the mapping's dividers), the same running line (<see cref="SameRunningLine"/>) printed
        /// within <see cref="RepeatTolerance"/> of the same height on at least half of the document's pages, two at least.
        /// Lines between rows (continuation lines, headings inside the list) are never running lines, however alike they
        /// are. Every page of the document counts, not only the pages read, so the proposal on one page knows them too.
        /// <see cref="PageRepeats.Above"/> holds the words of those printed above the page's first row.
        /// </summary>
        private Dictionary<int, PageRepeats> RepeatedWords()
        {
            var repeated = new Dictionary<int, PageRepeats>();
            if (_pages.Count < 2)
            {
                return repeated;
            }

            var threshold = Math.Max(2, (_pages.Count + 1) / 2);
            var bounds = _spec.Columns.Select(c => new ColumnBounds(c.X0, c.X1)).ToList();
            var candidates = new List<Candidate>();
            foreach (var page in _pages.Values)
            {
                var lines = PageGeometry.Lines(page.Words);
                var rowLike = lines.Select(l =>
                {
                    _ct.ThrowIfCancellationRequested();
                    var cells = Assign(l, bounds);
                    for (var i = 0; i < cells.Length; i++)
                    {
                        if (_leftOut[i])
                        {
                            cells[i].Clear();
                        }
                    }

                    return ReadsAsRow(cells);
                }).ToList();
                var first = rowLike.IndexOf(true);
                var last = rowLike.LastIndexOf(true);
                for (var l = 0; l < lines.Count; l++)
                {
                    if (first >= 0 && l >= first && l <= last)
                    {
                        continue;
                    }

                    var tokens = RepeatTokens(lines[l].Text);
                    if (tokens.Count > 0)
                    {
                        candidates.Add(new Candidate(RepeatKey(tokens), tokens, lines[l].Box.CenterY, page.Number, lines[l], Above: first < 0 || l < first));
                    }
                }
            }

            foreach (var group in candidates.GroupBy(c => c.Key, StringComparer.Ordinal))
            {
                var byHeight = group.OrderBy(c => c.Y).ToList();
                for (var start = 0; start < byHeight.Count;)
                {
                    var end = start;
                    while (end + 1 < byHeight.Count && byHeight[end + 1].Y - byHeight[start].Y <= RepeatTolerance)
                    {
                        end++;
                    }

                    foreach (var same in SameLines(byHeight.GetRange(start, end - start + 1)))
                    {
                        if (same.Select(c => c.Page).Distinct().Count() < threshold)
                        {
                            continue;
                        }

                        foreach (var candidate in same)
                        {
                            if (!repeated.TryGetValue(candidate.Page, out var words))
                            {
                                repeated[candidate.Page] = words = new PageRepeats([], []);
                            }

                            var indexes = candidate.Line.Words.Select(w => w.Index).ToList();
                            words.Words.UnionWith(indexes);
                            if (candidate.Above)
                            {
                                words.Above.UnionWith(indexes);
                            }
                        }
                    }

                    start = end + 1;
                }
            }

            return repeated;
        }

        /// <summary>
        /// The lines of one height band split into the groups of one running line each: two lines are joined when
        /// <see cref="SameTokens"/> holds for them (the numbers compared token by token, each against its own page).
        /// </summary>
        private static IEnumerable<List<Candidate>> SameLines(List<Candidate> band)
        {
            var parent = Enumerable.Range(0, band.Count).ToArray();
            int Root(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }

                return i;
            }

            for (var i = 0; i < band.Count; i++)
            {
                for (var j = i + 1; j < band.Count; j++)
                {
                    if (band[i].Page != band[j].Page && Root(i) != Root(j) && SameTokens(band[i].Tokens, band[i].Page, band[j].Tokens, band[j].Page))
                    {
                        parent[Root(j)] = Root(i);
                    }
                }
            }

            return Enumerable.Range(0, band.Count).GroupBy(Root).Select(g => g.Select(i => band[i]).ToList());
        }

        private int? IndexOf(string field)
        {
            for (var i = 0; i < _spec.Columns.Count; i++)
            {
                if (string.Equals(_spec.Columns[i].Field, field, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return null;
        }

        private int? FirstNumericColumn()
        {
            for (var i = 0; i < _spec.Columns.Count; i++)
            {
                if (i != _anchor && _spec.Columns[i].Field is { } field && _spec.Fields.TryGetValue(field, out var f)
                    && f.Kind is FieldKinds.Money or FieldKinds.Decimal or FieldKinds.Integer or FieldKinds.Percent)
                {
                    return i;
                }
            }

            return null;
        }

        public TableResult Execute()
        {
            var pages = (_spec.Pages ?? [_spec.Page]).Distinct().Where(_pages.ContainsKey).ToList();
            var lastMeasured = _spec.Columns.Select(c => new ColumnBounds(c.X0, c.X1)).ToList();
            var lastFrom = ColumnsFrom.Mapping;
            int? stoppedOn = null;
            foreach (var number in pages)
            {
                var page = _pages[number];
                var state = new PageState(number);
                _reports[number] = state;
                var overridden = _spec.PageOverrides.FirstOrDefault(o => o.Page == number);
                IReadOnlyList<ColumnBounds> columns;
                double top;
                double bottom;
                if (number == _spec.Page)
                {
                    columns = overridden?.Columns ?? [.. _spec.Columns.Select(c => new ColumnBounds(c.X0, c.X1))];
                    state.ColumnsFrom = overridden is null ? ColumnsFrom.Mapping : ColumnsFrom.Override;
                    top = Math.Max(_spec.Region.Y0, _spec.HeaderRow?.Y1 ?? _spec.Region.Y0);
                    bottom = _headerOnly ? _spec.PageMargins?.Bottom ?? PageBottom : _spec.Region.Y1;
                    state.HeaderFound = _spec.HeaderRow is not null;
                    if (overridden is null)
                    {
                        lastMeasured = [.. columns];
                        lastFrom = ColumnsFrom.Mapping;
                    }
                }
                else
                {
                    top = _spec.PageMargins?.Top ?? 0;
                    bottom = _spec.PageMargins?.Bottom ?? (_headerOnly ? PageBottom : _spec.Region.Y1);
                    columns = overridden?.Columns ?? lastMeasured;
                    state.ColumnsFrom = overridden is not null ? ColumnsFrom.Override : lastFrom == ColumnsFrom.Mapping ? ColumnsFrom.Mapping : ColumnsFrom.PreviousHeader;
                    if (_spec.RepeatHeader == RepeatHeader.Auto && _minAnchors > 0
                        && FindHeader(page, top, bottom, lastMeasured) is { } found)
                    {
                        state.HeaderFound = true;
                        top = found.Bottom;
                        if (overridden is null)
                        {
                            columns = found.Columns;
                            lastMeasured = [.. found.Columns];
                            lastFrom = ColumnsFrom.Header;
                            state.ColumnsFrom = ColumnsFrom.Header;
                        }
                    }
                }

                state.Columns = columns;
                state.Top = top;
                state.Bottom = bottom;
                if (!ReadPage(page, state, columns, top, bottom))
                {
                    stoppedOn = number;
                    break;
                }
            }

            Finish();
            var perPage = pages.Where(_reports.ContainsKey).Select(n => _reports[n].ToReport(_spec.MaxRejectedListed)).ToList();
            var report = new TableReport(
                _rows.Count,
                perPage.Sum(p => p.Rejected),
                [.. perPage.Select(p => p.Page)],
                [.. perPage.Where(p => p.Rows == 0).Select(p => p.Page)],
                stoppedOn,
                perPage);
            return new TableResult(_rows, report);
        }

        /// <summary>Reads the lines of one page; false when a <c>stopAt</c> line ended the table.</summary>
        private bool ReadPage(WordPage page, PageState state, IReadOnlyList<ColumnBounds> columns, double top, double bottom)
        {
            var left = columns.Min(c => c.X0) - ColumnTolerance;
            var right = columns.Max(c => c.X1) + ColumnTolerance;
            var words = page.Words.Where(w => w.Box.CenterY > top && w.Box.CenterY <= bottom && w.Box.CenterX >= left && w.Box.CenterX <= right);
            var repeated = _repeated.GetValueOrDefault(page.Number);
            foreach (var line in PageGeometry.Lines(words))
            {
                _ct.ThrowIfCancellationRequested();
                var text = line.Text;
                if (AnyMatch(_spec.StopAt, text))
                {
                    return false;
                }

                if (AnyMatch(_spec.SkipRows, text))
                {
                    state.Skipped++;
                    continue;
                }

                var cells = Assign(line, columns);
                var leftOutWords = false;
                for (var i = 0; i < cells.Length; i++)
                {
                    if (_leftOut[i] && cells[i].Count > 0)
                    {
                        leftOutWords = true;
                        cells[i].Clear();
                    }
                }

                if (leftOutWords && cells.All(c => c.Count == 0))
                {
                    // Words only in columns the mapping leaves out (a wrapped line of such a column): nothing to read.
                    state.LeftOut++;
                    continue;
                }

                var hasAnchor = cells[_anchor].Count > 0;
                var spans = Spans(line, columns);
                var headingLike = _spec.GroupHeader is not null && IsWithoutPrice(cells) && (spans || IsOneRun(line));
                var continues = headingLike && !hasAnchor && !spans && _current is not null && _spec.Continuation == Continuation.AppendToPrevious && OnlyTextColumns(cells);
                if (repeated is not null && line.Words.All(w => repeated.Words.Contains(w.Index)) && !ReadsAsRow(cells)
                    && !(headingLike && !continues && line.Words.All(w => repeated.Above.Contains(w.Index)) && !string.Equals(_category?.Text, text, StringComparison.Ordinal)))
                {
                    // A running title, page number or footer: left out, and listed by its text. Not a line above the page's
                    // first row that reads as a group heading for another category than the current one: a list may open
                    // every page with the same heading at the same height, and leaving it out would give the rows under it
                    // the category of the previous page's last heading. That line is read as a heading and listed as one.
                    state.Repeated++;
                    if (state.RepeatedTexts.Count < MaxHeadingsListed)
                    {
                        state.RepeatedTexts.Add(text);
                    }

                    continue;
                }

                if (headingLike)
                {
                    if (continues)
                    {
                        Continue(page.Number, line, cells, state);
                    }
                    else
                    {
                        Finish();
                        _category = (text, page.Number, line.Box);
                        state.GroupHeadings++;
                        if (state.Headings.Count < MaxHeadingsListed)
                        {
                            state.Headings.Add(text);
                        }
                    }

                    continue;
                }

                if (hasAnchor)
                {
                    Finish();
                    _current = new RowBuilder(page.Number, _category, _spec.Columns.Count);
                    _current.Add(page.Number, line, cells);
                }
                else if (_spec.Continuation == Continuation.AppendToPrevious && _current is not null)
                {
                    Continue(page.Number, line, cells, state);
                }
                else
                {
                    var anchorField = _spec.Columns[_anchor].Field;
                    state.Reject(new RejectedLine(page.Number, text, line.Box, RejectionReasons.NoAnchor, $"No value in the {anchorField} column, and no row above it to continue.", anchorField));
                }
            }

            return true;
        }

        /// <summary>
        /// A line that reads as a row by itself: a value in the row anchor column and, when the table has a price column,
        /// a price there that reads as its kind (without one, a value in another column the mapping fills). Running lines
        /// are looked for outside the rows only, and a line that reads as a row is never left out as one.
        /// </summary>
        private bool ReadsAsRow(List<IndexedWord>[] cells)
        {
            if (cells[_anchor].Count == 0)
            {
                return false;
            }

            if (_when is not { } when)
            {
                return cells.Where((c, i) => i != _anchor && !_leftOut[i]).Any(c => c.Count > 0);
            }

            if (cells[when].Count == 0 || _spec.Columns[when].Field is not { } key || !_spec.Fields.TryGetValue(key, out var field))
            {
                return false;
            }

            var text = PrintedValues.Collapse(string.Join(' ', cells[when].Select(w => w.Text)));
            return PrintedValues.Parse(field.Kind, text, _spec.Locale, field.Options, field.Pattern).Ok;
        }

        private void Continue(int page, TextLine line, List<IndexedWord>[] cells, PageState state)
        {
            state.Continuations++;
            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i].Count == 0)
                {
                    continue;
                }

                if (!IsTextColumn(i) && _current!.Has(i))
                {
                    _current.Conflict ??= i;
                }
            }

            _current!.Add(page, line, cells);
        }

        /// <summary>The words of a line by column: by centre, or into the nearest column within <see cref="ColumnTolerance"/>.</summary>
        private static List<IndexedWord>[] Assign(TextLine line, IReadOnlyList<ColumnBounds> columns)
        {
            var cells = new List<IndexedWord>[columns.Count];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = [];
            }

            foreach (var word in line.Words)
            {
                var x = word.Box.CenterX;
                var best = -1;
                var distance = double.MaxValue;
                for (var i = 0; i < columns.Count; i++)
                {
                    var d = x < columns[i].X0 ? columns[i].X0 - x : x >= columns[i].X1 ? x - columns[i].X1 : 0;
                    if (d < distance)
                    {
                        distance = d;
                        best = i;
                    }
                }

                if (best >= 0 && distance <= ColumnTolerance)
                {
                    cells[best].Add(word);
                }
            }

            return cells;
        }

        private bool IsWithoutPrice(List<IndexedWord>[] cells)
        {
            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i].Count > 0 && (i == _when || _tiers[i] is not null))
                {
                    return false;
                }
            }

            if (_when is null)
            {
                // No price column: a heading has nothing outside text and anchor columns.
                for (var i = 0; i < cells.Length; i++)
                {
                    if (cells[i].Count > 0 && i != _anchor && !IsTextColumn(i))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>One run of text: no gap between neighbouring words wider than <see cref="RunGap"/>.</summary>
        private static bool IsOneRun(TextLine line)
        {
            for (var i = 1; i < line.Words.Count; i++)
            {
                if (line.Words[i].Box.X0 - line.Words[i - 1].Box.X1 > RunGap)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>A word printed across a divider (a heading centred over the table), which no cell of a row does.</summary>
        private static bool Spans(TextLine line, IReadOnlyList<ColumnBounds> columns) =>
            line.Words.Any(w => columns.Skip(1).Any(c => w.Box.X0 < c.X0 - 2 && w.Box.X1 > c.X0 + 2));

        private bool OnlyTextColumns(List<IndexedWord>[] cells)
        {
            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i].Count > 0 && !IsTextColumn(i))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsTextColumn(int i) =>
            _spec.Columns[i].Field is { } field && _spec.Fields.TryGetValue(field, out var f) && f.Kind is FieldKinds.Text or FieldKinds.String;

        /// <summary>
        /// The first line between <paramref name="top"/> and <paramref name="bottom"/> that prints at least
        /// <c>_minAnchors</c> column headers, and the dividers measured from where they are printed.
        /// </summary>
        private (double Bottom, IReadOnlyList<ColumnBounds> Columns)? FindHeader(WordPage page, double top, double bottom, List<ColumnBounds> reference)
        {
            var words = page.Words.Where(w => w.Box.CenterY > top && w.Box.CenterY <= bottom);
            var lines = PageGeometry.Lines(words);
            var bandHeight = _spec.HeaderRow is { } band ? band.Y1 - band.Y0 : 0;
            for (var l = 0; l < lines.Count; l++)
            {
                var line = lines[l];
                var normalized = line.Words.Select(w => NormalizeHeader(w.Text)).ToList();
                var pairs = new List<(int Column, double From, double To)>();
                for (var c = 0; c < _headers.Length; c++)
                {
                    if (_headers[c].Length == 0)
                    {
                        continue;
                    }

                    var at = FindRun(normalized, _headers[c], line.Words, reference[c]);
                    if (at is { } centre)
                    {
                        pairs.Add((c, _mappingCentres[c], centre));
                    }
                }

                if (pairs.Count < _minAnchors)
                {
                    continue;
                }

                // A header printed over two lines: the lines that still lie within the mapping band's height.
                var headerBottom = line.Box.Y1;
                for (var next = l + 1; next < lines.Count && lines[next].Box.CenterY <= line.Box.Y0 + bandHeight; next++)
                {
                    headerBottom = Math.Max(headerBottom, lines[next].Box.Y1);
                }

                return (headerBottom, Remeasure(pairs));
            }

            return null;
        }

        /// <summary>The centre of the run of words whose joined text is <paramref name="header"/>, the one nearest <paramref name="expected"/>.</summary>
        private static double? FindRun(List<string> normalized, string header, IReadOnlyList<IndexedWord> words, ColumnBounds expected)
        {
            double? best = null;
            var bestDistance = double.MaxValue;
            var expectedCentre = (expected.X0 + expected.X1) / 2;
            for (var start = 0; start < normalized.Count; start++)
            {
                var joined = new StringBuilder();
                for (var end = start; end < normalized.Count && joined.Length < header.Length; end++)
                {
                    joined.Append(normalized[end]);
                    if (joined.Length == header.Length && string.Equals(joined.ToString(), header, StringComparison.Ordinal))
                    {
                        var centre = (words[start].Box.X0 + words[end].Box.X1) / 2;
                        var distance = Math.Abs(centre - expectedCentre);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = centre;
                        }
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// The mapping's dividers moved to this page: a linear fit of the header centres (shift and scale) when two or more
        /// headers far enough apart were found and the scale is plausible, else the median shift.
        /// </summary>
        private IReadOnlyList<ColumnBounds> Remeasure(List<(int Column, double From, double To)> pairs)
        {
            double scale = 1;
            double shift;
            var spread = pairs.Max(p => p.From) - pairs.Min(p => p.From);
            if (pairs.Count >= 2 && spread > 50)
            {
                var meanFrom = pairs.Average(p => p.From);
                var meanTo = pairs.Average(p => p.To);
                var covariance = pairs.Sum(p => (p.From - meanFrom) * (p.To - meanTo));
                var variance = pairs.Sum(p => (p.From - meanFrom) * (p.From - meanFrom));
                scale = variance > 0 ? covariance / variance : 1;
                if (scale is < 0.85 or > 1.15)
                {
                    scale = 1;
                    shift = Median(pairs.Select(p => p.To - p.From));
                }
                else
                {
                    shift = meanTo - (scale * meanFrom);
                }
            }
            else
            {
                shift = Median(pairs.Select(p => p.To - p.From));
            }

            return [.. _spec.Columns.Select(c => new ColumnBounds(Clamp((scale * c.X0) + shift), Clamp((scale * c.X1) + shift)))];
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
        }

        private static double Clamp(double x) => Math.Round(Math.Clamp(x, 0, 1000), 2);

        /// <summary>Turns the row being read into a row, or a rejected line when a value does not read.</summary>
        private void Finish()
        {
            if (_current is not { } row)
            {
                return;
            }

            _current = null;
            var state = _reports[row.Page];
            var text = row.Text;
            if (row.Conflict is { } conflict)
            {
                // "in the price column", or "in column 5" for a column the mapping leaves without a field.
                var field = _spec.Columns[conflict].Field;
                var where = field is null ? $"column {conflict + 1}" : $"the {field} column";
                state.Reject(new RejectedLine(row.Page, text, row.Box, RejectionReasons.ContinuationConflict, $"A continuation line has a second value in {where}.", field));
                return;
            }

            var cells = new List<TableCell>();
            var tiers = new List<TableTier>();
            for (var i = 0; i < _spec.Columns.Count; i++)
            {
                var column = _spec.Columns[i];
                var cellText = row.CellText(i);
                if (cellText.Length == 0)
                {
                    continue;
                }

                if (column.Field is { } key && _spec.Fields.TryGetValue(key, out var field))
                {
                    var parsed = PrintedValues.Parse(field.Kind, cellText, _spec.Locale, field.Options, field.Pattern);
                    if (!parsed.Ok)
                    {
                        state.Reject(new RejectedLine(row.Page, text, row.Box, RejectionReasons.Unreadable, $"{key}: {parsed.Error}", key));
                        return;
                    }

                    cells.Add(new TableCell(key, cellText, parsed.Value, row.Page, row.CellBox(i)));
                }
                else if (_tiers[i] is { } quantity && _spec.TierColumns is { } tierSpec)
                {
                    var parsed = PrintedValues.Parse(tierSpec.PriceKind, cellText, _spec.Locale);
                    if (!parsed.Ok)
                    {
                        var label = $"{tierSpec.Target} {quantity.ToString(CultureInfo.InvariantCulture)}";
                        state.Reject(new RejectedLine(row.Page, text, row.Box, RejectionReasons.Unreadable, $"{label}: {parsed.Error}", tierSpec.Target));
                        return;
                    }

                    tiers.Add(new TableTier(quantity, new TableCell(tierSpec.PriceField, cellText, parsed.Value, row.Page, row.CellBox(i))));
                }
            }

            if (tiers.Count == 0 && cells.Count == 1 && cells[0].Field == _spec.RowAnchor
                && _spec.Columns.Any(c => c.Field is not null && c.Field != _spec.RowAnchor))
            {
                // A page footer, a title or a page number printed in the anchor column reads as an anchor value; an article
                // has more than that.
                state.Reject(new RejectedLine(row.Page, text, row.Box, RejectionReasons.AnchorOnly, $"Only a value in the {_spec.RowAnchor} column ('{cells[0].Text}'): not a row (a page footer or a title?).", _spec.RowAnchor));
                return;
            }

            if (_spec.GroupHeader is { } heading && row.Category is { } category && cells.All(c => c.Field != heading.Target))
            {
                var kind = _spec.Fields.TryGetValue(heading.Target, out var target) ? target.Kind : FieldKinds.String;
                var parsed = PrintedValues.Parse(kind, category.Text, _spec.Locale);
                cells.Add(new TableCell(heading.Target, category.Text, parsed.Ok ? parsed.Value : JsonValue.Create(category.Text), category.Page, category.Box));
            }

            var tableRow = new TableRow(row.Page, row.Box, cells, [.. tiers.OrderBy(t => t.MinQuantity)]);
            _rows.Add(tableRow);
            state.Rows++;
            if (state.FirstRows.Count < FirstRowsPerPage)
            {
                var preview = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var cell in tableRow.Cells)
                {
                    preview[cell.Field] = cell.Text;
                }

                foreach (var tier in tableRow.Tiers)
                {
                    preview[$"{_spec.TierColumns!.Target}:{tier.MinQuantity.ToString(CultureInfo.InvariantCulture)}"] = tier.Price.Text;
                }

                state.FirstRows.Add(preview);
            }
        }
    }

    /// <summary>A line outside the rows of its page that may be a running line: its <see cref="RepeatKey"/>, tokens, height, page, and whether it is printed above the page's first row.</summary>
    private sealed record Candidate(string Key, IReadOnlyList<string> Tokens, double Y, int Page, TextLine Line, bool Above);

    /// <summary>The words of a page's running lines (their indexes), and of those printed above its first row.</summary>
    private sealed record PageRepeats(HashSet<int> Words, HashSet<int> Above);

    /// <summary>A row being read: its lines (possibly over a page break) and the words of each column.</summary>
    private sealed class RowBuilder(int page, (string Text, int Page, BoundingBox Box)? category, int columns)
    {
        private readonly List<IndexedWord>[] _cells = [.. Enumerable.Range(0, columns).Select(_ => new List<IndexedWord>())];
        private readonly List<IndexedWord>[] _cellsOnStartPage = [.. Enumerable.Range(0, columns).Select(_ => new List<IndexedWord>())];
        private readonly List<string> _lines = [];
        private BoundingBox? _box;

        public int Page { get; } = page;

        public (string Text, int Page, BoundingBox Box)? Category { get; } = category;

        /// <summary>The index of the column a continuation line gave a second value in, when one did.</summary>
        public int? Conflict { get; set; }

        public BoundingBox Box => _box ?? default;

        public string Text => string.Join(" / ", _lines);

        public bool Has(int column) => _cells[column].Count > 0;

        public void Add(int page, TextLine line, List<IndexedWord>[] cells)
        {
            _lines.Add(line.Text);
            if (page == Page)
            {
                _box = PageGeometry.Union(_box is { } b ? [b, line.Box] : [line.Box]);
            }

            for (var i = 0; i < cells.Length; i++)
            {
                _cells[i].AddRange(cells[i]);
                if (page == Page)
                {
                    _cellsOnStartPage[i].AddRange(cells[i]);
                }
            }
        }

        public string CellText(int column) => PrintedValues.Collapse(string.Join(' ', _cells[column].Select(w => w.Text)));

        public BoundingBox CellBox(int column) => PageGeometry.Union(_cellsOnStartPage[column]) ?? Box;
    }

    private sealed class PageState(int page)
    {
        private readonly List<RejectedLine> _rejected = [];

        public int Page { get; } = page;

        public bool HeaderFound { get; set; }

        public int Rows { get; set; }

        public int Rejected { get; private set; }

        public int Skipped { get; set; }

        public int GroupHeadings { get; set; }

        public int Continuations { get; set; }

        public int Repeated { get; set; }

        public int LeftOut { get; set; }

        public List<string> Headings { get; } = [];

        public List<string> RepeatedTexts { get; } = [];

        public IReadOnlyList<ColumnBounds> Columns { get; set; } = [];

        public double Top { get; set; }

        public double Bottom { get; set; }

        public string ColumnsFrom { get; set; } = Tables.ColumnsFrom.Mapping;

        public List<IReadOnlyDictionary<string, string>> FirstRows { get; } = [];

        public void Reject(RejectedLine line)
        {
            Rejected++;
            _rejected.Add(line);
        }

        public TablePageReport ToReport(int maxListed) =>
            new(Page, HeaderFound, Rows, Rejected, [.. _rejected.Take(maxListed)], Skipped, GroupHeadings, Continuations, Columns, ColumnsFrom, FirstRows, Math.Round(Top, 2), Math.Round(Bottom, 2), [.. Headings], Repeated, LeftOut, [.. RepeatedTexts]);
    }
}
