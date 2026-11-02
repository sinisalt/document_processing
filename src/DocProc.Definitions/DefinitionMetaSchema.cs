using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;

namespace DocProc.Definitions;

/// <summary>
/// The meta-schema of definitions, <c>schemas/document-type-definition.v1.json</c> (docs/11 §5.2): a JSON Schema
/// 2020-12 document generated from the <see cref="TypeDefinition"/> records the strict reader binds, so the schema
/// and the reader cannot drift apart, plus the syntax rules of the format (key and id patterns, the kind, severity and
/// mode vocabularies, the length caps on tenant text of §5.7 and §16). Saving a draft validates it with JsonSchema.Net
/// (licence note in docs/11 §5.5); the committed file is a snapshot of <see cref="Schema"/>, like the other
/// <c>schemas/*.v1.json</c>.
/// </summary>
public static class DefinitionMetaSchema
{
    public const string Name = "document-type-definition";
    public const int Version = 1;
    public const string Id = "https://schemas.docproc.local/document-type-definition.v1.json";

    /// <summary>The longest description, classification description or rule message a tenant may write.</summary>
    public const int MaxDescriptionLength = 2000;

    /// <summary>The longest LLM guidance or field hint (§16: tenant text sent to the LLM is length-capped).</summary>
    public const int MaxGuidanceLength = 4000;

    /// <summary>The longest label or name in one locale.</summary>
    public const int MaxLabelLength = 200;

    private static readonly Lazy<JsonObject> Generated = new(Generate);
    private static readonly Lazy<JsonSchema> Compiled = new(() => JsonSchema.Build(JsonSerializer.SerializeToElement(Generated.Value), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));

    /// <summary>A copy of the meta-schema.</summary>
    public static JsonObject Schema => (JsonObject)Generated.Value.DeepClone();

