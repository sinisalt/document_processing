using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Domain.Flags;

namespace DocProc.Domain.Validation.Rules;

/// <summary>
/// The line-table rule kinds of P5 (docs/11 §5.8, §11), which stand in for a totals check on types without totals such
/// as price lists: <c>tableComplete</c> (the table report of the rows: no rejected lines, no page without rows, the
/// printed row count), <c>unique</c> (no two rows share a value), <c>ordered</c> (tier breaks ascend, their prices do not
/// rise) and <c>deltaToPrevious</c> (row values against the last approved document of the same supplier and type).
/// </summary>
public static partial class GenericRules
{
    public const string TableComplete = "tableComplete";
    public const string Unique = "unique";
    public const string Ordered = "ordered";
    public const string DeltaToPrevious = "deltaToPrevious";

    /// <summary>The orders <c>ordered.then.order</c> takes.</summary>
    public static readonly IReadOnlyList<string> Orders = ["increasing", "nonDecreasing", "decreasing", "nonIncreasing"];

    /// <summary>
    /// The change below which a row does not count as changed for <c>deltaToPrevious.maxChangedRowsPercent</c> (unless the
    /// rule sets <c>ignoreBelowPercent</c>): docs/11 §5.8 lets "a normal yearly 3 % increase" through and holds a list "with
    /// more than 30 % of the rows changed", which only fit together when small moves are not counted.
    /// </summary>
    public const decimal DefaultIgnoreBelowPercent = 5m;

    /// <summary>The most row indexes a <c>deltaToPrevious</c> summary lists in its <c>rows</c> parameter (the review grid's filter).</summary>
    public const int MaxListedRows = 150;

