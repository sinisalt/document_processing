using System.Collections.Frozen;
using DocProc.Domain.Common;

namespace DocProc.Domain.Validation;

/// <summary>
/// The data the generic rule kinds read besides the document (docs/11 §11). Rules are pure, so whatever needs the
/// database is looked up by the validate stage first and handed in here: the duplicate check's result and the code
/// lists that <c>lookupExists</c> and <c>enumMember</c> name.
/// </summary>
public sealed record RuleData
{
    public static RuleData None { get; } = new();

    /// <summary>The result of the duplicate check of the type's <c>duplicateKey</c>; null when it was not run.</summary>
    public DuplicateCheck? Duplicate { get; init; }

    /// <summary>Code lists by key (<c>uom</c>, ...). <see cref="CodeLists.Platform"/> unless the stage adds the tenant's.</summary>
    public IReadOnlyDictionary<string, CodeList> CodeLists { get; init; } = Validation.CodeLists.Platform;

    /// <summary>
    /// The matched supplier as the expression kinds read it (<c>$supplier</c>: <c>id</c>, <c>name</c>, <c>vatId</c>,
    /// <c>attributes</c>, docs/11 §11); null when the document has no supplier.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject? Supplier { get; init; }

    /// <summary>
    /// The stored table reports of the document by group path (<c>lines</c>), as <c>tableComplete</c> reads them
    /// (docs/11 §6.2 <c>DocumentTableReport</c>); empty when the rows have no report (read by the LLM, typed in).
    /// </summary>
    public IReadOnlyDictionary<string, TableReportSummary> TableReports { get; init; } = new Dictionary<string, TableReportSummary>(StringComparer.Ordinal);

    /// <summary>
    /// The last approved document of the same supplier and type (<c>SupplierHints.LastApproved</c>, docs/11 §11), as its
    /// values view, which <c>deltaToPrevious</c> compares with; null when there is none or the rule is not used.
    /// </summary>
    public PreviousDocument? Previous { get; init; }
}

/// <summary>What <c>tableComplete</c> reads of a table report: rows read, rejected rows per page, the pages and the pages without rows.</summary>
public sealed record TableReportSummary(
    int Rows,
    int RejectedRows,
    IReadOnlyList<int> Pages,
    IReadOnlyList<int> PagesWithoutRows,
    IReadOnlyDictionary<int, int> RejectedByPage);

/// <summary>The previous approved document a <c>deltaToPrevious</c> rule compares with: its id, when it was approved and its values view.</summary>
public sealed record PreviousDocument(Guid DocumentId, DateTimeOffset? ApprovedAt, System.Text.Json.Nodes.JsonObject Values);

/// <summary>An earlier document with the same duplicate key.</summary>
public sealed record DuplicateMatch(Guid DocumentId, string Status, string? Number);

/// <summary>
/// The outcome of the duplicate check (docs/11 §11 "Duplicates"): the matching documents, or why the check was
/// skipped (no supplier, a required key component missing, every component other than the supplier empty).
/// </summary>
public sealed record DuplicateCheck(IReadOnlyList<DuplicateMatch> Matches, string? SkippedBecause = null)
{
    public static DuplicateCheck Skipped(string reason) => new([], reason);

    public bool WasSkipped => SkippedBecause is not null;
}

/// <summary>A list of codes with aliases (a <c>LookupTable</c>, docs/11 §6.1), compared without case unless it is case sensitive.</summary>
public sealed class CodeList
{
    private readonly FrozenDictionary<string, string> _codes;

    public CodeList(string key, IEnumerable<KeyValuePair<string, string>> entries, bool caseSensitive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(entries);
        Key = key;
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var map = new Dictionary<string, string>(comparer);
        foreach (var (from, to) in entries)
        {
            map.TryAdd(from.Trim(), to);
        }

        _codes = map.ToFrozenDictionary(comparer);
    }

    public string Key { get; }

    /// <summary>Every code and alias the list accepts.</summary>
    public IEnumerable<string> Entries => _codes.Keys;

    /// <summary>Whether the value is a code or an alias of the list.</summary>
    public bool Contains(string? value) => value is not null && _codes.ContainsKey(value.Trim());

    /// <summary>The code a value or alias maps to, or null.</summary>
    public string? Resolve(string? value) => value is not null && _codes.TryGetValue(value.Trim(), out var code) ? code : null;
}

/// <summary>
/// The platform code lists (docs/11 §6.1, <c>TenantId</c> NULL): <c>uom</c>, the UN/ECE Recommendation 20 codes of
/// the units <see cref="UnitOfMeasure"/> knows, with the enum names (what version 1 trees hold) and the printed
/// aliases of <see cref="UnitOfMeasureParser"/> mapped to them.
/// </summary>
public static class CodeLists
{
    public const string Uom = "uom";

    /// <summary>The UN/ECE code of each unit of the normalised set.</summary>
    public static readonly FrozenDictionary<UnitOfMeasure, string> UneceCodes = new Dictionary<UnitOfMeasure, string>
    {
        [UnitOfMeasure.Kilogram] = "KGM",
        [UnitOfMeasure.Gram] = "GRM",
        [UnitOfMeasure.Litre] = "LTR",
        [UnitOfMeasure.Millilitre] = "MLT",
        [UnitOfMeasure.Piece] = "H87",
        [UnitOfMeasure.Box] = "BX",
        [UnitOfMeasure.Case] = "CS",
        [UnitOfMeasure.Crate] = "CR",
        [UnitOfMeasure.Pallet] = "PF",
        [UnitOfMeasure.Hour] = "HUR",
    }.ToFrozenDictionary();

    public static IReadOnlyDictionary<string, CodeList> Platform { get; } = new Dictionary<string, CodeList>(StringComparer.Ordinal)
    {
        [Uom] = BuildUom(),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static CodeList BuildUom()
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (var (unit, code) in UneceCodes)
        {
            entries.Add(new(code, code));
            entries.Add(new(unit.ToString(), code));
        }

        // EA (each) and C62 (one) are the other common piece codes in e-invoices.
        entries.Add(new("EA", "H87"));
        entries.Add(new("C62", "H87"));
        foreach (var alias in UnitOfMeasureParser.KnownAliases)
        {
            if (UneceCodes.TryGetValue(UnitOfMeasureParser.Parse(alias), out var code))
            {
                entries.Add(new(alias, code));
            }
        }

        return new CodeList(Uom, entries);
    }
}
