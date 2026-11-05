using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;

namespace DocProc.Domain.Validation.Rules;

/// <summary>
/// The generic rule kinds of P1 (docs/11 §11): <c>required</c>, <c>pattern</c>, <c>range</c>, <c>enumMember</c>,
/// <c>dateOrder</c>, <c>datePlausibility</c>, <c>lookupExists</c>, <c>duplicate</c> (with <c>onMatch</c>),
/// <c>confidence</c> and <c>customerMatch</c>. They read the values view (<see cref="DocumentView.Values"/>) by the
/// paths their parameters name, the provenance of the canonical tree (<see cref="DocumentView.Root"/>) and the data
/// the stage looked up for them (<see cref="ValidationContext.Data"/>). A view without values (no definition) raises
/// nothing. Each flag carries its field's concrete path; the validator adds the rule id and applies the rule's
/// severity and message. One rule raises at most <see cref="FlagCap.MaxFlagsPerCheck"/> flags per document, then one
/// summary for the rest (<see cref="FlagCap"/>); a <c>pattern</c> rule stops matching there, and every match draws on
/// the validation's <see cref="ValidationContext.PatternBudget"/>.
/// </summary>
public static partial class GenericRules
{
    public const string Required = "required";
    public const string Pattern = "pattern";
    public const string Range = "range";
    public const string EnumMember = "enumMember";
    public const string DateOrder = "dateOrder";
    public const string DatePlausibility = "datePlausibility";
    public const string LookupExists = "lookupExists";
    public const string Duplicate = "duplicate";
    public const string Confidence = "confidence";
    public const string CustomerMatch = "customerMatch";

    /// <summary>One evaluator per generic kind of P1 and P5 (<c>requiredIf</c>, <c>compare</c>, <c>sumEquals</c>, <c>arithmetic</c>, <c>expression</c>).</summary>
    public static IReadOnlyList<IDocumentRule> All { get; } =
    [
        new Rule(Required, EvaluateRequired),
        new Rule(Pattern, EvaluatePattern, capped: false),
        new Rule(Range, EvaluateRange),
        new Rule(EnumMember, EvaluateEnumMember),
        new Rule(DateOrder, EvaluateDateOrder),
        new Rule(DatePlausibility, EvaluateDatePlausibility),
        new Rule(LookupExists, EvaluateLookupExists),
        new Rule(Duplicate, EvaluateDuplicate),
        new Rule(Confidence, EvaluateConfidence),
        new Rule(CustomerMatch, EvaluateCustomerMatch),
        new Rule(RequiredIf, EvaluateRequiredIf),
        new Rule(Compare, EvaluateCompare),
        new Rule(SumEquals, EvaluateSumEquals),
        new Rule(Arithmetic, EvaluateArithmetic),
        new Rule(Expression, EvaluateExpression),
        new Rule(TableComplete, EvaluateTableComplete),
        new Rule(Unique, EvaluateUnique),
        new Rule(Ordered, EvaluateOrdered),
        new Rule(DeltaToPrevious, EvaluateDeltaToPrevious),
    ];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The compiled regex of each pattern rule's parameters, built once per parameters instance. The rule references of
    /// a definition are built once per definition object (<c>TypeDefinitionExtensions.ToRuleReferences</c>), and a
    /// published version's definition object lives in the process-wide cache, so a pattern is compiled once per
    /// published version instead of on every evaluation; the table holds nothing alive past its definition.
    /// </summary>
    private static readonly ConditionalWeakTable<RuleParams, CompiledPattern> Patterns = new();

    private sealed record CompiledPattern(string Pattern, Regex? Regex);

    /// <summary>
    /// The regex of a <c>pattern</c> rule's <c>pattern</c> parameter (NonBacktracking, with a match timeout), compiled
    /// once per <paramref name="parameters"/> instance; null when the parameter is missing or the pattern does not
    /// compile (a syntax error, or a construct NonBacktracking cannot run).
    /// </summary>
    public static Regex? PatternRegexOf(RuleParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (String(parameters, "pattern") is not { } pattern)
        {
            return null;
        }

        var compiled = Patterns.GetValue(parameters, p => CompilePattern(String(p, "pattern")!));

        // RuleParams wraps a read-only view of a dictionary; should the dictionary behind it change, compile again.
        return compiled.Pattern == pattern ? compiled.Regex : CompilePattern(pattern).Regex;
    }

