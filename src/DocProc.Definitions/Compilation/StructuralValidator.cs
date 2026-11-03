using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Domain.Flags;
using Json.Schema;

namespace DocProc.Definitions.Compilation;

/// <summary>
/// The structural layer of validation, the step <c>validate.schema</c> (docs/11 §11, layer 1): the values view of a
/// document against its version's compiled JSON Schema (<see cref="CompiledDefinition.JsonSchema"/>), with JsonSchema.Net.
/// <list type="bullet">
/// <item><description><c>FIELD_REQUIRED {field}</c> (blocking) for every required field without a value: a header
/// field, or a row field of an existing row, that is absent, null or blank; a required list without entries.</description></item>
/// <item><description><c>FIELD_INVALID {field, keyword}</c> for every other schema error (type, format, pattern,
/// bounds, length, enum, row cap): blocking on a required field, a warning otherwise. JSON Pointer locations become
/// the path grammar (<c>/lines/2/lineNet</c> is <c>lines[2].lineNet</c>).</description></item>
/// </list>
/// </summary>
public static class StructuralValidator
{
    private static readonly ConditionalWeakTable<CompiledDefinition, JsonSchema> Schemas = new();

    public static IReadOnlyList<DocumentFlag> Validate(JsonObject values, CompiledDefinition compiled)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(compiled);
        var flags = new List<DocumentFlag>();
        var missing = new HashSet<string>(StringComparer.Ordinal);
        Required(values, compiled.Descriptors.Roots, compiled.Descriptors, prefix: null, flags, missing);

        var schema = Schemas.GetValue(compiled, c => JsonSchema.Build(JsonSerializer.SerializeToElement(c.JsonSchema), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));
        var instance = JsonSerializer.SerializeToElement(values);
        var results = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (results.IsValid)
        {
            return flags;
        }

        var seen = new HashSet<(string, string)>();
        foreach (var detail in (results.Details ?? []).Where(d => d.Errors is { Count: > 0 }))
        {
            var path = ToPath(detail.InstanceLocation.ToString());
            if (missing.Contains(path) || IsNull(values, detail.InstanceLocation.ToString()))
            {
                continue;
            }

            foreach (var (keyword, message) in detail.Errors!)
            {
                if (keyword == "required" || !seen.Add((path, keyword)))
                {
                    continue;
                }

                var field = compiled.Descriptors.Find(FieldPathOf(path, compiled.Descriptors));
                var where = path.Length == 0 ? "The document" : $"'{path}'";
                var flag = FlagCodes.Create(
                    FlagCodes.FieldInvalid,
                    $"{where} does not fit its definition: {message}",
                    path.Length == 0 ? null : path,
                    FlagParams.Of(("field", path), ("keyword", keyword)));
                flags.Add(field is { Required: true } ? flag with { Severity = FlagSeverity.Blocking } : flag);
            }
        }

        return flags;
    }

    /// <summary>The JSON Pointer of the schema output as a path of the grammar (<c>/lines/2/lineNet</c> → <c>lines[2].lineNet</c>).</summary>
    public static string ToPath(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Length == 0 || location == "/")
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        foreach (var raw in location.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                result.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            else
            {
                if (result.Length > 0)
                {
                    result.Append('.');
                }

                result.Append(segment);
            }
        }

        return result.ToString();
    }

    private static void Required(JsonObject? node, IEnumerable<FieldDescriptor> fields, DescriptorIndex descriptors, string? prefix, List<DocumentFlag> flags, HashSet<string> missing)
    {
        foreach (var field in fields)
        {
            var path = prefix is null ? field.Key : $"{prefix}.{field.Key}";
            var value = node?[field.Key];
            var required = field.Required && !field.Deprecated;
            var empty = value is null
                        || (value is JsonValue v && v.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetValue<string>()))
                        || (field.IsList && value is JsonArray { Count: 0 });
            if (required && empty)
            {
                missing.Add(path);
                flags.Add(FlagCodes.Create(FlagCodes.FieldRequired, $"Required field '{path}' has no value.", path, FlagParams.Of(("field", path))));
                continue;
            }

            switch (field.Kind)
            {
                case FieldKinds.Array when value is JsonArray rows:
                    var children = field.Children.Select(c => descriptors.Find(c)!).ToList();
                    for (var i = 0; i < rows.Count; i++)
                    {
                        Required(rows[i] as JsonObject, children, descriptors, $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]", flags, missing);
                    }

                    break;
                case FieldKinds.Object when value is JsonObject group:
                    Required(group, field.Children.Select(c => descriptors.Find(c)!), descriptors, path, flags, missing);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>The field a concrete path lies in: the path itself, or its nearest ancestor that is a field (a member of a composite value).</summary>
    private static string FieldPathOf(string path, DescriptorIndex descriptors)
    {
        var current = path;
        while (current.Length > 0 && descriptors.Find(current) is null)
        {
            var cut = Math.Max(current.LastIndexOf('.'), current.LastIndexOf('['));
            current = cut <= 0 ? string.Empty : current[..cut];
        }

        return current;
    }

    private static bool IsNull(JsonObject root, string pointer)
    {
        JsonNode? node = root;
        foreach (var raw in pointer.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < array.Count => array[i],
                _ => null,
            };
            if (node is null)
            {
                return true;
            }
        }

        return false;
    }
}
