using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Domain.Common;
using DocProc.Domain.DeliveryNotes;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.PurchaseOrders;
using DocProc.Infrastructure.Persistence;

namespace DocProc.Api.Documents;

/// <summary>A correction that could not be applied; <see cref="Path"/> names the offending entry for the validation problem.</summary>
public sealed class CanonicalPatchException : Exception
{
    public CanonicalPatchException()
        : this(string.Empty, "The correction could not be applied.")
    {
    }

    public CanonicalPatchException(string message)
        : this(string.Empty, message)
    {
    }

    public CanonicalPatchException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Path = string.Empty;
    }

    public CanonicalPatchException(string path, string message)
        : base(message)
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>What one correction changed: the field's value before and after, as JSON, for the <c>Corrections</c> audit table.</summary>
public sealed record AppliedCorrection(string Path, string? OldValueJson, string? NewValueJson, string? Reason);

public sealed record CanonicalPatchResult(string Json, IReadOnlyList<AppliedCorrection> Applied);

/// <summary>
/// Applies reviewer corrections to the stored canonical JSON by path (docs/05 "PATCH /documents/{id}/fields"). The
/// walker follows the path on the domain type (so only real fields are reachable and values are typed) but edits
/// the JSON tree, so every field that is not corrected stays byte-for-byte as the pipeline wrote it. The addressed
/// <see cref="FieldValue{T}"/> is deserialised, corrected through <see cref="FieldValue{T}.Correct"/> (previous value
/// and provenance go to its history) and written back.
/// </summary>
public static partial class CanonicalPatcher
{
    private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> PropertyCache = new();

    /// <summary>The domain record a document's canonical JSON holds, or null for types without canonical data.</summary>
    public static Type? RootTypeFor(DocumentType type) => type switch
    {
        DocumentType.Invoice or DocumentType.CreditNote => typeof(Invoice),
        DocumentType.PurchaseOrder => typeof(PurchaseOrder),
        DocumentType.DeliveryNote => typeof(DeliveryNote),
        _ => null,
    };

    /// <summary>
    /// Applies every correction in order and returns the new JSON. Throws <see cref="CanonicalPatchException"/> for an
    /// unknown path, an index past the end of a list, a value of the wrong shape, or an attempt to clear a field whose
    /// type has no empty value; nothing is partially applied in that case.
    /// </summary>
    /// <param name="rootType">The domain record the JSON holds (<see cref="RootTypeFor"/>).</param>
    /// <param name="canonicalJson">The stored canonical JSON.</param>
    /// <param name="corrections">What to change.</param>
    /// <param name="by">The reviewer, recorded in each field's history.</param>
    /// <param name="at">When, recorded in each field's history.</param>
    /// <param name="fallbackCurrency">Currency for a bare amount when neither the field nor the document carries one (the tenant default).</param>
    public static CanonicalPatchResult Apply(Type rootType, string canonicalJson, IReadOnlyList<FieldCorrection> corrections, string by, DateTimeOffset at, string? fallbackCurrency = null)
    {
        ArgumentNullException.ThrowIfNull(rootType);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        if (JsonNode.Parse(canonicalJson) is not JsonObject root)
        {
            throw new CanonicalPatchException(string.Empty, "The stored canonical document is not a JSON object.");
        }

        var documentCurrency = root["currency"]?["value"]?.GetValue<string>() ?? fallbackCurrency;
        var applied = new List<AppliedCorrection>(corrections.Count);
        foreach (var correction in corrections)
        {
            applied.Add(ApplyOne(rootType, root, correction, by, at, documentCurrency));
        }

        return new CanonicalPatchResult(root.ToJsonString(), applied);
    }

    private static AppliedCorrection ApplyOne(Type rootType, JsonObject root, FieldCorrection correction, string by, DateTimeOffset at, string? documentCurrency)
    {
        var path = correction.Path?.Trim() ?? string.Empty;
        var segments = ParsePath(path);
        JsonObject node = root;
        var type = rootType;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var property = FindProperty(type, segment.Name) ?? throw new CanonicalPatchException(path, $"'{segment.Name}' is not a field of {type.Name}.");
            var propertyType = property.PropertyType;
            var remaining = segments.Skip(i + 1).ToList();

            if (IsFieldValue(propertyType))
            {
                if (segment.Index is not null)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' is a single field, not a list.");
                }

                var (newNode, record) = Correct(path, propertyType, node[segment.Name], remaining, correction, by, at, documentCurrency, IsCurrencyField(property));
                node[segment.Name] = newNode;
                return record;
            }

            if (ElementTypeOf(propertyType) is { } elementType)
            {
                if (segment.Index is null)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' is a list; address an entry as {segment.Name}[n].");
                }

