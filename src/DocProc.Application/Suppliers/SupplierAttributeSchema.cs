using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocProc.Application.Suppliers;

/// <summary>One typed attribute of <see cref="SupplierAttributeSchema"/>.</summary>
public sealed record SupplierAttributeDefinition(
    string Key,
    string Type,
    string? Title,
    string? Description,
    IReadOnlyList<JsonElement>? Enum,
    int? MinLength,
    int? MaxLength,
    string? Pattern,
    decimal? Minimum,
    decimal? Maximum);

/// <summary>
/// The tenant's supplier attribute schema (docs/11 §6.4 <c>TenantSettings.SupplierAttributeSchema</c>): a JSON Schema
/// object of a typed subset, so the Attributes form can be generated from it and every supplier, group and per-entity
/// attribute write is checked against it in the API process without a general schema engine (whose <c>pattern</c> is
/// a backtracking regex, which P3 confined to the renderer process). The subset:
/// <code>
/// { "type": "object",
///   "properties": { "erpVendorId": { "type": "string", "title": "ERP vendor", "pattern": "^V-[0-9]{5}$", "maxLength": 20 },
///                   "paymentBlock": { "type": "boolean" },
///                   "costCentre": { "type": "string", "enum": ["4711", "4712"] },
///                   "discount": { "type": "number", "minimum": 0, "maximum": 100 } },
///   "required": ["erpVendorId"],
///   "additionalProperties": false }
/// </code>
/// A property's <c>type</c> is <c>string</c>, <c>number</c>, <c>integer</c> or <c>boolean</c>; its keywords are
/// <c>title</c>, <c>description</c>, <c>enum</c>, <c>minLength</c>, <c>maxLength</c>, <c>pattern</c> (a .NET regular
/// expression, run non-backtracking with a 100 ms timeout), <c>minimum</c> and <c>maximum</c>. <c>required</c> applies to
/// a supplier's own attributes (a group's and an entity's are partial). <c>additionalProperties</c> defaults to true,
/// so a schema that lists a few typed attributes keeps the free ones of P3 valid. Null on an attribute is always
/// allowed (it clears the attribute).
/// </summary>
public sealed partial class SupplierAttributeSchema
{
    public const int MaxProperties = 50;
    public const int MaxSchemaLength = 32 * 1024;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly HashSet<string> Types = new(["string", "number", "integer", "boolean"], StringComparer.Ordinal);
    private static readonly HashSet<string> PropertyKeywords = new(["type", "title", "description", "enum", "minLength", "maxLength", "pattern", "minimum", "maximum"], StringComparer.Ordinal);
    private static readonly HashSet<string> RootKeywords = new(["type", "properties", "required", "additionalProperties", "title", "description", "$schema"], StringComparer.Ordinal);

    private readonly Dictionary<string, (SupplierAttributeDefinition Definition, Regex? Pattern)> _properties;

    private SupplierAttributeSchema(Dictionary<string, (SupplierAttributeDefinition, Regex?)> properties, IReadOnlyList<string> required, bool additionalProperties)
    {
        _properties = properties;
        Required = required;
        AdditionalProperties = additionalProperties;
    }

    public IReadOnlyList<SupplierAttributeDefinition> Properties => [.. _properties.Values.Select(p => p.Definition)];

    public IReadOnlyList<string> Required { get; }

    public bool AdditionalProperties { get; }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex KeyPattern();

    /// <summary>Reads a stored or submitted schema; <paramref name="errors"/> says why it is not one of the subset (then null).</summary>
    public static SupplierAttributeSchema? TryParse(string? json, out IReadOnlyList<string> errors)
    {
        var found = new List<string>();
        errors = found;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        if (json.Length > MaxSchemaLength)
        {
            found.Add($"The schema is at most {MaxSchemaLength / 1024} KB.");
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            found.Add($"The schema is not JSON: {e.Message}");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                found.Add("The schema is a JSON object.");
                return null;
            }

            foreach (var member in root.EnumerateObject().Where(m => !RootKeywords.Contains(m.Name)))
            {
                found.Add($"'{member.Name}' is not a keyword of the supplier attribute schema (type, properties, required, additionalProperties, title, description).");
            }

            if (root.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "object"))
            {
                found.Add("type is \"object\".");
            }

            var properties = new Dictionary<string, (SupplierAttributeDefinition, Regex?)>(StringComparer.Ordinal);
            if (root.TryGetProperty("properties", out var props))
            {
                if (props.ValueKind != JsonValueKind.Object)
                {
                    found.Add("properties is an object of attribute definitions.");
                }
                else
                {
                    foreach (var property in props.EnumerateObject())
                    {
                        if (properties.Count >= MaxProperties)
                        {
                            found.Add($"At most {MaxProperties} attributes.");
                            break;
                        }

                        if (ReadProperty(property, found) is { } definition)
                        {
                            properties[property.Name] = definition;
                        }
                    }
                }
            }

