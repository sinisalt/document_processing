using System.Text.Json.Nodes;
using DocProc.Domain.Fields;

namespace DocProc.Mapping.Tables;

/// <summary>One value of a row: the field, the text as printed, the value read from it, and where it is.</summary>
public sealed record TableCell(string Field, string Text, JsonNode? Value, int Page, BoundingBox Box);

/// <summary>A tier price of a row (docs/11 §8.6 <c>tierColumns</c>): the minimum quantity from the column header, the price from the cell.</summary>
public sealed record TableTier(decimal MinQuantity, TableCell Price);

/// <summary>A row the table yielded: the page it starts on, its box there, its values and its tier prices.</summary>
public sealed record TableRow(int Page, BoundingBox Box, IReadOnlyList<TableCell> Cells, IReadOnlyList<TableTier> Tiers)
{
    public TableCell? Cell(string field) => Cells.FirstOrDefault(c => string.Equals(c.Field, field, StringComparison.Ordinal));
}

/// <summary>Why a line did not become a row (docs/11 §8.6 "rows rejected").</summary>
public static class RejectionReasons
{
    /// <summary>A value did not read as its field's kind, or did not match its pattern.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>No value in the row anchor column, and no row above it to continue (or continuation is off).</summary>
    public const string NoAnchor = "noAnchor";

    /// <summary>A continuation line has a second value in a column that is not text.</summary>
    public const string ContinuationConflict = "continuationConflict";

    /// <summary>
    /// Only the row anchor column holds a value, although the mapping fills other columns: a page footer, a title or a
    /// page number printed in that column, not an article.
    /// </summary>
    public const string AnchorOnly = "anchorOnly";
}

/// <summary>A pattern of the mapping (<c>skipRows</c>, <c>stopAt</c>) ran out of time on a line: the table is not read, rather than read as if it did not match.</summary>
public sealed class TablePatternTimeoutException(string pattern, Exception? inner = null)
    : TimeoutException($"The pattern '{pattern}' took too long on a line of the table; simplify it.", inner)
{
    public string Pattern { get; } = pattern;
}

/// <summary>A line (or a row of several lines) that did not become a row, with its text, its box and why.</summary>
public sealed record RejectedLine(int Page, string Text, BoundingBox Box, string Reason, string Message, string? Field = null);

/// <summary>Where a page's dividers came from.</summary>
public static class ColumnsFrom
{
    /// <summary>The mapping page's dividers, as drawn.</summary>
    public const string Mapping = "mapping";

    /// <summary>Measured again on this page's own header row.</summary>
    public const string Header = "header";

    /// <summary>The last page with a header row (this page has none).</summary>
    public const string PreviousHeader = "previousHeader";

    /// <summary>Set by hand for this page (<c>pageOverrides</c>).</summary>
    public const string Override = "override";
}

/// <summary>
/// What the table yielded on one page (docs/11 §12.4 table report and page strip): rows that start on it, the rejected
/// lines (with their text, at most <see cref="ColumnTableSpec.MaxRejectedListed"/> listed), whether its header row was
/// found, the dividers used on it and where they came from, its first three rows as printed, and the band of the page
/// read (<paramref name="Top"/> to <paramref name="Bottom"/>, in page units). <paramref name="Headings"/> are the texts of
/// the group headings read on it (at most <see cref="ColumnTableEngine.MaxHeadingsListed"/>), so a running title or a
/// footer taken for a heading shows; <paramref name="RepeatedLines"/> counts the lines left out as running titles,
/// page numbers or footers (<see cref="ColumnTableSpec.SkipRepeatedLines"/>) and <paramref name="RepeatedTexts"/> lists
/// their texts (at most <see cref="ColumnTableEngine.MaxHeadingsListed"/>), so nothing is left out unseen;
/// <paramref name="LeftOutLines"/> counts the lines with words only in columns the mapping leaves out.
/// </summary>
public sealed record TablePageReport(
    int Page,
    bool HeaderFound,
    int Rows,
    int Rejected,
    IReadOnlyList<RejectedLine> RejectedLines,
    int SkippedLines,
    int GroupHeadings,
    int ContinuationLines,
    IReadOnlyList<ColumnBounds> Columns,
    string ColumnsFrom,
    IReadOnlyList<IReadOnlyDictionary<string, string>> FirstRows,
    double? Top = null,
    double? Bottom = null,
    IReadOnlyList<string>? Headings = null,
    int RepeatedLines = 0,
    int LeftOutLines = 0,
    IReadOnlyList<string>? RepeatedTexts = null);

/// <summary>
/// The table report (docs/11 §8.6, §12.4): totals, the pages read, the pages that yielded no row, the page a
/// <c>stopAt</c> line ended the table on, and the report of every page.
/// </summary>
public sealed record TableReport(
    int Rows,
    int RejectedRows,
    IReadOnlyList<int> Pages,
    IReadOnlyList<int> PagesWithoutRows,
    int? StoppedOnPage,
    IReadOnlyList<TablePageReport> PerPage);

/// <summary>The rows and the report of one run of the table.</summary>
public sealed record TableResult(IReadOnlyList<TableRow> Rows, TableReport Report);
