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
/// "compile now, run later". The P5 kinds (<c>requiredIf</c>, <c>compare</c>, <c>sumEquals</c>, <c>arithmetic</c>,
/// <c>expression</c>, <c>tableComplete</c>, <c>unique</c>, <c>ordered</c>, <c>deltaToPrevious</c>) are not listed.
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
    }

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
