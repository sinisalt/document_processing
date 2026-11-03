using System.Text.Json;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Documents;

/// <summary>A word of a page in page units (0 to 1000), with its index in the text layer's word list of that page (null for a word OCR just read).</summary>
public sealed record PageWordDto(int? Index, string Text, SourceBox BBox, double? Confidence = null);

/// <summary>A reading of the selected text as a field kind (docs/11 §12.3: "28.10.2026 → 2026-10-28"), in the shape a correction takes.</summary>
public sealed record ValueCandidateDto(string Kind, JsonElement Value);

/// <summary>
/// <c>GET /documents/{id}/pages/{n}/words?bbox=</c> and <c>POST /documents/{id}/regions:read</c> (docs/11 §12.3): the
/// page's words in page units. <c>source</c> says where they come from: <c>pdfText</c> (the PDF's own text layer),
/// <c>ocr</c> (an OCR text layer, or the region OCR just ran) or <c>none</c> (the page has no text layer: draw a box
/// and read it with <c>regions:read</c>). With a box, <c>words</c> are the words whose centre lies inside it, <c>text</c>
/// is them joined in reading order, <c>bBox</c> their union and <c>candidates</c> the readings of <c>text</c> per kind
/// in <c>locale</c>; without one, every word of the page and no candidates. <c>provider</c> names the OCR that read a
/// region.
/// </summary>
public sealed record PageWordsResponse(
    int Page,
    string Source,
    string Locale,
    IReadOnlyList<PageWordDto> Words,
    string? Text,
    SourceBox? BBox,
    IReadOnlyList<ValueCandidateDto> Candidates,
    string? Provider = null);

/// <summary>Where <see cref="PageWordsResponse.Source"/>'s words come from.</summary>
public static class WordSources
{
    public const string PdfText = "pdfText";
    public const string Ocr = "ocr";
    public const string None = "none";
}

/// <summary>
/// <c>POST /documents/{id}/regions:read</c> (docs/11 §12.3): the box (page units) on page <c>page</c> to read with OCR,
/// for a scan whose text layer has no words there; <c>locale</c> reads the candidates (the tenant's country by default).
/// </summary>
public sealed record ReadRegionRequest(int Page, SourceBox BBox, string? Locale = null);

/// <summary>A column of a table mapping in page units of the mapping page: its dividers, the row field it fills (none to leave it out), its printed header, and a tier column's minimum quantity.</summary>
public sealed record TableColumnDto(double X0, double X1, string? Field = null, string? Header = null, decimal? TierMinQuantity = null);

/// <summary>A vertical band in page units (the header row).</summary>
public sealed record TableBandDto(double Y0, double Y1);

/// <summary>The dividers of one column on one page.</summary>
public sealed record TableColumnBoundsDto(double X0, double X1);

/// <summary>Group headings (docs/11 §8.6): <c>target</c> is the row field a heading fills; <c>when</c> the column whose emptiness makes a line a heading (the first amount column by default).</summary>
public sealed record TableGroupHeaderDto(string Target, string? When = null);

/// <summary>
/// Tier price columns (docs/11 §8.6): a column without a field whose header matches <c>headerPattern</c> (group 1 the
/// minimum quantity; <c>ab\s+(\d+)</c> by default) or that names <c>tierMinQuantity</c> becomes entries of the row's
/// <c>target</c> list (<c>tiers</c>) with <c>quantityField</c> (<c>minQuantity</c>) and <c>priceField</c> (<c>price</c>).
/// </summary>
public sealed record TableTierColumnsDto(string? HeaderPattern = null, string? Target = null, string? QuantityField = null, string? PriceField = null);

/// <summary>The part of every page the table uses (docs/11 §8.6 <c>pageMargins</c>), in page units: running headers above <c>top</c> and footers below <c>bottom</c> are left out.</summary>
public sealed record TableMarginsDto(double? Top = null, double? Bottom = null);

/// <summary>Dividers set by hand for one page (<c>pageOverrides</c>), one per column of the mapping.</summary>
public sealed record TablePageOverrideDto(int Page, IReadOnlyList<TableColumnBoundsDto> Columns);

