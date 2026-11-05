using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Domain.Flags;
using DocProc.Domain.Validation.Expressions;

namespace DocProc.Domain.Validation.Rules;

/// <summary>
/// The generic rule kinds of P5 (docs/11 §11): <c>requiredIf</c>, <c>compare</c>, <c>sumEquals</c>, <c>arithmetic</c>
/// and <c>expression</c>. The two that take expressions (<c>requiredIf.when</c>, <c>arithmetic.expression</c>,
/// <c>expression.predicate</c>) evaluate them with <see cref="RuleExpression"/>, bounded per evaluation and per rule
/// (<see cref="ExpressionLimits"/>); a rule whose expressions ran out of steps raises one blocking
/// <c>CHECKS_SKIPPED {reason: expressionBudget}</c>, and one that cannot be evaluated a <c>RULE_FAILED {reason:
/// expressionError}</c>, so neither passes silently.
/// </summary>
public static partial class GenericRules
{
    public const string RequiredIf = "requiredIf";
    public const string Compare = "compare";
    public const string SumEquals = "sumEquals";
    public const string Arithmetic = "arithmetic";
    public const string Expression = "expression";

    /// <summary>The variables an expression of a rule may name: <c>$today</c> (<c>yyyy-MM-dd</c>) and <c>$supplier</c> (<see cref="RuleData.Supplier"/>).</summary>
    public static readonly IReadOnlyList<string> ExpressionVariables = ["today", "supplier"];

    /// <summary>The comparison operators of <c>compare</c>.</summary>
    public static readonly IReadOnlyList<string> CompareOperators = ["eq", "ne", "lt", "le", "gt", "ge"];

    /// <summary>The tolerance of <c>sumEquals</c> and <c>arithmetic</c> when the rule sets none (the money tolerance of the coded rules).</summary>
    public const decimal DefaultTolerance = 0.02m;

    /// <summary>The <c>reason</c> of the <c>CHECKS_SKIPPED</c> a rule raises when its expressions ran out of steps.</summary>
    public const string ExpressionBudgetReason = "expressionBudget";

    private static readonly ConditionalWeakTable<RuleParams, Dictionary<string, (string Text, RuleExpression? Expression, string? Error)>> Expressions = new();

    /// <summary>
    /// The parsed expression of a rule's parameter <paramref name="name"/>, once per <paramref name="parameters"/>
    /// instance (as patterns are, <see cref="PatternRegexOf"/>); null with the reason when it does not parse.
    /// </summary>
    public static RuleExpression? ExpressionOf(RuleParams parameters, string name, out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        error = null;
        if (String(parameters, name) is not { } text)
        {
            return null;
        }

        var cache = Expressions.GetValue(parameters, _ => new Dictionary<string, (string, RuleExpression?, string?)>(StringComparer.Ordinal));
        lock (cache)
        {
            if (!cache.TryGetValue(name, out var entry) || entry.Text != text)
            {
                RuleExpression.TryParse(text, ExpressionVariables, out var parsed, out var problem);
                entry = (text, parsed, problem);
                cache[name] = entry;
            }

            error = entry.Error;
            return entry.Expression;
        }
    }