                if (node[segment.Name] is not JsonArray array || segment.Index.Value >= array.Count)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' has no entry at index {segment.Index.Value}.");
                }

                if (IsFieldValue(elementType))
                {
                    var (newNode, record) = Correct(path, elementType, array[segment.Index.Value], remaining, correction, by, at, documentCurrency, IsCurrencyField(property));
                    array[segment.Index.Value] = newNode;
                    return record;
                }

                node = array[segment.Index.Value] as JsonObject ?? throw new CanonicalPatchException(path, $"'{segment.Name}[{segment.Index.Value}]' is not an object.");
                type = elementType;
                continue;
            }

            if (propertyType.IsClass && propertyType != typeof(string))
            {
                if (segment.Index is not null)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' is not a list.");
                }

                node = node[segment.Name] as JsonObject ?? throw new CanonicalPatchException(path, $"'{segment.Name}' is empty; it cannot be navigated into.");
                type = propertyType;
                continue;
            }

            throw new CanonicalPatchException(path, $"'{segment.Name}' carries no provenance and cannot be corrected through this endpoint.");
        }

        throw new CanonicalPatchException(path, "The path does not address a field.");
    }

    /// <summary>Deserialises the field, applies the correction and returns the new field node plus the audit record.</summary>
    private static (JsonNode NewNode, AppliedCorrection Record) Correct(string path, Type fieldType, JsonNode? fieldNode, List<PathSegment> nested, FieldCorrection correction, string by, DateTimeOffset at, string? documentCurrency, bool currencyField)
    {
        var valueType = fieldType.GetGenericArguments()[0];
        var options = CanonicalDocumentSerializer.Options;
        object current;
        try
        {
            current = fieldNode is null
                ? Activator.CreateInstance(fieldType)!
                : JsonSerializer.Deserialize(fieldNode, fieldType, options) ?? Activator.CreateInstance(fieldType)!;
        }
        catch (JsonException ex)
        {
            throw new CanonicalPatchException(path, $"The stored field could not be read: {ex.Message}");
        }

        var oldValueNode = fieldNode?["value"];
        var newValue = nested.Count > 0
            ? NestedValue(path, valueType, oldValueNode, nested, correction.Value)
            : ConvertValue(path, valueType, correction.Value, oldValueNode, documentCurrency);
        newValue = Validate(path, newValue, currencyField);

        if (newValue is null && valueType.IsValueType && Nullable.GetUnderlyingType(valueType) is null)
        {
            throw new CanonicalPatchException(path, $"A {valueType.Name} field cannot be cleared; provide a value.");
        }

        var correct = fieldType.GetMethod(nameof(FieldValue<object>.Correct), BindingFlags.Public | BindingFlags.Instance)!;
        object corrected;
        try
        {
            corrected = correct.Invoke(current, [newValue, by, at])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new CanonicalPatchException(path, ex.InnerException.Message);
        }

        var newNode = JsonSerializer.SerializeToNode(corrected, fieldType, options)!;
        return (newNode, new AppliedCorrection(path, oldValueNode?.ToJsonString(), newNode["value"]?.ToJsonString(), correction.Reason));
    }

    /// <summary>A property inside the field's value (<c>supplier.vatId</c>): patch that property on the current value and re-read the whole value.</summary>
    private static object? NestedValue(string path, Type valueType, JsonNode? currentValueNode, List<PathSegment> nested, JsonElement? value)
    {
        if (!valueType.IsClass || valueType == typeof(string))
        {
            throw new CanonicalPatchException(path, $"'{nested[0].Name}' cannot be addressed inside a {valueType.Name} value.");
        }

        if (valueType == typeof(DocumentIdentifier))
        {
            throw new CanonicalPatchException(path, "An identifier is corrected as a whole: send the new verbatim text for the field; the normalized form is derived from it.");
        }

        var target = currentValueNode?.DeepClone() as JsonObject ?? [];
        var container = target;
        var type = valueType;
        for (var i = 0; i < nested.Count; i++)
        {
            var segment = nested[i];
            if (segment.Index is not null)
            {
                throw new CanonicalPatchException(path, "Lists inside a field value cannot be addressed by index; correct the whole field.");
            }

            var property = FindProperty(type, segment.Name) ?? throw new CanonicalPatchException(path, $"'{segment.Name}' is not a property of {type.Name}.");
            if (i == nested.Count - 1)
            {
                container[segment.Name] = ToNode(value);
                break;
            }

            if (!property.PropertyType.IsClass || property.PropertyType == typeof(string) || ElementTypeOf(property.PropertyType) is not null)
            {
                throw new CanonicalPatchException(path, $"'{segment.Name}' cannot be navigated into.");
            }

            if (container[segment.Name] is not JsonObject child)
            {
                child = [];
                container[segment.Name] = child;
            }

            container = child;
            type = property.PropertyType;
        }

        try
        {
            return JsonSerializer.Deserialize(target, valueType, CanonicalDocumentSerializer.Options);
        }
        catch (JsonException ex)
        {
            throw new CanonicalPatchException(path, $"The value is not a valid {valueType.Name}: {ex.Message}");
        }
    }

    /// <summary>The new value typed as the field expects, with the shorthands reviewers use: a bare string for an identifier, a bare number for an amount.</summary>
    private static object? ConvertValue(string path, Type valueType, JsonElement? value, JsonNode? oldValueNode, string? documentCurrency)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var element = value.Value;
        if (valueType == typeof(DocumentIdentifier))
        {
            // Whatever the shape (a bare string or {verbatim, normalized}), only the verbatim text counts: the
            // normalized form is always recomputed, so a client cannot store an inconsistent pair.
            var text = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Object when element.TryGetProperty("verbatim", out var verbatim) && verbatim.ValueKind == JsonValueKind.String => verbatim.GetString(),
                JsonValueKind.Object => null,
                _ => throw new CanonicalPatchException(path, "An identifier is a string (or an object with a verbatim property)."),
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new CanonicalPatchException(path, "An identifier cannot be blank; send null to clear the field.");
            }

            return DocumentIdentifier.Create(text);
        }

        if (valueType == typeof(Money) && element.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            if (!TryReadDecimal(element, out var amount))
            {
                throw new CanonicalPatchException(path, "The amount is not a decimal number.");
            }

            var currency = oldValueNode?["currency"]?.GetValue<string>() ?? documentCurrency;
            if (string.IsNullOrWhiteSpace(currency))
            {
                throw new CanonicalPatchException(path, "The document has no currency; send {\"amount\": …, \"currency\": \"EUR\"} instead of a bare number.");
            }

            return new Money(amount, currency);
        }

        try
        {
            return JsonSerializer.Deserialize(element, valueType, CanonicalDocumentSerializer.Options);
        }
        catch (JsonException ex)
        {
            throw new CanonicalPatchException(path, $"The value is not a valid {valueType.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks the converted value against what the stored columns accept: a currency (the document's <c>currency</c>
    /// field or <see cref="Money.Currency"/>) is normalised and must be a three-letter code, because
    /// <c>Document.Currency</c> is <c>char(3)</c> and every arithmetic check compares codes verbatim; an identifier is
    /// always rebuilt from its verbatim text.
    /// </summary>
    private static object? Validate(string path, object? value, bool currencyField)
    {
        switch (value)
        {
            case string code when currencyField:
                return NormalizeCurrency(path, code);

            case Money money:
                return money with { Currency = NormalizeCurrency(path, money.Currency) };

            case DocumentIdentifier identifier:
                if (string.IsNullOrWhiteSpace(identifier.Verbatim))
                {
                    throw new CanonicalPatchException(path, "An identifier cannot be blank; send null to clear the field.");
                }

                return DocumentIdentifier.Create(identifier.Verbatim);

            default:
                return value;
        }
    }

    private static string NormalizeCurrency(string path, string? code)
    {
        var normalized = code is null ? null : CurrencyCode.Normalize(code);
        if (!CurrencyCode.IsWellFormed(normalized))
        {
            throw new CanonicalPatchException(path, $"'{code}' is not a currency; use the three-letter ISO 4217 code (EUR, USD, …).");
        }

        return normalized!;
    }

    /// <summary>The document-level currency field (<c>Invoice.Currency</c>, <c>PurchaseOrder.Currency</c>): a <c>FieldValue&lt;string&gt;</c> named Currency.</summary>
    private static bool IsCurrencyField(PropertyInfo property) =>
        string.Equals(property.Name, "Currency", StringComparison.Ordinal);

    private static bool TryReadDecimal(JsonElement element, out decimal amount)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDecimal(out amount);
        }

        return decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private static JsonNode? ToNode(JsonElement? value) =>
        value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : JsonNode.Parse(value.Value.GetRawText());

    private static bool IsFieldValue(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FieldValue<>);

    /// <summary>The element type when the property is a list (<c>IReadOnlyList&lt;T&gt;</c> or anything implementing it), else null.</summary>
    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            return type.GetGenericArguments()[0];
        }

        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            ?.GetGenericArguments()[0];
    }

    /// <summary>
    /// Public settable (or init-only) properties keyed by their JSON name (camelCase, as
    /// <see cref="CanonicalDocumentSerializer"/> writes them). Computed get-only properties such as
    /// <c>Party.NormalizedVatId</c> are not addressable: the serialiser ignores them on input, so a correction to one
    /// would be recorded but change nothing.
    /// </summary>
    private static PropertyInfo? FindProperty(Type type, string jsonName)
    {
        var properties = PropertyCache.GetOrAdd(type, t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p, StringComparer.Ordinal));
        return properties.TryGetValue(jsonName, out var property) ? property : null;
    }

    private static List<PathSegment> ParsePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CanonicalPatchException(path, "A field path is required.");
        }

        var segments = new List<PathSegment>();
        foreach (var part in path.Split('.'))
        {
            var match = SegmentPattern().Match(part);
            if (!match.Success)
            {
                throw new CanonicalPatchException(path, $"'{part}' is not a valid path segment; expected name or name[index].");
            }

            int? index = match.Groups["index"].Success ? int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture) : null;
            segments.Add(new PathSegment(match.Groups["name"].Value, index));
        }

        return segments;
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\[(?<index>\d{1,6})\])?$")]
    private static partial Regex SegmentPattern();

    private readonly record struct PathSegment(string Name, int? Index);
}
