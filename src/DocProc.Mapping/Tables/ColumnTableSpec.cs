using System.Text.RegularExpressions;
using DocProc.Domain.Fields;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Tables;

/// <summary>
/// One column of a table mapping (docs/11 §12.4 step 3), in page units of the page the mapping was drawn on: its left and
/// right divider, the row field it fills (none for a column the reviewer leaves out), the header text printed above it
/// (what finds the header again on the other pages) and, for a tier price column (<c>ab 10</c>), the minimum quantity
/// of its tier.
/// </summary>
public sealed record TableColumn(double X0, double X1, string? Field = null, string? Header = null, decimal? TierMinQuantity = null)
{
    public double Center => (X0 + X1) / 2;
}

/// <summary>The row field a column fills: its key in the row, its kind, and the checks its printed text must pass.</summary>
public sealed record TableField(string Key, string Kind, Regex? Pattern = null, IReadOnlyList<string>? Options = null);

/// <summary>How the header row is looked for on the pages after the mapping page.</summary>
public enum RepeatHeader
{
    /// <summary>On every page (docs/11 §8.6 <c>repeat: "auto"</c>): where found, the columns are measured again there.</summary>
    Auto,

    /// <summary>Never: every page uses the mapping page's columns.</summary>
    None,
}

/// <summary>What a line without a value in the row anchor column is.</summary>
public enum Continuation
{
    /// <summary>A continuation of the row above (a second description line), also across a page break.</summary>
    AppendToPrevious,

    /// <summary>Not a row: rejected with its text.</summary>
    None,
}

/// <summary>
/// Group headings (docs/11 §8.6 <c>groupHeader</c>): a line with text but no value in <see cref="When"/> (the price
/// column) is a heading, and its text fills <see cref="Target"/> of the rows below it.
/// </summary>
public sealed record GroupHeaderSpec(string Target, string? When = null);

/// <summary>
/// Tier price columns (docs/11 §8.6 <c>tierColumns</c>): a column whose header matches <see cref="HeaderPattern"/>
/// (group 1 the minimum quantity, as in <c>ab\s+(\d+)</c>) is a tier; its cells become entries of the row's
/// <see cref="Target"/> list with <see cref="QuantityField"/> from the header and <see cref="PriceField"/> from the cell.
/// </summary>
public sealed record TierColumnsSpec(Regex? HeaderPattern, string Target = "tiers", string QuantityField = "minQuantity", string PriceField = "price", string PriceKind = Definitions.FieldKinds.Money);

/// <summary>The part of every page the table may use (docs/11 §8.6 <c>pageMargins</c>), in page units.</summary>
public sealed record PageMargins(double? Top, double? Bottom);

/// <summary>The left and right divider of a column on one page, in page units.</summary>
public sealed record ColumnBounds(double X0, double X1);

/// <summary>Dividers set by hand for one page (docs/11 §12.4 <c>pageOverrides</c>), one pair per column in the mapping's order.</summary>
public sealed record PageOverride(int Page, IReadOnlyList<ColumnBounds> Columns);

/// <summary>
/// A table mapping drawn once on one page (docs/11 §12.4 "apply to all pages", the review tool's side of the
/// <c>column-table</c> strategy of §8.6): where the table is on that page, its columns, its header row and row anchor,
/// and how it continues on the other pages.
/// </summary>
public sealed record ColumnTableSpec
{
    /// <summary>The page the mapping was drawn on (1-based).</summary>
    public required int Page { get; init; }

    /// <summary>The table's box on <see cref="Page"/>: rows are read between its top (or below the header row) and its bottom.</summary>
    public required BoundingBox Region { get; init; }

    /// <summary>The columns, left to right.</summary>
    public required IReadOnlyList<TableColumn> Columns { get; init; }

    /// <summary>The fields the columns fill, by key.</summary>
    public required IReadOnlyDictionary<string, TableField> Fields { get; init; }

    /// <summary>The band of the header row on <see cref="Page"/> (only its y range counts); null for a table without one.</summary>
    public (double Y0, double Y1)? HeaderRow { get; init; }

    /// <summary>The field whose column starts a new row (docs/11 §8.6 <c>rowAnchor</c>).</summary>
    public required string RowAnchor { get; init; }

    /// <summary>The pages to read, in order; just <see cref="Page"/> when null.</summary>
    public IReadOnlyList<int>? Pages { get; init; }

    public RepeatHeader RepeatHeader { get; init; } = RepeatHeader.Auto;

    /// <summary>How many column headers must be found on one line to take it for the header row; 0 for min(3, headers).</summary>
    public int MinAnchors { get; init; }

    public Continuation Continuation { get; init; } = Continuation.AppendToPrevious;

    public GroupHeaderSpec? GroupHeader { get; init; }

    public TierColumnsSpec? TierColumns { get; init; }

    /// <summary>Lines whose text matches one of these are dropped (carry-over and page-number lines).</summary>
    public IReadOnlyList<Regex> SkipRows { get; init; } = [];

    /// <summary>A line whose text matches one of these ends the table (<c>Ende der Preisliste</c>).</summary>
    public IReadOnlyList<Regex> StopAt { get; init; } = [];

    public PageMargins? PageMargins { get; init; }

    public IReadOnlyList<PageOverride> PageOverrides { get; init; } = [];

    /// <summary>How printed numbers and dates are read.</summary>
    public ValueLocale Locale { get; init; } = ValueLocale.German;

    /// <summary>The most rejected lines a page report lists with their text (the count is always complete).</summary>
    public int MaxRejectedListed { get; init; } = 50;
}