    /// <summary>
    /// A row path split at its last <c>[*]</c>: <c>lines[*].price.amount</c> is the rows <c>lines[*]</c> and the field
    /// <c>price.amount</c> in each row; null when the path names no rows.
    /// </summary>
    public static (string Rows, string Field)? SplitRowPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var at = path.LastIndexOf("[*].", StringComparison.Ordinal);
        return at < 0 ? null : (path[..(at + 3)], path[(at + 4)..]);
    }

    // ---------------------------------------------------------------- tableComplete

    private static IEnumerable<DocumentFlag> EvaluateTableComplete(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        if (path is null)
        {
            yield break;
        }

        var group = path.EndsWith("[*]", StringComparison.Ordinal) ? path[..^3] : path;
        var maxRejected = Integer(p, "maxRejectedRows") ?? 0;
        var everyPage = Bool(p, "everyPageInRange");
        var allowUnreported = Bool(p, "allowUnreported");
        var rows = ValuePaths.Select(view.Values, group + "[*]").Count();

        if (String(p, "declaredCount") is { } declaredPath
            && ValuePaths.Select(view.Values, declaredPath).Select(v => ValuePaths.Number(v.Value)).FirstOrDefault() is { } declared
            && declared != rows)
        {
            yield return RuleFailed(
                declaredPath,
                $"The list prints {FlagParams.Number(declared)} articles, but {rows.ToString(CultureInfo.InvariantCulture)} rows were read.",
                "tableCount",
                ("field", declaredPath),
                ("declared", FlagParams.Number(declared)),
                ("read", rows.ToString(CultureInfo.InvariantCulture)));
        }

        if (!context.Data.TableReports.TryGetValue(group, out var report))
        {
            if (!allowUnreported)
            {
                yield return RuleFailed(
                    group,
                    $"The rows of '{group}' have no table report: they were not read by a table mapping, so nothing shows that every row of the list was read.",
                    "tableUnreported",
                    ("field", group));
            }

            yield break;
        }

        if (report.RejectedRows > maxRejected)
        {
            foreach (var (page, count) in report.RejectedByPage.Where(e => e.Value > 0).OrderBy(e => e.Key))
            {
                yield return FlagCodes.Create(
                    FlagCodes.TableRowsRejected,
                    $"Page {page.ToString(CultureInfo.InvariantCulture)}: {count.ToString(CultureInfo.InvariantCulture)} line(s) of the table did not become rows.",
                    group,
                    FlagParams.Of(("field", group), ("page", page.ToString(CultureInfo.InvariantCulture)), ("rows", count.ToString(CultureInfo.InvariantCulture)), ("maxRejectedRows", maxRejected.ToString(CultureInfo.InvariantCulture))));
            }

            if (report.RejectedByPage.Count == 0)
            {
                yield return FlagCodes.Create(
                    FlagCodes.TableRowsRejected,
                    $"{report.RejectedRows.ToString(CultureInfo.InvariantCulture)} line(s) of the table did not become rows.",
                    group,
                    FlagParams.Of(("field", group), ("rows", report.RejectedRows.ToString(CultureInfo.InvariantCulture)), ("maxRejectedRows", maxRejected.ToString(CultureInfo.InvariantCulture))));
            }
        }

        foreach (var page in report.PagesWithoutRows.Order())
        {
            var flag = FlagCodes.Create(
                FlagCodes.TablePageNoRows,
                $"Page {page.ToString(CultureInfo.InvariantCulture)} is in the table's page range but yielded no row.",
                group,
                FlagParams.Of(("field", group), ("page", page.ToString(CultureInfo.InvariantCulture))));
            yield return everyPage ? flag with { Severity = FlagSeverity.Blocking } : flag with { Severity = FlagSeverity.Warning };
        }
    }

    // ---------------------------------------------------------------- unique

    private static IEnumerable<DocumentFlag> EvaluateUnique(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        if (path is null || SplitRowPath(path) is not { } split)
        {
            yield break;
        }

        var scopeBy = Strings(p, "scopeBy") ?? [];
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in ValuePaths.Select(view.Values, split.Rows))
        {
            if (row.Value is not JsonObject item)
            {
                continue;
            }

            var value = ValuePaths.Select(item, split.Field).FirstOrDefault().Value;
            if (ValuePaths.IsEmpty(value))
            {
                continue; // An empty value is not a duplicate of another; required reports it.
            }

            var text = KeyText(value);
            var scope = string.Join("\u001f", scopeBy.Select(s => KeyText(ValuePaths.Select(item, s).FirstOrDefault().Value)));
            var key = scope + "\u001e" + text;
            var at = $"{row.Path}.{split.Field}";
            if (seen.TryGetValue(key, out var first))
            {
                yield return RuleFailed(
                    at,
                    $"'{at}' ({ValuePaths.Describe(value)}) is the same as '{first}'; every row must have its own value.",
                    "unique",
                    ("field", at),
                    ("value", ValuePaths.Describe(value)),
                    ("firstField", first));
            }
            else
            {
                seen[key] = at;
            }
        }
    }

    /// <summary>A value as a comparison key: a string trimmed (ordinal), a number invariant, anything else its JSON.</summary>
    private static string KeyText(JsonNode? value) => value switch
    {
        null => string.Empty,
        _ when ValuePaths.Text(value) is { } text => text.Trim(),
        _ when ValuePaths.Number(value) is { } number => FlagParams.Number(number),
        _ => value.ToJsonString(),
    };

    // ---------------------------------------------------------------- ordered

    private static IEnumerable<DocumentFlag> EvaluateOrdered(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        var by = String(p, "by");
        if (path is null || by is null)
        {
            yield break;
        }

        var strict = !p.TryGet("strict", out var s) || s.ValueKind != JsonValueKind.False;
        string? thenPath = null;
        var thenOrder = "nonIncreasing";
        if (p.TryGet("then", out var then) && then.ValueKind == JsonValueKind.Object)
        {
            thenPath = then.TryGetProperty("path", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;
            thenOrder = then.TryGetProperty("order", out var to) && to.ValueKind == JsonValueKind.String && Orders.Contains(to.GetString()!, StringComparer.Ordinal) ? to.GetString()! : thenOrder;
        }

        var lists = path.EndsWith("[*]", StringComparison.Ordinal) ? ValuePaths.Select(view.Values, path[..^3]) : ValuePaths.Select(view.Values, path);
        foreach (var list in lists)
        {
            if (list.Value is not JsonArray items)
            {
                continue;
            }

            (string Path, IComparable Value)? previousBy = null;
            (string Path, decimal Value)? previousThen = null;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is not JsonObject item)
                {
                    continue;
                }

                var itemPath = $"{list.Path}[{i.ToString(CultureInfo.InvariantCulture)}]";
                if (Comparable(ValuePaths.Select(item, by).FirstOrDefault().Value) is { } key)
                {
                    var at = $"{itemPath}.{by}";
                    if (previousBy is { } before)
                    {
                        var order = key.CompareTo(before.Value);
                        if (order < 0 || (strict && order == 0))
                        {
                            yield return RuleFailed(
                                at,
                                $"'{at}' ({Show(key)}) must be {(strict ? "greater than" : "at least")} '{before.Path}' ({Show(before.Value)}).",
                                "ordered",
                                ("field", at),
                                ("value", Show(key)),
                                ("previous", Show(before.Value)),
                                ("previousField", before.Path));
                        }
                    }

                    previousBy = (at, key);
                }

                if (thenPath is not null && ValuePaths.Number(ValuePaths.Select(item, thenPath).FirstOrDefault().Value) is { } amount)
                {
                    var at = $"{itemPath}.{thenPath}";
                    if (previousThen is { } before && !Follows(before.Value, amount, thenOrder))
                    {
                        yield return RuleFailed(
                            at,
                            $"'{at}' ({FlagParams.Number(amount)}) breaks the {thenOrder} order after '{before.Path}' ({FlagParams.Number(before.Value)}).",
                            "orderedThen",
                            ("field", at),
                            ("value", FlagParams.Number(amount)),
                            ("previous", FlagParams.Number(before.Value)),
                            ("previousField", before.Path),
                            ("order", thenOrder));
                    }

                    previousThen = (at, amount);
                }
            }
        }

        static IComparable? Comparable(JsonNode? node) =>
            ValuePaths.Date(node) is { } date ? date : ValuePaths.Number(node) is { } number ? number : null;

        static string Show(IComparable value) => value switch
        {
            DateOnly date => FlagParams.Date(date),
            decimal number => FlagParams.Number(number),
            _ => value.ToString() ?? string.Empty,
        };

        static bool Follows(decimal before, decimal next, string order) => order switch
        {
            "increasing" => next > before,
            "nonDecreasing" => next >= before,
            "decreasing" => next < before,
            _ => next <= before,
        };
    }

    // ---------------------------------------------------------------- deltaToPrevious

    private static IEnumerable<DocumentFlag> EvaluateDeltaToPrevious(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        var joinOn = String(p, "joinOn");
        if (path is null || joinOn is null || SplitRowPath(path) is not { } split)
        {
            yield break;
        }

        if (context.Data.Previous is not { } previous)
        {
            yield return new DocumentFlag(
                FlagCodes.RuleFailed,
                FlagSeverity.Info,
                "There is no earlier approved document of this supplier and type to compare the values with.",
                split.Rows.Length > 3 ? split.Rows[..^3] : split.Rows,
                Params: FlagParams.Of((FlagParams.Reason, "noPrevious")));
            yield break;
        }

        var maxChange = Decimal(p, "maxChangePercent");
        var maxChangedRows = Decimal(p, "maxChangedRowsPercent");
        var ignoreBelow = Decimal(p, "ignoreBelowPercent") ?? DefaultIgnoreBelowPercent;
        var before = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var row in ValuePaths.Select(previous.Values, split.Rows))
        {
            if (row.Value is JsonObject item
                && ValuePaths.Select(item, joinOn).FirstOrDefault().Value is { } key && !ValuePaths.IsEmpty(key)
                && ValuePaths.Number(ValuePaths.Select(item, split.Field).FirstOrDefault().Value) is { } value)
            {
                before.TryAdd(KeyText(key), value);
            }
        }

        var compared = 0;
        var changed = 0;
        var over = new List<(string Path, int Index, string Key, decimal Old, decimal New, decimal Percent)>();
        var index = -1;
        foreach (var row in ValuePaths.Select(view.Values, split.Rows))
        {
            index++;
            if (row.Value is not JsonObject item
                || ValuePaths.Select(item, joinOn).FirstOrDefault().Value is not { } key || ValuePaths.IsEmpty(key)
                || ValuePaths.Number(ValuePaths.Select(item, split.Field).FirstOrDefault().Value) is not { } value
                || !before.TryGetValue(KeyText(key), out var old))
            {
                continue;
            }

            compared++;
            if (value == old)
            {
                continue;
            }

            var percent = old == 0 ? 100m : Math.Round(Math.Abs(value - old) / Math.Abs(old) * 100m, 2);

            // A move below ignoreBelowPercent (a yearly increase of a few per cent) does not count as a changed row for
            // maxChangedRowsPercent, which is about lists that change shape, not about general price rises.
            if (percent >= ignoreBelow)
            {
                changed++;
            }

            if (maxChange is { } limit && percent > limit)
            {
                over.Add(($"{row.Path}.{split.Field}", index, KeyText(key), old, value, percent));
            }
        }

        var share = compared == 0 ? 0m : Math.Round(changed * 100m / compared, 2);
        var tooMany = maxChangedRows is { } rowsLimit && share > rowsLimit;
        var group = split.Rows.Length > 3 ? split.Rows[..^3] : split.Rows;
        if (over.Count > 0 || tooMany)
        {
            // The summary comes first, so the flag cap never folds it away: it lists the rows the review grid filters to.
            var listed = string.Join(",", over.Take(MaxListedRows).Select(o => o.Index.ToString(CultureInfo.InvariantCulture)));
            yield return RuleFailed(
                group,
                tooMany
                    ? $"{changed.ToString(CultureInfo.InvariantCulture)} of {compared.ToString(CultureInfo.InvariantCulture)} rows changed against the last approved list ({Percent(share)} %, more than {FlagParams.Number(maxChangedRows!.Value)} %)."
                    : $"{over.Count.ToString(CultureInfo.InvariantCulture)} row(s) changed by more than {FlagParams.Number(maxChange!.Value)} % against the last approved list.",
                "deltaToPrevious",
                ("field", group),
                ("previousDocumentId", previous.DocumentId.ToString()),
                ("compared", compared.ToString(CultureInfo.InvariantCulture)),
                ("changed", changed.ToString(CultureInfo.InvariantCulture)),
                ("changedPercent", Percent(share)),
                ("overThreshold", over.Count.ToString(CultureInfo.InvariantCulture)),
                ("rows", listed.Length == 0 ? null : listed));
        }

        foreach (var (at, _, key, old, value, percent) in over)
        {
            yield return RuleFailed(
                at,
                $"'{at}' ({key}) changed from {FlagParams.Number(old)} to {FlagParams.Number(value)} ({Percent(percent)} %, more than {FlagParams.Number(maxChange!.Value)} %).",
                "deltaRow",
                ("field", at),
                ("key", key),
                ("previous", FlagParams.Number(old)),
                ("value", FlagParams.Number(value)),
                ("changePercent", Percent(percent)),
                ("previousDocumentId", previous.DocumentId.ToString()));
        }
    }

    /// <summary>A percentage as flag parameters carry it: invariant, at most two decimals, no trailing zeros (<c>40</c>, <c>3.25</c>).</summary>
    private static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool Bool(RuleParams p, string name) => p.TryGet(name, out var value) && value.ValueKind == JsonValueKind.True;
}