/// <summary>
/// <c>POST /documents/{id}/regions:table</c> (docs/11 §12.4). Without <c>columns</c>: the dividers proposed from the box
/// drawn on <c>page</c> (word gaps and header words, each column named from the field labels its header matches), with
/// the rows they read on that page. With <c>columns</c>: the rows the mapping reads on <c>pages</c> (<c>1-*</c>,
/// <c>2-5,7</c>; just <c>page</c> by default), and the table report. <c>path</c> is the repeating group (the type's
/// line items by default), <c>rowAnchor</c> the field that starts a row (the group's anchor by default),
/// <c>repeatHeader</c> <c>auto</c> (default) or <c>none</c>, <c>continuation</c> <c>appendToPrevious</c> (default) or
/// <c>none</c>, <c>skipRows</c> and <c>stopAt</c> regular expressions over a line's text. <c>write: true</c> writes the
/// rows into the document as one correction (op <c>set</c> on <c>path</c>, the specification and report in its source)
/// and stores the report (docs/11 §6.2 <c>DocumentTableReport</c>).
/// </summary>
public sealed record ReadTableRequest(
    int Page,
    SourceBox BBox,
    IReadOnlyList<TableColumnDto>? Columns = null,
    TableBandDto? HeaderRow = null,
    string? Path = null,
    string? RowAnchor = null,
    string? Pages = null,
    string? RepeatHeader = null,
    int? MinAnchors = null,
    string? Continuation = null,
    TableGroupHeaderDto? GroupHeader = null,
    TableTierColumnsDto? TierColumns = null,
    IReadOnlyList<string>? SkipRows = null,
    IReadOnlyList<string>? StopAt = null,
    TableMarginsDto? PageMargins = null,
    IReadOnlyList<TablePageOverrideDto>? PageOverrides = null,
    string? Locale = null,
    bool Write = false,
    string? Reason = null);

/// <summary>A value of a row: field, printed text, the value read (shape of a correction), its page and box.</summary>
public sealed record TableCellDto(string Field, string Text, JsonElement? Value, int Page, SourceBox BBox);

/// <summary>A tier price of a row: the minimum quantity and the price cell.</summary>
public sealed record TableTierDto(decimal MinQuantity, TableCellDto Price);

/// <summary>A row the table read: the page it starts on, its box there, its values and tiers.</summary>
public sealed record TableRowDto(int Page, SourceBox BBox, IReadOnlyList<TableCellDto> Cells, IReadOnlyList<TableTierDto> Tiers);

/// <summary>A line that did not become a row: its text, box, a reason code (<c>unreadable</c>, <c>noAnchor</c>, <c>continuationConflict</c>), the message and the field.</summary>
public sealed record TableRejectedLineDto(int Page, string Text, SourceBox BBox, string Reason, string Message, string? Field);

/// <summary>
/// One page of the table report (docs/11 §12.4, the page strip): rows that start on it, rejected lines (count, and the
/// first 50 with their text), skipped lines, headings and continuation lines, whether its header row was found, the
/// dividers used and where they came from (<c>mapping</c>, <c>header</c>, <c>previousHeader</c>, <c>override</c>), and
/// its first three rows as printed.
/// </summary>
public sealed record TablePageReportDto(
    int Page,
    bool HeaderFound,
    int Rows,
    int Rejected,
    IReadOnlyList<TableRejectedLineDto> RejectedLines,
    int SkippedLines,
    int GroupHeadings,
    int ContinuationLines,
    IReadOnlyList<TableColumnBoundsDto> Columns,
    string ColumnsFrom,
    IReadOnlyList<IReadOnlyDictionary<string, string>> FirstRows);

/// <summary>The table report (docs/11 §8.6, §12.4): totals, pages read, pages without rows, the page a stop line ended it on, and every page.</summary>
public sealed record TableReportDto(int Rows, int RejectedRows, IReadOnlyList<int> Pages, IReadOnlyList<int> PagesWithoutRows, int? StoppedOnPage, IReadOnlyList<TablePageReportDto> PerPage);

/// <summary>
/// What <c>regions:table</c> answers: the repeating group, whether the columns were proposed, the columns and header row
/// used (to draw the dividers), the row anchor, the report, the rows (the first 1,000; <c>rowsTruncated</c> when there
/// were more), and with <c>write</c> the document as stored after the write.
/// </summary>
public sealed record ReadTableResponse(
    string Path,
    bool Proposed,
    IReadOnlyList<TableColumnDto> Columns,
    TableBandDto? HeaderRow,
    string? RowAnchor,
    TableReportDto Report,
    IReadOnlyList<TableRowDto> Rows,
    bool RowsTruncated,
    bool Written,
    DocumentDetail? Document = null);

/// <summary>
/// A stored table report (docs/11 §6.2 <c>DocumentTableReport</c>): the group, who wrote it, the Correction that wrote
/// the rows, the document version it describes, when, and the report.
/// </summary>
public sealed record DocumentTableReportDto(string Path, TableReportOrigin Origin, long? CorrectionId, int ProducedAtVersion, DateTimeOffset CreatedAt, TableReportDto Report);
