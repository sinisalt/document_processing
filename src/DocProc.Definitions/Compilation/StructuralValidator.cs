using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Domain.Flags;
using DocProc.Domain.Validation;
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
/// A field's own <c>pattern</c> is a tenant's regular expression and never reaches the schema evaluator, whose
/// <c>pattern</c> keyword runs a backtracking regex without a timeout (one tenant's <c>^(a+)+$</c> would stall the
/// worker every tenant shares). The validator evaluates the schema without those keywords and checks the field
/// patterns itself, with a non-backtracking regex and <see cref="PatternTimeout"/>, reporting
/// <c>FIELD_INVALID {field, keyword: pattern}</c> the same way; a match the timeout cuts short counts as no match.
/// </summary>
public static class StructuralValidator
{
    /// <summary>The longest a field pattern may take on one value.</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The patterns the compiler writes itself (a currency code); they are safe and stay in the schema.</summary>
    private static readonly HashSet<string> FixedPatterns = new(StringComparer.Ordinal) { "^[A-Z]{3}$" };

    private static readonly ConditionalWeakTable<CompiledDefinition, Prepared> Schemas = new();

    public static IReadOnlyList<DocumentFlag> Validate(JsonObject values, CompiledDefinition compiled)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(compiled);
        var flags = new List<DocumentFlag>();
        var missing = new HashSet<string>(StringComparer.Ordinal);
        Required(values, compiled.Descriptors.Roots, compiled.Descriptors, prefix: null, flags, missing, only: null);

        var prepared = Schemas.GetValue(compiled, Prepare);
        var instance = JsonSerializer.SerializeToElement(values);
        var results = prepared.Schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Patterns(values, prepared.Patterns, compiled.Descriptors, flags, missing);
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

    /// <summary>
    /// Only the <c>FIELD_REQUIRED</c> part of <see cref="Validate"/>, for the required fields among
    /// <paramref name="paths"/> (definition paths, <c>lines[*].sku</c>): what the validate stage checks for a type that
    /// does not list <c>validate.schema</c> (<c>EffectiveType.RequiredOutsideSchemaStep</c>).
    /// </summary>
    public static IReadOnlyList<DocumentFlag> ValidateRequired(JsonObject values, CompiledDefinition compiled, IReadOnlySet<string> paths)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(paths);
        var flags = new List<DocumentFlag>();
        if (paths.Count > 0)
        {
            Required(values, compiled.Descriptors.Roots, compiled.Descriptors, prefix: null, flags, new HashSet<string>(StringComparer.Ordinal), paths);
        }

        return flags;
    }

    /// <summary>
    /// The schema the evaluator runs, without any <c>pattern</c> keyword but the compiler's fixed ones, and the field
    /// patterns to check separately.
    /// </summary>
    private static Prepared Prepare(CompiledDefinition compiled)
    {
        var patterns = DefinitionPaths.Enumerate(compiled.Definition)
            .Where(f => f.Field.Pattern is { } p && !FixedPatterns.Contains(p) && f.Field.Kind is FieldKinds.String or FieldKinds.Text or FieldKinds.Identifier)
            .Select(f => new FieldPattern(f.Field.List ? $"{f.Path}[*]" : f.Path, f.Field.Pattern!, TryRegex(f.Field.Pattern!)))
            .ToList();
        var schema = (JsonObject)compiled.JsonSchema.DeepClone();
        StripPatterns(schema);
        return new Prepared(JsonSchema.Build(JsonSerializer.SerializeToElement(schema), new BuildOptions { SchemaRegistry = new SchemaRegistry() }), patterns);
    }

    private static Regex? TryRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (ArgumentException)
        {
            // The publish checks refuse such a pattern; one that still does not compile matches nothing.
            return null;
        }
    }

    private static void StripPatterns(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["pattern"] is JsonValue value && value.TryGetValue<string>(out var pattern) && !FixedPatterns.Contains(pattern))
                {
                    obj.Remove("pattern");
                }

                foreach (var (_, child) in obj.ToList())
                {
                    StripPatterns(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    StripPatterns(child);
                }

                break;
            default:
                break;
        }
    }

    private static void Patterns(JsonObject values, IReadOnlyList<FieldPattern> patterns, DescriptorIndex descriptors, List<DocumentFlag> flags, HashSet<string> missing)
    {
        foreach (var field in patterns)
        {
            var required = descriptors.Find(field.Path) is { Required: true, Deprecated: false };
            foreach (var (path, node) in ValuePaths.Select(values, field.Path))
            {
                if (missing.Contains(path) || ValuePaths.Text(node) is not { } text)
                {
                    continue;
                }

                bool matches;
                try
                {
                    matches = field.Regex?.IsMatch(text) ?? false;
                }
                catch (RegexMatchTimeoutException)
                {
                    matches = false;
                }

                if (matches)
                {
                    continue;
                }

                var flag = FlagCodes.Create(
                    FlagCodes.FieldInvalid,
                    $"'{path}' does not fit its definition: the value does not match the pattern '{field.Pattern}'.",
                    path,
                    FlagParams.Of(("field", path), ("keyword", "pattern")));
                flags.Add(required ? flag with { Severity = FlagSeverity.Blocking } : flag);
            }
        }
    }

    private sealed record FieldPattern(string Path, string Pattern, Regex? Regex);

    private sealed record Prepared(JsonSchema Schema, IReadOnlyList<FieldPattern> Patterns);

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

    private static void Required(JsonObject? node, IEnumerable<FieldDescriptor> fields, DescriptorIndex descriptors, string? prefix, List<DocumentFlag> flags, HashSet<string> missing, IReadOnlySet<string>? only)
    {
        foreach (var field in fields)
        {
            var path = prefix is null ? field.Key : $"{prefix}.{field.Key}";
            var value = node?[field.Key];
            var required = field.Required && !field.Deprecated && (only is null || only.Contains(field.Path));
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
                        Required(rows[i] as JsonObject, children, descriptors, $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]", flags, missing, only);
                    }

                    break;
                case FieldKinds.Object when value is JsonObject group:
                    Required(group, field.Children.Select(c => descriptors.Find(c)!), descriptors, path, flags, missing, only);
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