    /// <summary>The variables bound for an expression of a rule.</summary>
    private static Dictionary<string, JsonNode?> VariablesFor(ValidationContext context) => new(StringComparer.Ordinal)
    {
        ["today"] = JsonValue.Create(context.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        ["supplier"] = context.Data.Supplier?.DeepClone(),
    };

    // ---------------------------------------------------------------- requiredIf

    /// <summary>
    /// <c>requiredIf</c>: the value at <c>path</c> is required whenever <c>when</c> holds. With <c>rows</c> (a path to
    /// rows, for example <c>lines[*]</c>), <c>path</c> is a field of each row and <c>when</c> is evaluated on each row
    /// (<c>$$</c> is the document), so <c>{rows: lines[*], path: unit, when: "quantity &gt; 0"}</c> requires a unit on
    /// every row with a quantity. Without <c>rows</c>, <c>when</c> is evaluated once on the document.
    /// </summary>
    private static List<DocumentFlag> EvaluateRequiredIf(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        if (path is null)
        {
            return [];
        }

        var rows = String(p, "rows");
        var when = ExpressionOf(p, "when", out var error);
        if (when is null)
        {
            return error is null ? [] : [ExpressionFailed(rows is null ? path : $"{rows}.{path}", "when", error)];
        }

        var variables = VariablesFor(context);
        var budget = new ExpressionBudget();
        var scopes = rows is null ? [new PathValue(string.Empty, view.Values)] : ValuePaths.Select(view.Values, rows).ToList();
        var flags = new List<DocumentFlag>();
        foreach (var scope in scopes)
        {
            if (scope.Value is not JsonObject scoped)
            {
                continue;
            }

            var at = scope.Path.Length == 0 ? path : $"{scope.Path}.{path}";
            bool holds;
            try
            {
                holds = when.Test(scoped, view.Values, variables, budget);
            }
            catch (RuleExpressionBudgetException e)
            {
                flags.Add(BudgetSkipped(at, e.Message));
                break;
            }
            catch (RuleExpressionEvaluationException e)
            {
                flags.Add(ExpressionFailed(at, "when", e.Message));
                continue;
            }

            if (!holds)
            {
                continue;
            }

            foreach (var (valuePath, value) in ValuePaths.Select(scoped, path))
            {
                if (ValuePaths.IsEmpty(value))
                {
                    var full = scope.Path.Length == 0 ? valuePath : $"{scope.Path}.{valuePath}";
                    flags.Add(FlagCodes.Create(FlagCodes.FieldRequired, $"'{full}' is required here ({when.Text}) and has no value.", full, FlagParams.Of(("field", full), ("when", when.Text))));
                }
            }
        }

        return flags;
    }

    // ---------------------------------------------------------------- compare

    private static IEnumerable<DocumentFlag> EvaluateCompare(DocumentView view, RuleParams p, ValidationContext context)
    {
        var left = String(p, "left");
        var op = String(p, "op");
        if (left is null || op is null || !CompareOperators.Contains(op, StringComparer.Ordinal))
        {
            yield break;
        }

        var tolerance = Decimal(p, "tolerance") ?? 0m;
        var firsts = ValuePaths.Select(view.Values, left).ToList();
        IEnumerable<(PathValue Left, JsonNode? Right, string? RightPath)> pairs;
        if (String(p, "right") is { } right)
        {
            // A single-valued side (a path without [*]) is compared with every value of the other side; two row paths
            // are paired row by row. Rows whose counts differ cannot be paired: the rule fails instead of passing
            // without comparing anything (publish refuses two row paths of different row groups).
            var seconds = ValuePaths.Select(view.Values, right).ToList();
            var leftRows = left.Contains("[*]", StringComparison.Ordinal);
            var rightRows = right.Contains("[*]", StringComparison.Ordinal);
            if (!rightRows)
            {
                var single = seconds.Count > 0 ? seconds[0] : new PathValue(right, null);
                pairs = firsts.Select(a => (a, single.Value, (string?)single.Path));
            }
            else if (!leftRows)
            {
                var single = firsts.Count > 0 ? firsts[0] : new PathValue(left, null);
                pairs = seconds.Select(b => (single, b.Value, (string?)b.Path));
            }
            else if (firsts.Count == seconds.Count)
            {
                pairs = firsts.Zip(seconds, (a, b) => (a, b.Value, (string?)b.Path));
            }
            else
            {
                yield return RuleFailed(
                    left,
                    $"'{left}' has {firsts.Count.ToString(CultureInfo.InvariantCulture)} values and '{right}' {seconds.Count.ToString(CultureInfo.InvariantCulture)}; they cannot be compared pair by pair.",
                    "compareUnpaired",
                    ("field", left),
                    ("otherField", right),
                    ("count", firsts.Count.ToString(CultureInfo.InvariantCulture)),
                    ("otherCount", seconds.Count.ToString(CultureInfo.InvariantCulture)));
                yield break;
            }
        }
        else if (p.TryGet("value", out var literal))
        {
            var node = JsonNode.Parse(literal.GetRawText());
            pairs = firsts.Select(a => (a, node, (string?)null));
        }
        else
        {
            yield break;
        }

        foreach (var (first, second, secondPath) in pairs)
        {
            if (CompareValues(first.Value, second, tolerance) is not { } order)
            {
                continue; // An absent or incomparable value is not compared; required and the schema check report it.
            }

            var holds = op switch
            {
                "eq" => order == 0,
                "ne" => order != 0,
                "lt" => order < 0,
                "le" => order <= 0,
                "gt" => order > 0,
                _ => order >= 0,
            };
            if (!holds)
            {
                var other = secondPath is null ? ValuePaths.Describe(second) : $"'{secondPath}' ({ValuePaths.Describe(second)})";
                yield return RuleFailed(
                    first.Path,
                    $"'{first.Path}' ({ValuePaths.Describe(first.Value)}) must be {OperatorText(op)} {other}.",
                    "compare",
                    ("field", first.Path),
                    ("op", op),
                    ("value", ValuePaths.Describe(first.Value)),
                    ("other", ValuePaths.Describe(second)),
                    ("otherField", secondPath));
            }
        }
    }

    private static string OperatorText(string op) => op switch
    {
        "eq" => "equal to",
        "ne" => "different from",
        "lt" => "less than",
        "le" => "at most",
        "gt" => "greater than",
        _ => "at least",
    };

    /// <summary>
    /// The order of two values: as numbers (a money value is its amount) within <paramref name="tolerance"/>, as
    /// <c>yyyy-MM-dd</c> dates, or as strings (ordinal); null when either is absent or they are of different kinds.
    /// </summary>
    internal static int? CompareValues(JsonNode? left, JsonNode? right, decimal tolerance)
    {
        if (ValuePaths.IsEmpty(left) || ValuePaths.IsEmpty(right))
        {
            return null;
        }

        if (ValuePaths.Date(left) is { } a && ValuePaths.Date(right) is { } b)
        {
            return a.CompareTo(b);
        }

        if (ValuePaths.Number(left) is { } x && ValuePaths.Number(right) is { } y)
        {
            return Math.Abs(x - y) <= tolerance ? 0 : x.CompareTo(y);
        }

        if (ValuePaths.Text(left) is { } s && ValuePaths.Text(right) is { } t)
        {
            return Math.Sign(string.CompareOrdinal(s, t));
        }

        if (left is JsonValue l && right is JsonValue r && l.GetValueKind() is JsonValueKind.True or JsonValueKind.False && r.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return l.GetValue<bool>() == r.GetValue<bool>() ? 0 : 1;
        }

        return null;
    }

    // ---------------------------------------------------------------- sumEquals

    private static IEnumerable<DocumentFlag> EvaluateSumEquals(DocumentView view, RuleParams p, ValidationContext context)
    {
        var path = String(p, "path");
        var equals = String(p, "equals");
        if (path is null || equals is null)
        {
            yield break;
        }

        var target = ValuePaths.Select(view.Values, equals).FirstOrDefault();
        if (ValuePaths.Number(target.Value) is not { } expected)
        {
            yield break; // No total to compare with; the required check reports a missing one.
        }

        var items = ValuePaths.Select(view.Values, path).Select(v => ValuePaths.Number(v.Value)).ToList();
        var sum = items.OfType<decimal>().Sum();
        var tolerance = (Decimal(p, "tolerance") ?? DefaultTolerance) + ((Decimal(p, "perItemTolerance") ?? 0m) * items.Count);
        if (Math.Abs(sum - expected) > tolerance)
        {
            yield return RuleFailed(
                target.Path,
                $"The values at '{path}' add up to {FlagParams.Amount(sum)}, but '{target.Path}' is {FlagParams.Amount(expected)}.",
                "sumEquals",
                ("field", target.Path),
                ("sum", FlagParams.Amount(sum)),
                ("expected", FlagParams.Amount(expected)),
                ("count", FlagParams.Number(items.Count)),
                ("items", path));
        }
    }

    // ---------------------------------------------------------------- arithmetic

    private static List<DocumentFlag> EvaluateArithmetic(DocumentView view, RuleParams p, ValidationContext context)
    {
        var equals = String(p, "equals");
        if (equals is null)
        {
            return [];
        }

        var rows = String(p, "rows");
        var expression = ExpressionOf(p, "expression", out var error);
        if (expression is null)
        {
            return error is null ? [] : [ExpressionFailed(rows ?? equals, "expression", error)];
        }

        var tolerance = Decimal(p, "tolerance") ?? DefaultTolerance;
        var variables = VariablesFor(context);
        var budget = new ExpressionBudget();
        var scopes = rows is null ? [new PathValue(string.Empty, view.Values)] : ValuePaths.Select(view.Values, rows).ToList();
        var flags = new List<DocumentFlag>();
        foreach (var scope in scopes)
        {
            if (scope.Value is not JsonObject row)
            {
                continue;
            }

            var target = ValuePaths.Select(row, equals).FirstOrDefault();
            var at = scope.Path.Length == 0 ? target.Path : $"{scope.Path}.{target.Path}";
            if (ValuePaths.Number(target.Value) is not { } expected)
            {
                continue;
            }

            decimal? computed;
            try
            {
                computed = expression.EvaluateNumber(row, view.Values, variables, budget);
            }
            catch (RuleExpressionBudgetException e)
            {
                flags.Add(BudgetSkipped(at, e.Message));
                break;
            }
            catch (RuleExpressionEvaluationException e)
            {
                flags.Add(ExpressionFailed(at, "expression", e.Message));
                continue;
            }

            if (computed is { } value && Math.Abs(value - expected) > tolerance)
            {
                flags.Add(RuleFailed(
                    at,
                    $"'{at}' is {FlagParams.Amount(expected)}, but {expression.Text} gives {FlagParams.Amount(value)}.",
                    "arithmetic",
                    ("field", at),
                    ("expected", FlagParams.Amount(value)),
                    ("actual", FlagParams.Amount(expected)),
                    ("expression", expression.Text)));
            }
        }

        return flags;
    }

    // ---------------------------------------------------------------- expression

    private static List<DocumentFlag> EvaluateExpression(DocumentView view, RuleParams p, ValidationContext context)
    {
        var forEach = String(p, "forEach");
        var field = String(p, "field");
        var predicate = ExpressionOf(p, "predicate", out var error);
        if (predicate is null)
        {
            return error is null ? [] : [ExpressionFailed(field ?? forEach ?? string.Empty, "predicate", error)];
        }

        var variables = VariablesFor(context);
        var budget = new ExpressionBudget();
        var scopes = forEach is null ? [new PathValue(string.Empty, view.Values)] : ValuePaths.Select(view.Values, forEach).ToList();
        var flags = new List<DocumentFlag>();
        foreach (var scope in scopes)
        {
            var at = (scope.Path, field) switch
            {
                ({ Length: 0 }, null) => string.Empty,
                ({ Length: 0 }, { } f) => f,
                (var row, null) => row,
                (var row, { } f) => $"{row}.{f}",
            };
            bool holds;
            try
            {
                holds = predicate.Test(scope.Value, view.Values, variables, budget);
            }
            catch (RuleExpressionBudgetException e)
            {
                flags.Add(BudgetSkipped(at, e.Message));
                break;
            }
            catch (RuleExpressionEvaluationException e)
            {
                flags.Add(ExpressionFailed(at, "predicate", e.Message));
                continue;
            }

            if (!holds)
            {
                flags.Add(RuleFailed(
                    at,
                    at.Length == 0 ? $"The document does not satisfy {predicate.Text}." : $"'{at}' does not satisfy {predicate.Text}.",
                    "expression",
                    ("field", at.Length == 0 ? null : at),
                    ("expression", predicate.Text)));
            }
        }

        return flags;
    }

    private static DocumentFlag ExpressionFailed(string field, string parameter, string error) =>
        RuleFailed(field, $"The rule's {parameter} could not be evaluated: {error}", "expressionError", ("field", field.Length == 0 ? null : field), ("parameter", parameter), ("error", error));

    private static DocumentFlag BudgetSkipped(string field, string message) =>
        FlagCodes.Create(
            FlagCodes.ChecksSkipped,
            $"A rule was not checked to the end: {message}",
            field.Length == 0 ? null : field,
            FlagParams.Of((FlagParams.Reason, ExpressionBudgetReason), ("count", "1")));
}
