using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DocProc.Llm.Schema;

/// <summary>
/// Generates JSON schemas from the contract types and rewrites them to the structured-output intersection
/// (docs/08 "Schema portability"): every property required, optional values nullable, additionalProperties false,
/// no numeric or length constraints, no recursion.
/// </summary>
public static class PortableSchemaGenerator
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly string[] ForbiddenKeywords =
    [
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "minLength", "maxLength", "pattern", "minItems", "maxItems", "uniqueItems",
        "minProperties", "maxProperties", "patternProperties", "default",
    ];

    public static JsonSchemaDefinition Generate<T>(string name, string version)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (ctx, node) => Transform(node),
        };

        var node = SerializerOptions.GetJsonSchemaAsNode(typeof(T), exporterOptions);
        if (node is not JsonObject obj)
        {
            throw new InvalidOperationException($"Schema root for {typeof(T).Name} is not an object.");
        }

        return new JsonSchemaDefinition(name, version, obj);
    }

    private static JsonNode Transform(JsonNode node)
    {
        if (node is not JsonObject obj)
        {
            return node;
        }

        foreach (var keyword in ForbiddenKeywords)
        {
            obj.Remove(keyword);
        }

        if (obj["properties"] is JsonObject properties)
        {
            obj["additionalProperties"] = false;
            obj["required"] = new JsonArray(properties.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray());
        }

        // String enums come out as a bare "enum" list; OpenAI strict mode wants an explicit type next to it.
        if (obj["enum"] is JsonArray values && obj["type"] is null)
        {
            var nullable = values.Any(v => v is null);
            obj["type"] = nullable ? new JsonArray("string", "null") : "string";
        }

        return obj;
    }
}
