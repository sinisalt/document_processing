using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocProc.Domain.Validation;

/// <summary>A value of the values view at a concrete path (<c>lines[2].price.amount</c>).</summary>
public readonly record struct PathValue(string Path, JsonNode? Value);

/// <summary>
/// Reads the values view by the path grammar of rule parameters (docs/11 §11): segments joined by dots, <c>[*]</c>
/// after a list or an array for every entry, members of a value shape after the field (<c>lines[*].price.amount</c>).
/// </summary>
public static class ValuePaths
{
    /// <summary>
    /// Every value <paramref name="path"/> names, with its concrete path. A plain segment through an absent value
    /// yields one null; <c>[*]</c> over an absent or empty list yields nothing (there is no entry to check).
    /// </summary>
    public static IEnumerable<PathValue> Select(JsonObject? root, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IEnumerable<PathValue> current = [new PathValue(string.Empty, root)];
        foreach (var raw in path.Split('.'))
        {
            var many = raw.EndsWith("[*]", StringComparison.Ordinal);
            var key = many ? raw[..^3] : raw;
            current = current.SelectMany(p => Step(p, key, many)).ToList();
        }

        return current;
    }

    private static IEnumerable<PathValue> Step(PathValue at, string key, bool many)
    {
        var path = at.Path.Length == 0 ? key : $"{at.Path}.{key}";
        var child = at.Value is JsonObject obj ? obj[key] : null;
        if (!many)
        {
            yield return new PathValue(path, child);
            yield break;
        }

        if (child is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                yield return new PathValue($"{path}[{i.ToString(CultureInfo.InvariantCulture)}]", array[i]);
            }
        }
    }

    /// <summary>A string value, or null for anything else.</summary>
    public static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>A number, or the <c>amount</c> of a money value, or a numeric string; null otherwise.</summary>
    public static decimal? Number(JsonNode? node) => node switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<decimal>(out var number) => number,
        JsonValue value when value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var real) => (decimal)real,
        JsonValue value when value.GetValueKind() == JsonValueKind.String && decimal.TryParse(value.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        JsonObject money when money["amount"] is { } amount => Number(amount),
        _ => null,
    };

    /// <summary>A <c>yyyy-MM-dd</c> date, or null.</summary>
    public static DateOnly? Date(JsonNode? node) =>
        Text(node) is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>Whether the value counts as empty for a required check: null, or a string of white space only.</summary>
    public static bool IsEmpty(JsonNode? node) => node is null || (Text(node) is { } text && string.IsNullOrWhiteSpace(text));

    /// <summary>The value as a short invariant string for flag parameters (at most 200 characters).</summary>
    public static string Describe(JsonNode? node)
    {
        var text = node switch
        {
            null => "null",
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            _ => node.ToJsonString(),
        };
        return text.Length <= 200 ? text : text[..200];
    }

    /// <summary>Whether a concrete path (<c>lines[3].sku</c>) lies under a rule path (<c>lines[*]</c>, <c>lines</c>, <c>lines[*].sku</c>).</summary>
    public static bool IsUnder(string concretePath, string rulePath)
    {
        var normalized = Normalize(concretePath);
        var prefix = Normalize(rulePath);
        return normalized == prefix
               || normalized.StartsWith(prefix + ".", StringComparison.Ordinal)
               || normalized.StartsWith(prefix + "[", StringComparison.Ordinal);
    }

    /// <summary>A concrete path with every index written as <c>[*]</c> (<c>lines[3].sku</c> is <c>lines[*].sku</c>).</summary>
    public static string Generalize(string concretePath)
    {
        ArgumentNullException.ThrowIfNull(concretePath);
        return Normalize(concretePath);
    }

    private static string Normalize(string path)
    {
        var result = new StringBuilder(path.Length);
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '[')
            {
                var end = path.IndexOf(']', i);
                if (end < 0)
                {
                    break;
                }

                result.Append("[*]");
                i = end;
                continue;
            }

            result.Append(path[i]);
        }

        return result.ToString();
    }
}