    /// <summary>The meta-schema as the committed snapshot holds it (indented, with a trailing newline).</summary>
    public static string SnapshotText => JsonSerializer.Serialize(Generated.Value, Indented) + Environment.NewLine;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The places where <paramref name="definitionJson"/> breaks the meta-schema; empty when it conforms.</summary>
    public static IReadOnlyList<DefinitionProblem> Validate(JsonElement definitionJson)
    {
        var results = Compiled.Value.Evaluate(definitionJson, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        var problems = new List<DefinitionProblem>();
        foreach (var detail in (results.Details ?? []).Where(d => d.Errors is { Count: > 0 }))
        {
            var where = detail.InstanceLocation.ToString();
            foreach (var (keyword, message) in detail.Errors!)
            {
                problems.Add(new DefinitionProblem(where.Length == 0 ? "(root)" : where, $"{message} ({keyword})"));
            }
        }

        return problems.Count > 0 ? problems.Distinct().ToList() : [new DefinitionProblem("(root)", "does not conform to the definition meta-schema.")];
    }

    /// <summary><see cref="Validate(JsonElement)"/> over text; text that is not JSON is one problem.</summary>
    public static IReadOnlyList<DefinitionProblem> Validate(string definitionJson)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);
        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            return Validate(document.RootElement);
        }
        catch (JsonException e)
        {
            return [new DefinitionProblem("(root)", $"is not JSON: {e.Message}")];
        }
    }

    private static JsonObject Generate()
    {
        var options = new JsonSerializerOptions(DefinitionSerializer.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var exporter = new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true, TransformSchemaNode = Transform };
        var root = (JsonObject)options.GetJsonSchemaAsNode(typeof(TypeDefinition), exporter);

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = Id,
            ["title"] = "Document type definition, version 1 (docs/11 §5.2)",
        };
        foreach (var (key, value) in root.ToList())
        {
            root.Remove(key);
            schema[key] = value;
        }

        return schema;
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject schema)
        {
            return node;
        }

        var property = context.PropertyInfo;
        if (property is null)
        {
            return schema;
        }

        var owner = property.DeclaringType;
        var name = property.Name;
        switch (owner.Name, name)
        {
            case (nameof(TypeDefinition), "key"):
                schema["pattern"] = "^[A-Za-z][A-Za-z0-9_.-]{0,63}$";
                break;
            case (nameof(TypeDefinition), "version"):
            case (nameof(DefinitionBase), "version"):
                schema["minimum"] = 1;
                break;
            case (nameof(TypeDefinition), "legacyKey"):
                schema["pattern"] = "^[A-Z][A-Za-z]{0,63}$";
                break;
            case (nameof(DefinitionBase), "template"):
                schema["pattern"] = "^std\\.[a-z][a-z0-9-]{0,59}$";
                break;
            case (nameof(DefinitionBase), "mode"):
                Enum(schema, "adopt", "copy");
                break;
            case (nameof(TypeDefinition), "name"):
            case (nameof(FieldDefinition), "label"):
            case (nameof(EnumOption), "label"):
            case (nameof(UiSection), "label"):
                LocaleMap(schema, MaxLabelLength, minProperties: name == "name" && owner.Name == nameof(TypeDefinition) ? 1 : 0);
                break;
            case (nameof(RuleDefinition), "message"):
                LocaleMap(schema, MaxDescriptionLength, minProperties: 0);
                break;
            case (nameof(TypeDefinition), "description"):
            case (nameof(FieldDefinition), "description"):
            case (nameof(ClassificationDefinition), "description"):
                schema["maxLength"] = MaxDescriptionLength;
                break;
            case (nameof(LlmDefinition), "guidance"):
            case (nameof(ExtractionHints), "llmHint"):
                schema["maxLength"] = MaxGuidanceLength;
                break;
            case (nameof(FieldDefinition), "id"):
                schema["pattern"] = "^fld_[a-z0-9_]{1,60}$";
                break;
            case (nameof(FieldDefinition), "key"):
                schema["pattern"] = "^[a-z][A-Za-z0-9_]{0,63}$";
                break;
            case (nameof(FieldDefinition), "kind"):
                Enum(schema, [.. FieldKinds.All.Order(StringComparer.Ordinal)]);
                break;
            case (nameof(FieldDefinition), "maxItems"):
                schema["minimum"] = 1;
                schema["maximum"] = DefinitionChecker.MaxItemsLimit;
                break;
            case (nameof(FieldDefinition), "maxLength"):
                schema["minimum"] = 1;
                break;
            case (nameof(FieldDefinition), "pattern"):
            case (nameof(ExtractionHints), "pattern"):
                schema["maxLength"] = 500;
                break;
            case (nameof(EnumOption), "code"):
                schema["minLength"] = 1;
                schema["maxLength"] = 100;
                break;
            case (nameof(RuleDefinition), "id"):
            case (nameof(StepDefinition), "id"):
                schema["pattern"] = "^[A-Za-z][A-Za-z0-9_.-]{0,63}$";
                break;
            case (nameof(RuleDefinition), "kind"):
            case (nameof(StepDefinition), "kind"):
                schema["pattern"] = "^[a-z][A-Za-z0-9_.-]{0,63}$";
                break;
            case (nameof(RuleDefinition), "severity"):
                Enum(schema, "blocking", "warning", "info");
                break;
            case (nameof(RuleDefinition), "appliesWhen"):
                schema["maxLength"] = 1000;
                break;
            case (nameof(SplitPolicy), "mode"):
                Enum(schema, "auto", "never", "perPage", "llm");
                break;
            case (nameof(ClassifyPolicy), "llmThreshold"):
            case (nameof(ReviewPolicy), "minConfidence"):
                schema["minimum"] = 0;
                schema["maximum"] = 1;
                break;
            case (nameof(ReviewPolicy), "priority"):
                Enum(schema, "normal", "high", "urgent");
                break;
            case (nameof(ReviewPolicy), "dueBy"):
                Enum(schema, "validFrom", "validUntil");
                break;
            case (nameof(LinesPolicy), "maxLowConfidenceRows"):
                schema["minimum"] = 0;
                break;
            case (nameof(ClassificationDefinition), "keywords"):
            case (nameof(ClassificationDefinition), "labels"):
            case (nameof(ClassificationDefinition), "dateLabels"):
            case (nameof(ClassificationDefinition), "negatives"):
            case (nameof(ExtractionHints), "labels"):
                schema["propertyNames"] = new JsonObject { ["pattern"] = "^[a-z]{2}$" };
                break;
            default:
                break;
        }

        return schema;
    }

    private static void Enum(JsonObject schema, params string[] values)
    {
        var nullable = schema["type"] is JsonArray types && types.Any(t => t?.GetValue<string>() == "null");
        schema["enum"] = new JsonArray([.. values.Select(v => (JsonNode?)v), .. nullable ? new JsonNode?[] { null } : []]);
    }

    private static void LocaleMap(JsonObject schema, int maxLength, int minProperties)
    {
        schema["propertyNames"] = new JsonObject { ["pattern"] = "^[a-z]{2}(-[A-Z]{2})?$" };
        if (schema["additionalProperties"] is JsonObject values)
        {
            values["minLength"] = 1;
            values["maxLength"] = maxLength;
        }

        if (minProperties > 0)
        {
            schema["minProperties"] = minProperties;
        }
    }
}