    private static CompiledPattern CompilePattern(string pattern)
    {
        try
        {
            return new CompiledPattern(pattern, new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout));
        }
        catch (ArgumentException)
        {
            return new CompiledPattern(pattern, null);
        }
        catch (NotSupportedException)
        {
            // A construct NonBacktracking cannot run (backreference, lookaround, atomic group). The publish checks refuse
            // it; a version published before they did must not crash validation, so it fails the rule instead.
            return new CompiledPattern(pattern, null);
        }
    }

    /// <summary>
    /// A generic kind. Its flags are capped (<see cref="FlagCap.Apply"/>) unless the kind caps them itself
    /// (<paramref name="capped"/> false: <c>pattern</c>, which stops matching at the cap).
    /// </summary>
    private sealed class Rule(string kind, Func<DocumentView, RuleParams, ValidationContext, IEnumerable<DocumentFlag>> evaluate, bool capped = true) : IDocumentRule
    {
        public string Kind { get; } = kind;

        public IEnumerable<DocumentFlag> Evaluate(DocumentView view, RuleParams parameters, ValidationContext context)
        {
            ArgumentNullException.ThrowIfNull(view);
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(context);
            if (view.Values is null)
            {
                return [];
            }

            List<DocumentFlag> flags;
            try
            {
                flags = evaluate(view, parameters, context).ToList();
            }
            catch (Exception e) when (e is ArithmeticException or ArgumentException or InvalidOperationException or InvalidCastException or FormatException or IndexOutOfRangeException or KeyNotFoundException or NotSupportedException)
            {
                // A rule is configuration and its values are the document's: neither may crash the validation of the
                // whole document (the stage would retry and fail, an inline revalidation answer 500). What the rule
                // could not evaluate fails the rule instead, as an expression that cannot be evaluated does.
                return [FlagCodes.Create(FlagCodes.RuleFailed, $"The rule could not be evaluated: {e.Message}", null, FlagParams.Of((FlagParams.Reason, "expressionError"), ("error", e.GetType().Name)))];
            }

            if (!capped)
            {
                return flags;
            }

            // A CHECKS_SKIPPED (a rule's expressions ran out of steps) is never folded into the cap's summary: it is
            // what keeps the document out of auto-approval.
            var skipped = flags.Where(f => f.Code == FlagCodes.ChecksSkipped).ToList();
            return skipped.Count == 0 ? FlagCap.Apply(flags) : [.. FlagCap.Apply(flags.Where(f => f.Code != FlagCodes.ChecksSkipped)), .. skipped];
        }
    }

    // ---------------------------------------------------------------- required

    private static IEnumerable<DocumentFlag> EvaluateRequired(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        if (path is null)
        {
            yield break;
        }

        foreach (var (at, value) in ValuePaths.Select(view.Values, path))
        {
            if (ValuePaths.IsEmpty(value))
            {
                yield return FlagCodes.Create(FlagCodes.FieldRequired, $"Required field '{at}' has no value.", at, FlagParams.Of(("field", at)));
            }
        }
    }

    // ---------------------------------------------------------------- pattern

    private static IEnumerable<DocumentFlag> EvaluatePattern(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        var pattern = String(p, "pattern");
        if (path is null || pattern is null)
        {
            yield break;
        }

        var regex = PatternRegexOf(p);
        if (regex is null)
        {
            yield return RuleFailed(path, $"The pattern of the rule on '{path}' is not a valid regular expression.", "invalidPattern", ("field", path), ("pattern", pattern));
            yield break;
        }

        // At most FlagCap.MaxFlagsPerCheck failures are flagged; then matching stops and one summary counts the values
        // left unchecked. Each match draws on the validation's budget; a value it has no room for is skipped (the
        // budget's CHECKS_SKIPPED says so), not failed.
        var failures = 0;
        string? stoppedAt = null;
        var notChecked = 0;
        foreach (var (at, value) in ValuePaths.Select(view.Values, path))
        {
            if (ValuePaths.Text(value) is not { } text)
            {
                continue;
            }

            if (failures == FlagCap.MaxFlagsPerCheck)
            {
                stoppedAt ??= at;
                notChecked++;
                continue;
            }

            if (context.PatternBudget.IsMatch(regex, text) is false)
            {
                failures++;
                yield return RuleFailed(at, $"'{at}' ({text}) does not match the expected format.", "pattern", ("field", at), ("value", ValuePaths.Describe(value)), ("pattern", pattern));
            }
        }

        if (stoppedAt is not null)
        {
            yield return FlagCap.Summary(RuleFailed(stoppedAt, string.Empty, "pattern", ("pattern", pattern)), FlagCap.NotCheckedReason, notChecked, "pattern");
        }
    }

    // ---------------------------------------------------------------- range

    private static IEnumerable<DocumentFlag> EvaluateRange(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        var min = Decimal(p, "min");
        var max = Decimal(p, "max");
        if (path is null || (min is null && max is null))
        {
            yield break;
        }

        foreach (var (at, value) in ValuePaths.Select(view.Values, path))
        {
            if (ValuePaths.Number(value) is not { } number)
            {
                continue;
            }

            if ((min is { } low && number < low) || (max is { } high && number > high))
            {
                var bounds = (min, max) switch
                {
                    ({ } a, { } b) => $"between {FlagParams.Number(a)} and {FlagParams.Number(b)}",
                    ({ } a, null) => $"at least {FlagParams.Number(a)}",
                    _ => $"at most {FlagParams.Number(max!.Value)}",
                };
                yield return RuleFailed(at, $"'{at}' is {FlagParams.Number(number)}; it must be {bounds}.", "range", ("field", at), ("value", FlagParams.Number(number)), ("min", min is { } m ? FlagParams.Number(m) : null), ("max", max is { } x ? FlagParams.Number(x) : null));
            }
        }
    }

    // ---------------------------------------------------------------- enumMember

    private static IEnumerable<DocumentFlag> EvaluateEnumMember(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        if (path is null)
        {
            yield break;
        }

        var listed = Strings(p, "values");
        CodeList? codes = null;
        if (String(p, "codeList") is { } table)
        {
            if (!context.Data.CodeLists.TryGetValue(table, out codes))
            {
                yield return RuleFailed(path, $"The code list '{table}' is not available.", "tableUnknown", ("field", path), ("table", table));
                yield break;
            }
        }

        if (listed is null && codes is null)
        {
            yield break;
        }

        foreach (var (at, value) in ValuePaths.Select(view.Values, path))
        {
            if (ValuePaths.Text(value) is not { } code || string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            var ok = (listed?.Contains(code, StringComparer.Ordinal) ?? false) || (codes?.Contains(code) ?? false);
            if (!ok)
            {
                yield return RuleFailed(at, $"'{at}' is '{code}', which is not one of the allowed codes.", "enumMember", ("field", at), ("value", code));
            }
        }
    }

    // ---------------------------------------------------------------- dateOrder

    private static IEnumerable<DocumentFlag> EvaluateDateOrder(DocumentView view, RuleParams p, ValidationContext context)
    {
        var before = String(p, "before");
        var after = String(p, "after");
        if (before is null || after is null)
        {
            yield break;
        }

        var allowEqual = !p.TryGet("allowEqual", out var equal) || equal.ValueKind != JsonValueKind.False;
        var firsts = ValuePaths.Select(view.Values, before).ToList();
        var seconds = ValuePaths.Select(view.Values, after).ToList();
        var pairs = firsts.Count == seconds.Count
            ? firsts.Zip(seconds)
            : firsts.Count == 1 ? seconds.Select(s => (firsts[0], s)) : [];
        foreach (var (first, second) in pairs)
        {
            if (ValuePaths.Date(first.Value) is not { } from || ValuePaths.Date(second.Value) is not { } to)
            {
                continue;
            }

            if (to < from || (!allowEqual && to == from))
            {
                yield return RuleFailed(
                    second.Path,
                    $"'{second.Path}' ({FlagParams.Date(to)}) is {(to == from ? "the same day as" : "before")} '{first.Path}' ({FlagParams.Date(from)}).",
                    "dateOrder",
                    ("field", second.Path),
                    ("before", first.Path),
                    ("beforeDate", FlagParams.Date(from)),
                    ("afterDate", FlagParams.Date(to)));
            }
        }
    }

    // ---------------------------------------------------------------- datePlausibility

    private static IEnumerable<DocumentFlag> EvaluateDatePlausibility(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        if (path is null)
        {
            yield break;
        }

        var maxPast = Integer(p, "maxPastDays") ?? context.Options.MaxDocumentAgeDays;
        var maxFuture = Integer(p, "maxFutureDays") ?? context.Options.MaxFutureDays;
        var reference = context.Today;
        if (String(p, "relativeTo") == "documentDate")
        {
            var rolePath = view.RolePath("documentDate");
            if (rolePath is null || ValuePaths.Select(view.Values, rolePath).Select(v => ValuePaths.Date(v.Value)).FirstOrDefault() is not { } documentDate)
            {
                yield break;
            }

            reference = documentDate;
        }

        foreach (var (at, value) in ValuePaths.Select(view.Values, path))
        {
            if (ValuePaths.Date(value) is not { } date)
            {
                continue;
            }

            if (date > reference.AddDays(maxFuture))
            {
                yield return FlagCodes.Create(FlagCodes.DateImplausible, $"'{at}' ({FlagParams.Date(date)}) is more than {maxFuture} days in the future.", at, FlagParams.Of((FlagParams.Reason, "pathFuture"), ("field", at), ("date", FlagParams.Date(date)), ("maxFutureDays", FlagParams.Number(maxFuture))));
            }
            else if (date < reference.AddDays(-maxPast))
            {
                yield return FlagCodes.Create(FlagCodes.DateImplausible, $"'{at}' ({FlagParams.Date(date)}) is more than {maxPast} days in the past.", at, FlagParams.Of((FlagParams.Reason, "pathTooOld"), ("field", at), ("date", FlagParams.Date(date)), ("maxAgeDays", FlagParams.Number(maxPast))));
            }
        }
    }

    // ---------------------------------------------------------------- lookupExists

    private static IEnumerable<DocumentFlag> EvaluateLookupExists(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        var table = String(p, "table");
        if (path is null || table is null)
        {
            yield break;
        }

        if (!context.Data.CodeLists.TryGetValue(table, out var codes))
        {
            yield return RuleFailed(path, $"The code list '{table}' is not available.", "tableUnknown", ("field", path), ("table", table));
            yield break;
        }

        foreach (var (at, value) in ValuePaths.Select(view.Values, path))
        {
            if (ValuePaths.Text(value) is not { } code || string.IsNullOrWhiteSpace(code) || codes.Contains(code))
            {
                continue;
            }

            yield return RuleFailed(at, $"'{at}' is '{code}', which is not in the code list '{table}'.", "lookup", ("field", at), ("value", code), ("table", table));
        }
    }

    // ---------------------------------------------------------------- duplicate

    private static IEnumerable<DocumentFlag> EvaluateDuplicate(DocumentView view, RuleParams p, ValidationContext context)
    {
        if (context.Data.Duplicate is not { WasSkipped: false, Matches.Count: > 0 } check)
        {
            yield break;
        }

        var field = view.RolePath("documentNumber");
        var match = check.Matches[0];
        if (String(p, "onMatch") == "revision")
        {
            yield return FlagCodes.Create(
                FlagCodes.RevisionOf,
                $"An earlier document ({match.Status}) has the same {DescribeKey(view)}; this one may replace it.",
                field,
                FlagParams.Of(("documentId", match.DocumentId.ToString()), ("status", match.Status), ("number", match.Number)));
            yield break;
        }

        yield return FlagCodes.Create(
            FlagCodes.DuplicateSuspected,
            match.Number is { } number ? $"Another document of this supplier carries the number {number}." : $"Another document of this supplier has the same {DescribeKey(view)}.",
            field,
            FlagParams.Of(("number", match.Number), ("documentId", match.DocumentId.ToString())));
    }

    private static string DescribeKey(DocumentView view) => view.RolePaths.TryGetValue("duplicateKey", out var key) ? key : "duplicate key";

    // ---------------------------------------------------------------- confidence

    private static IEnumerable<DocumentFlag> EvaluateConfidence(DocumentView view, RuleParams p, ValidationContext context)
    {
        var threshold = Double(p, "threshold") ?? context.Options.ConfidenceThreshold;
        var paths = Strings(p, "paths");
        foreach (var (at, confidence) in ProvenanceWalker.Values(view.Root))
        {
            if (confidence >= threshold || (paths is not null && !paths.Any(prefix => ValuePaths.IsUnder(at, prefix))))
            {
                continue;
            }

            yield return FlagCodes.Create(
                FlagCodes.LowConfidence,
                $"'{at}' has confidence {confidence.ToString("0.00", CultureInfo.InvariantCulture)}, below the threshold {threshold.ToString("0.00", CultureInfo.InvariantCulture)}.",
                at,
                FlagParams.Of(("field", at), ("confidence", FlagParams.Ratio(confidence)), ("threshold", FlagParams.Ratio(threshold))));
        }
    }

    // ---------------------------------------------------------------- customerMatch

    private static IEnumerable<DocumentFlag> EvaluateCustomerMatch(DocumentView view, RuleParams p, ValidationContext context)
    {
        if (context.Tenant is null || view.RolePath("customerParty") is not { } path)
        {
            return [];
        }

        var value = ValuePaths.Select(view.Values, path).FirstOrDefault().Value;
        if (value is not JsonObject party)
        {
            return [];
        }

        Party? customer;
        try
        {
            customer = party.Deserialize<Party>(CanonicalJson.Options);
        }
        catch (JsonException)
        {
            customer = null;
        }

        return customer is null ? [] : CustomerMatchRule.Check(customer, path, context);
    }

    // ---------------------------------------------------------------- helpers

    private static DocumentFlag RuleFailed(string field, string message, string reason, params (string Key, string? Value)[] parameters) =>
        FlagCodes.Create(FlagCodes.RuleFailed, message, field, FlagParams.Of([(FlagParams.Reason, reason), .. parameters]));

    private static string? String(RuleParams p, string name) =>
        p.TryGet(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? Decimal(RuleParams p, string name) =>
        p.TryGet(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;

    private static double? Double(RuleParams p, string name) =>
        p.TryGet(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static int? Integer(RuleParams p, string name) =>
        p.TryGet(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static List<string>? Strings(RuleParams p, string name) =>
        p.TryGet(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : null;
}

/// <summary>
/// Walks a canonical tree for its <c>FieldValue</c> envelopes (docs/11 §11, the generic replacement of
/// <c>Invoice.EnumerateAllFields</c>): every object whose <c>status</c> names a <see cref="FieldStatus"/> is a field,
/// at the concrete path it was found at; its value is not walked into (a party's members are parts of one value).
/// </summary>
public static class ProvenanceWalker
{
    /// <summary>Every envelope of the tree with its path, status and confidence.</summary>
    public static IEnumerable<(string Path, FieldStatus Status, double? Confidence, JsonNode? Value)> Envelopes(JsonObject? root)
    {
        if (root is null)
        {
            yield break;
        }

        foreach (var entry in Walk(root, string.Empty))
        {
            yield return entry;
        }
    }

    /// <summary>The fields that have a value (not Missing, not Unreadable) and a confidence, with that confidence.</summary>
    public static IEnumerable<(string Path, double Confidence)> Values(JsonObject? root) =>
        Envelopes(root)
            .Where(e => e.Status is not (FieldStatus.Missing or FieldStatus.Unreadable) && e.Value is not null && e.Confidence is not null)
            .Select(e => (e.Path, e.Confidence!.Value));

    private static IEnumerable<(string Path, FieldStatus Status, double? Confidence, JsonNode? Value)> Walk(JsonNode node, string path)
    {
        switch (node)
        {
            case JsonObject obj when IsEnvelope(obj, out var status):
                double? confidence = obj["confidence"] is JsonValue c && c.GetValueKind() == JsonValueKind.Number ? c.GetValue<double>() : null;
                yield return (path, status, confidence, obj["value"]);
                break;

            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    if (child is null || key == "history")
                    {
                        continue;
                    }

                    foreach (var entry in Walk(child, path.Length == 0 ? key : $"{path}.{key}"))
                    {
                        yield return entry;
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } item)
                    {
                        foreach (var entry in Walk(item, $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]"))
                        {
                            yield return entry;
                        }
                    }
                }

                break;

            default:
                break;
        }
    }

    private static bool IsEnvelope(JsonObject obj, out FieldStatus status)
    {
        status = FieldStatus.Missing;
        return obj["status"] is JsonValue value
               && value.GetValueKind() == JsonValueKind.String
               && Enum.TryParse(value.GetValue<string>(), ignoreCase: false, out status)
               && Enum.IsDefined(status);
    }
}