            var required = new List<string>();
            if (root.TryGetProperty("required", out var req))
            {
                if (req.ValueKind != JsonValueKind.Array || req.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String))
                {
                    found.Add("required is an array of attribute keys.");
                }
                else
                {
                    foreach (var key in req.EnumerateArray().Select(r => r.GetString()!).Distinct(StringComparer.Ordinal))
                    {
                        if (!properties.ContainsKey(key))
                        {
                            found.Add($"required names '{key}', which properties does not define.");
                        }

                        required.Add(key);
                    }
                }
            }

            var additional = true;
            if (root.TryGetProperty("additionalProperties", out var add))
            {
                if (add.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    additional = add.GetBoolean();
                }
                else
                {
                    found.Add("additionalProperties is true or false.");
                }
            }

            return found.Count == 0 ? new SupplierAttributeSchema(properties, required, additional) : null;
        }
    }

    private static (SupplierAttributeDefinition, Regex?)? ReadProperty(JsonProperty property, List<string> errors)
    {
        var key = property.Name;
        if (!KeyPattern().IsMatch(key))
        {
            errors.Add($"'{Shorten(key)}' is not an attribute key: a letter, then letters, digits, '_', '-' or '.', at most 64 characters.");
            return null;
        }

        var value = property.Value;
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{key}: an attribute is defined by an object with a type.");
            return null;
        }

        foreach (var member in value.EnumerateObject().Where(m => !PropertyKeywords.Contains(m.Name)))
        {
            errors.Add($"{key}: '{member.Name}' is not a keyword of an attribute (type, title, description, enum, minLength, maxLength, pattern, minimum, maximum).");
        }

        var type = value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : null;
        if (type is null || !Types.Contains(type))
        {
            errors.Add($"{key}: type is string, number, integer or boolean.");
            return null;
        }

        string? Text(string name)
        {
            if (!value.TryGetProperty(name, out var v))
            {
                return null;
            }

            if (v.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{key}: {name} is a string.");
                return null;
            }

            return v.GetString();
        }

        int? Count(string name)
        {
            if (!value.TryGetProperty(name, out var v))
            {
                return null;
            }

            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < 0)
            {
                errors.Add($"{key}: {name} is a whole number of at least 0.");
                return null;
            }

            if (type != "string")
            {
                errors.Add($"{key}: {name} applies to a string attribute.");
            }

            return n;
        }

        decimal? Bound(string name)
        {
            if (!value.TryGetProperty(name, out var v))
            {
                return null;
            }

            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var n))
            {
                errors.Add($"{key}: {name} is a number.");
                return null;
            }

            if (type is not ("number" or "integer"))
            {
                errors.Add($"{key}: {name} applies to a number attribute.");
            }

            return n;
        }

        List<JsonElement>? values = null;
        if (value.TryGetProperty("enum", out var e))
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() is 0 or > 200)
            {
                errors.Add($"{key}: enum is an array of 1 to 200 values.");
            }
            else
            {
                values = [.. e.EnumerateArray().Select(x => x.Clone())];
                if (values.Any(x => !Fits(type, x)))
                {
                    errors.Add($"{key}: every enum value is a {type}.");
                }
            }
        }

        var patternText = Text("pattern");
        Regex? pattern = null;
        if (patternText is not null)
        {
            if (type != "string")
            {
                errors.Add($"{key}: pattern applies to a string attribute.");
            }
            else if (patternText.Length > 400)
            {
                errors.Add($"{key}: pattern is at most 400 characters.");
            }
            else
            {
                try
                {
                    pattern = new Regex(patternText, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, PatternTimeout);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    errors.Add($"{key}: pattern is not a regular expression this check can run: {ex.Message}");
                }
            }
        }

        var definition = new SupplierAttributeDefinition(key, type, Text("title"), Text("description"), values, Count("minLength"), Count("maxLength"), patternText, Bound("minimum"), Bound("maximum"));
        return (definition, pattern);
    }

    /// <summary>
    /// The errors of <paramref name="attributes"/> against the schema, keyed by attribute (an empty list when they fit).
    /// <paramref name="checkRequired"/> is true for a supplier's own attributes.
    /// </summary>
    public IReadOnlyList<(string Key, string Message)> Validate(IReadOnlyDictionary<string, JsonElement> attributes, bool checkRequired)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var errors = new List<(string, string)>();
        foreach (var (key, value) in attributes)
        {
            if (!_properties.TryGetValue(key, out var property))
            {
                if (!AdditionalProperties)
                {
                    errors.Add((key, $"'{Shorten(key)}' is not an attribute of the tenant's supplier attribute schema."));
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            var (definition, pattern) = property;
            if (!Fits(definition.Type, value))
            {
                errors.Add((key, $"{key} is a {definition.Type}."));
                continue;
            }

            if (definition.Enum is { } allowed && !allowed.Any(a => JsonElement.DeepEquals(a, value)))
            {
                errors.Add((key, $"{key} is one of {string.Join(", ", allowed.Select(a => a.GetRawText()))}."));
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()!;
                if (definition.MinLength is { } min && text.Length < min)
                {
                    errors.Add((key, $"{key} has at least {min.ToString(CultureInfo.InvariantCulture)} characters."));
                }

                if (definition.MaxLength is { } max && text.Length > max)
                {
                    errors.Add((key, $"{key} has at most {max.ToString(CultureInfo.InvariantCulture)} characters."));
                }

                if (pattern is not null && !Matches(pattern, text))
                {
                    errors.Add((key, $"{key} does not match the pattern {definition.Pattern}."));
                }
            }
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                if (definition.Minimum is { } minimum && number < minimum)
                {
                    errors.Add((key, $"{key} is at least {minimum.ToString(CultureInfo.InvariantCulture)}."));
                }

                if (definition.Maximum is { } maximum && number > maximum)
                {
                    errors.Add((key, $"{key} is at most {maximum.ToString(CultureInfo.InvariantCulture)}."));
                }
            }
        }

        if (checkRequired)
        {
            foreach (var key in Required.Where(k => !attributes.TryGetValue(k, out var v) || v.ValueKind == JsonValueKind.Null))
            {
                errors.Add((key, $"{key} is required by the tenant's supplier attribute schema."));
            }
        }

        return errors;
    }

    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool Fits(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) && decimal.Truncate(d) == d,
        _ => false,
    };

    private static string Shorten(string key) => key.Length > 40 ? key[..40] + "…" : key;
}
