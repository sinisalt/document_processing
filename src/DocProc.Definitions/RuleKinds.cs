using System.Collections.Frozen;
using System.Text.Json.Nodes;
using DocProc.Domain.Validation;

namespace DocProc.Definitions;

/// <summary>
/// A rule kind a definition may name (docs/11 §10.2, §11): its key, the phase that registers it, the JSON Schema of
/// its <c>params</c> and the parameters that name field paths (checked against the definition at publish, §5.7).
/// </summary>
public sealed record RuleKindDescriptor(string Kind, string Phase, string Description, JsonObject ParamsSchema, IReadOnlyList<string> PathParams);

/// <summary>
/// The rule kinds of this deployment (docs/11 §11, "phase in which each generic kind is registered"). Publishing a
/// definition that names a kind not listed here fails with "rule kind X is not available yet" (§5.7): there is no
/// "compile now, run later". The P5 kinds <c>requiredIf</c>, <c>compare</c>, <c>sumEquals</c>, <c>arithmetic</c> and
/// <c>expression</c> are listed; their expression parameters (<see cref="ExpressionParams"/>) are parsed at publish.
/// <para>
/// The coded invoice kinds run through <c>InvoiceRuleAdapter</c> and take no parameters (the adapter has none to
/// pass on). The generic P1 kinds are declared with their parameter schemas so definitions that use them can be
/// checked and published; a kind whose evaluator is not registered in the <c>RuleRegistry</c> yet is reported by the
/// validator as not run, which keeps the document out of auto-approval (<c>ValidationResult.NotRun</c>).
/// </para>
/// </summary>
public static class RuleKinds
{
    public const string P0 = "P0";
    public const string P1 = "P1";
    public const string P5 = "P5";

