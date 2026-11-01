using System.Text.Json.Nodes;

namespace DocProc.Llm.Schema;

/// <summary>
/// Checks a schema against the intersection of Anthropic's and OpenAI's structured-output rules. Run in CI for
/// every canonical schema so a contract change cannot silently break one vendor.
/// </summary>
public static class SchemaPortability
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "minLength", "maxLength", "pattern", "minItems", "maxItems", "uniqueItems",
        "minProperties", "maxProperties", "patternProperties", "default",
        "$ref", "oneOf", "allOf", "not", "if", "then", "else", "dependentSchemas", "dependentRequired",
    };

    private static readonly HashSet<string> AllowedFormats = new(StringComparer.Ordinal)
    {
        "date", "date-time", "time", "duration", "email", "hostname", "uri", "uuid", "ipv4", "ipv6",
    };

    public static IReadOnlyList<string> Check(JsonSchemaDefinition definition) => Check(definition.Schema);

    public static IReadOnlyList<string> Check(JsonObject schema)
    {
        var violations = new List<string>();
        Walk(schema, "#", violations, depth: 0);
        return violations;
    }

    public static void EnsurePortable(JsonSchemaDefinition definition)
    {
        var violations = Check(definition);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException($"Schema {definition} is not portable:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
        }
    }

    private static void Walk(JsonNode? node, string path, List<string> violations, int depth)
    {
        if (depth > 32)
        {
            violations.Add($"{path}: nesting deeper than 32 levels, likely recursive");
            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var (key, value) in obj)
        {
            if (Forbidden.Contains(key))
            {
                violations.Add($"{path}: keyword '{key}' is not accepted by every provider");
            }

            if (key == "format" && value is JsonValue fv && fv.TryGetValue<string>(out var format) && !AllowedFormats.Contains(format))
            {
                violations.Add($"{path}: format '{format}' is not accepted by every provider");
            }
        }

        if (obj["enum"] is not null && obj["type"] is null)
        {
            violations.Add($"{path}: an enum needs an explicit 'type' for strict-mode providers");
        }

        if (obj["properties"] is JsonObject properties)
        {
            if (obj["additionalProperties"] is not JsonValue ap || !ap.TryGetValue<bool>(out var apValue) || apValue)
            {
                violations.Add($"{path}: objects must set additionalProperties to false");
            }

            var required = (obj["required"] as JsonArray)?.Select(r => r?.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var (name, child) in properties)
            {
                if (!required.Contains(name))
                {
                    violations.Add($"{path}/properties/{name}: every property must be listed in 'required' (make it nullable instead of optional)");
                }

                Walk(child, $"{path}/properties/{name}", violations, depth + 1);
            }
        }

        if (obj["items"] is JsonObject items)
        {
            Walk(items, $"{path}/items", violations, depth + 1);
        }

        if (obj["anyOf"] is JsonArray anyOf)
        {
            for (var i = 0; i < anyOf.Count; i++)
            {
                Walk(anyOf[i], $"{path}/anyOf[{i}]", violations, depth + 1);
            }
        }
    }
}
