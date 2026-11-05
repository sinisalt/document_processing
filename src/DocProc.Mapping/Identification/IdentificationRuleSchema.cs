using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace DocProc.Mapping.Identification;

/// <summary>One place where an identification rule breaks a check: a JSON path (or a rule path such as <c>all[1]</c>) and why.</summary>
public sealed record IdentificationProblem(string Path, string Message);

/// <summary>
/// The meta-schema of identification rules, <c>schemas/identification-rule.v1.json</c> (docs/11 §8.5), a JSON Schema
/// 2020-12 document: a rule is a group (<c>all</c>, <c>any</c>, <c>none</c> and a location), a member is a group or one
/// identifier of a kind, each with its own closed set of keys. The committed file is a snapshot of <see cref="SnapshotText"/>.
/// What a schema cannot say (nesting depth, the number of identifiers, a positive identifier, patterns that compile and
/// stay small, <c>allowOcrErrors</c> never on the only positive identifier, the keys of the scope) is checked by
/// <see cref="IdentificationRuleChecks"/>.
/// </summary>
public static class IdentificationRuleSchema
{
    public const string Name = "identification-rule";
    public const int Version = 1;
    public const string Id = "https://schemas.docproc.local/identification-rule.v1.json";

    private static readonly Lazy<JsonObject> Generated = new(Generate);
    private static readonly Lazy<JsonSchema> Compiled = new(() => JsonSchema.Build(JsonSerializer.SerializeToElement(Generated.Value), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>A copy of the meta-schema.</summary>
    public static JsonObject Schema => (JsonObject)Generated.Value.DeepClone();

    /// <summary>The meta-schema as the committed snapshot holds it (indented, with a trailing newline).</summary>
    public static string SnapshotText => JsonSerializer.Serialize(Generated.Value, Indented) + Environment.NewLine;

    /// <summary>The places where <paramref name="rule"/> breaks the meta-schema; empty when it conforms.</summary>
    public static IReadOnlyList<IdentificationProblem> Validate(JsonElement rule)
    {
        var results = Compiled.Value.Evaluate(rule, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        var problems = new List<IdentificationProblem>();
        foreach (var detail in (results.Details ?? []).Where(d => d.Errors is { Count: > 0 }))
        {
            var where = detail.InstanceLocation.ToString();
            foreach (var (keyword, message) in detail.Errors!)
            {
                problems.Add(new IdentificationProblem(where.Length == 0 ? "(root)" : where, $"{message} ({keyword})"));
            }
        }

        return problems.Count > 0 ? [.. problems.Distinct()] : [new IdentificationProblem("(root)", "does not conform to the identification rule meta-schema.")];
    }

    private static JsonObject Generate()
    {
        static JsonObject Str(int max = 200) => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = max };
        static JsonObject Ref(string name) => new() { ["$ref"] = $"#/$defs/{name}" };

        JsonObject Location() => new()
        {
            ["page"] = new JsonObject
            {
                ["oneOf"] = new JsonArray(
                    new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10000 },
                    new JsonObject { ["enum"] = new JsonArray("last", "any") }),
            },
            ["region"] = new JsonObject { ["enum"] = new JsonArray([.. IdentificationRegions.All.Select(r => (JsonNode)r)]) },
            ["bBox"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 4,
                ["maxItems"] = 4,
                ["items"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1000 },
            },
            ["tolerance"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 200 },
        };

        JsonObject Closed(JsonObject properties, params string[] required)
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
                ["additionalProperties"] = false,
            };
            return schema;
        }

        JsonObject WithLocation(JsonObject own)
        {
            foreach (var (key, value) in Location())
            {
                own[key] = value!.DeepClone();
            }

            return own;
        }

        JsonObject Members() => new()
        {
            ["type"] = "array",
            ["minItems"] = 1,
            ["maxItems"] = IdentificationRule.MaxIdentifiers,
            ["items"] = Ref("member"),
        };

        var defs = new JsonObject
        {
            ["text"] = Closed(WithLocation(new JsonObject
            {
                ["text"] = Str(),
                ["caseSensitive"] = new JsonObject { ["type"] = "boolean" },
                ["allowOcrErrors"] = new JsonObject { ["type"] = "boolean" },
            }), "text"),
            ["regex"] = Closed(WithLocation(new JsonObject { ["regex"] = Str(500) }), "regex"),
            ["supplierValue"] = Closed(WithLocation(new JsonObject
            {
                ["supplierValue"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^(vatId|taxId|iban|attributes\\.[A-Za-z_][A-Za-z0-9_]{0,63})$",
                },
            }), "supplierValue"),
            ["source"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["source"] = new JsonObject { ["enum"] = new JsonArray([.. SourceFacts.Keys.Select(k => (JsonNode)k)]) },
                    ["regex"] = Str(500),
                    ["equals"] = Str(),
                },
                ["required"] = new JsonArray("source"),
                ["oneOf"] = new JsonArray(
                    new JsonObject { ["required"] = new JsonArray("regex") },
                    new JsonObject { ["required"] = new JsonArray("equals") }),
                ["additionalProperties"] = false,
            },
            ["xmlPath"] = Closed(new JsonObject { ["xmlPath"] = Str(400), ["equals"] = Str() }, "xmlPath"),
            ["cell"] = Closed(new JsonObject { ["cell"] = Str(64), ["equals"] = Str() }, "cell"),
            ["group"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = WithLocation(new JsonObject { ["all"] = Members(), ["any"] = Members(), ["none"] = Members() }),
                ["anyOf"] = new JsonArray(
                    new JsonObject { ["required"] = new JsonArray("all") },
                    new JsonObject { ["required"] = new JsonArray("any") },
                    new JsonObject { ["required"] = new JsonArray("none") }),
                ["additionalProperties"] = false,
            },
            ["member"] = new JsonObject
            {
                ["oneOf"] = new JsonArray(Ref("group"), Ref("text"), Ref("regex"), Ref("supplierValue"), Ref("source"), Ref("xmlPath"), Ref("cell")),
            },
        };

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = Id,
            ["title"] = "Identification rule",
            ["description"] = "A boolean combination of identifiers that proves a document's design (docs/11 §8.5): all, any and none, an implicit AND of the ones present; identifiers text, regex, supplierValue, source, xmlPath and cell, with page, region or bBox inherited key by key from the groups.",
            ["$ref"] = "#/$defs/group",
            ["$defs"] = defs,
        };
    }
}