    /// <summary>The parameters, by kind, that hold a rule expression (<c>RuleExpression</c>), checked to parse at publish.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ExpressionParams = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["requiredIf"] = ["when"],
        ["arithmetic"] = ["expression"],
        ["expression"] = ["predicate"],
    };

    /// <summary>The parameters, by kind, that name a path relative to the rows of another parameter (<c>arithmetic.equals</c> under <c>rows</c>, <c>expression.field</c> under <c>forEach</c>, <c>requiredIf.path</c> under <c>rows</c>).</summary>
    public static readonly IReadOnlyDictionary<string, (string Rows, string Relative)> RowRelativeParams = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["arithmetic"] = ("rows", "equals"),
        ["expression"] = ("forEach", "field"),
        ["requiredIf"] = ("rows", "path"),
    };

    /// <summary>Every kind available in this deployment, by key.</summary>
    public static FrozenDictionary<string, RuleKindDescriptor> Available { get; } = Build().ToFrozenDictionary(d => d.Kind, StringComparer.Ordinal);

    /// <summary>The keys of <see cref="Available"/>, for <see cref="DefinitionChecker.Check(TypeDefinition, IEnumerable{string})"/>.</summary>
    public static IEnumerable<string> Keys => Available.Keys;

    private static IEnumerable<RuleKindDescriptor> Build()
    {
        foreach (var (kind, description) in new[]
        {
            (InvoiceRuleKinds.Required, "The invoice's required fields are present (coded)."),
            (InvoiceRuleKinds.Currency, "The invoice's currency is known or inferred (coded)."),
            (InvoiceRuleKinds.Totals, "Net plus tax equals gross (coded)."),
            (InvoiceRuleKinds.TaxBreakdown, "The VAT summary adds up (coded)."),
            (InvoiceRuleKinds.LineArithmetic, "Quantity times unit price equals the line net (coded)."),
            (InvoiceRuleKinds.DatePlausibility, "The invoice dates are plausible (coded)."),
            (InvoiceRuleKinds.CustomerMatch, "The invoice is addressed to the tenant (coded)."),
            (InvoiceRuleKinds.Confidence, "Every field meets the confidence threshold (coded)."),
        })
        {
            yield return new RuleKindDescriptor(kind, P0, description, NoParams(), []);
        }

        yield return new RuleKindDescriptor("required", P1, "The field at path has a value.", Params(Required("path"), ("path", PathSchema())), ["path"]);
        yield return new RuleKindDescriptor("pattern", P1, "The value at path matches a regular expression.", Params(Required("path", "pattern"), ("path", PathSchema()), ("pattern", StringSchema())), ["path"]);
        yield return new RuleKindDescriptor(
            "range",
            P1,
            "The numbers at path lie between min and max.",
            Params(Required("path"), ("path", PathSchema()), ("min", NumberSchema()), ("max", NumberSchema())),
            ["path"]);
        yield return new RuleKindDescriptor(
            "enumMember",
            P1,
            "The value at path is one of the listed codes or a code of the lookup table.",
            Params(Required("path"), ("path", PathSchema()), ("values", new JsonObject { ["type"] = "array", ["items"] = StringSchema(), ["minItems"] = 1 }), ("codeList", StringSchema())),
            ["path"]);
        yield return new RuleKindDescriptor(
            "dateOrder",
            P1,
            "The date at before is not after the date at after.",
            Params(Required("before", "after"), ("before", PathSchema()), ("after", PathSchema()), ("allowEqual", new JsonObject { ["type"] = "boolean" })),
            ["before", "after"]);
        yield return new RuleKindDescriptor(
            "datePlausibility",
            P1,
            "The date at path lies within maxPastDays before and maxFutureDays after today.",
            Params(
                Required("path"),
                ("path", PathSchema()),
                ("maxPastDays", new JsonObject { ["type"] = "integer", ["minimum"] = 0 }),
                ("maxFutureDays", new JsonObject { ["type"] = "integer", ["minimum"] = 0 }),
                ("relativeTo", new JsonObject { ["enum"] = new JsonArray("now", "documentDate") })),
            ["path"]);
        yield return new RuleKindDescriptor(
            "lookupExists",
            P1,
            "The code at path is an entry of the lookup table.",
            Params(Required("path", "table"), ("path", PathSchema()), ("table", StringSchema())),
            ["path"]);
        yield return new RuleKindDescriptor(
            "duplicate",
            P1,
            "No other document of the supplier has the same duplicateKey; onMatch block raises DUPLICATE_SUSPECTED, revision REVISION_OF.",
            Params([], ("onMatch", new JsonObject { ["enum"] = new JsonArray("block", "revision") })),
            []);
        yield return new RuleKindDescriptor(
            "confidence",
            P1,
            "Every value (or every value under paths) meets the confidence threshold.",
            Params([], ("threshold", new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 }), ("paths", new JsonObject { ["type"] = "array", ["items"] = PathSchema() })),
            ["paths"]);
        yield return new RuleKindDescriptor("customerMatch", P1, "The customer party is one of the tenant's receiving entities.", NoParams(), []);

        yield return new RuleKindDescriptor(
            "requiredIf",
            P5,
            "The field at path has a value whenever the expression when holds; with rows (for example lines[*]), path is a field of each row and when is evaluated on each row ($$ is the document).",
            Params(Required("path", "when"), ("rows", PathSchema()), ("path", PathSchema()), ("when", ExpressionSchema())),
            ["rows"]);
        yield return new RuleKindDescriptor(
            "compare",
            P5,
            "The value at left compares to the value at right (or to value) by op: eq, ne, lt, le, gt or ge; numbers within tolerance, dates and strings in order.",
            Params(
                Required("left", "op"),
                ("left", PathSchema()),
                ("op", new JsonObject { ["enum"] = new JsonArray("eq", "ne", "lt", "le", "gt", "ge") }),
                ("right", PathSchema()),
                ("value", new JsonObject { ["type"] = new JsonArray("number", "string", "boolean") }),
                ("tolerance", new JsonObject { ["type"] = "number", ["minimum"] = 0 })),
            ["left", "right"]);
        yield return new RuleKindDescriptor(
            "sumEquals",
            P5,
            "The numbers at path (for example lines[*].lineNet) add up to the number at equals, within tolerance plus perItemTolerance per item.",
            Params(
                Required("path", "equals"),
                ("path", PathSchema()),
                ("equals", PathSchema()),
                ("tolerance", new JsonObject { ["type"] = "number", ["minimum"] = 0 }),
                ("perItemTolerance", new JsonObject { ["type"] = "number", ["minimum"] = 0 })),
            ["path", "equals"]);
        yield return new RuleKindDescriptor(
            "arithmetic",
            P5,
            "On every row of rows (or once), expression gives the number at equals (a path in the row), within tolerance.",
            Params(
                Required("expression", "equals"),
                ("rows", PathSchema()),
                ("expression", ExpressionSchema()),
                ("equals", PathSchema()),
                ("tolerance", new JsonObject { ["type"] = "number", ["minimum"] = 0 })),
            ["rows"]);
        yield return new RuleKindDescriptor(
            "expression",
            P5,
            "The predicate holds for the document (or for every row of forEach); field names the flagged field in the row.",
            Params(Required("predicate"), ("predicate", ExpressionSchema()), ("forEach", PathSchema()), ("field", PathSchema())),
            ["forEach"]);

        yield return new RuleKindDescriptor(
            "tableComplete",
            P5,
            "The table report of the rows at path shows every line read: at most maxRejectedRows rejected, no page of the range without rows (blocking with everyPageInRange), and the count at declaredCount, when printed, equal to the rows; rows without a report fail unless allowUnreported.",
            Params(
                Required("path"),
                ("path", PathSchema()),
                ("maxRejectedRows", new JsonObject { ["type"] = "integer", ["minimum"] = 0 }),
                ("everyPageInRange", new JsonObject { ["type"] = "boolean" }),
                ("allowUnreported", new JsonObject { ["type"] = "boolean" }),
                ("declaredCount", PathSchema())),
            ["path", "declaredCount"]);
        yield return new RuleKindDescriptor(
            "unique",
            P5,
            "No two rows share the value at path (a row path such as lines[*].supplierSku), within the same values of the row fields scopeBy.",
            Params(Required("path"), ("path", PathSchema()), ("scopeBy", new JsonObject { ["type"] = "array", ["items"] = PathSchema(), ["maxItems"] = 5 })),
            ["path"]);
        yield return new RuleKindDescriptor(
            "ordered",
            P5,
            "In every list at path (for example lines[*].tiers) the items ascend by the item field by (strictly with strict), and the item field then.path follows then.order.",
            Params(
                Required("path", "by"),
                ("path", PathSchema()),
                ("by", PathSchema()),
                ("strict", new JsonObject { ["type"] = "boolean" }),
                ("then", new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = PathSchema(),
                        ["order"] = new JsonObject { ["enum"] = new JsonArray("increasing", "nonDecreasing", "decreasing", "nonIncreasing") },
                    },
                    ["required"] = new JsonArray("path"),
                    ["additionalProperties"] = false,
                })),
            ["path"]);
        yield return new RuleKindDescriptor(
            "deltaToPrevious",
            P5,
            "The values at path (a row path) change by at most maxChangePercent against the last approved document of the same supplier and type, joined on the row field joinOn, and at most maxChangedRowsPercent of the joined rows change.",
            Params(
                Required("path", "joinOn"),
                ("path", PathSchema()),
                ("joinOn", PathSchema()),
                ("maxChangePercent", new JsonObject { ["type"] = "number", ["minimum"] = 0 }),
                ("maxChangedRowsPercent", new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 100 }),
                ("ignoreBelowPercent", new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 100 })),
            ["path"]);
    }

    /// <summary>
    /// The parameters of the line-table kinds that name a field relative to the items of another path, by kind: the
    /// relative parameter and how its full path is formed (<c>unique.scopeBy</c> and <c>deltaToPrevious.joinOn</c> under
    /// the rows of <c>path</c>; <c>ordered.by</c> and <c>ordered.then.path</c> under the items of the lists at <c>path</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> ItemRelativeParams = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["unique"] = ["scopeBy"],
        ["deltaToPrevious"] = ["joinOn"],
        ["ordered"] = ["by", "then.path"],
    };

    private static JsonObject ExpressionSchema() => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 2000 };

    private static string[] Required(params string[] names) => names;

    private static JsonObject NoParams() => new() { ["type"] = "object", ["additionalProperties"] = false };

    private static JsonObject Params(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }

        var result = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0)
        {
            result["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]);
        }

        return result;
    }

    private static JsonObject PathSchema() => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 };

    private static JsonObject StringSchema() => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 500 };

    private static JsonObject NumberSchema() => new() { ["type"] = "number" };
}
