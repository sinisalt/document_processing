using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;

namespace DocProc.Llm.Extraction;

/// <summary>
/// Maps the <c>x_custom</c> object of an invoice extraction answer (docs/11 §9.1) onto the tenant's added fields: each
/// value becomes a <c>FieldValue</c> envelope (a generic leaf, <c>FieldValue&lt;JsonNode&gt;</c>) with LLM provenance
/// located in the text layer like the invoice's own fields, under the field's key, so it is stored next to the invoice
/// fields in the same tree (<see cref="Domain.Invoices.Invoice.Extra"/>). A value the model did not give, or one that
/// does not fit its kind, is left out (the field stays Missing). A period is kept as <c>{from, to}</c> with the ends
/// that are dates. Added groups and tables (<c>object</c>, <c>array</c>) and the rows and members of the template's
/// own fields are not read by the invoice call: the compiler leaves them out of <c>x_custom</c> with a note.
/// </summary>
public static class CustomFieldMapper
{
    public const string Property = DefinitionCompiler.CustomObject;

    /// <summary>The envelopes of the added fields found in <paramref name="answerJson"/>, by field key.</summary>
    public static Dictionary<string, JsonElement> Map(string answerJson, IReadOnlyList<FieldDescriptor> fields, ProvenanceLocator locator, string? currency)
    {
        ArgumentNullException.ThrowIfNull(answerJson);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(locator);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        JsonObject? custom;
        try
        {
            custom = JsonNode.Parse(answerJson)?[Property] as JsonObject;
        }
        catch (JsonException)
        {
            custom = null;
        }

        if (custom is null)
        {
            return result;
        }

        foreach (var field in fields)
        {
            if (!custom.TryGetPropertyValue(field.Key, out var raw) || raw is null)
            {
                continue;
            }

            JsonNode? envelope;
            if (field.IsList)
            {
                envelope = raw is JsonArray list
                    ? new JsonArray([.. list.Select(item => Envelope(field, item, locator, currency)).OfType<JsonNode>()])
                    : null;
            }
            else
            {
                envelope = Envelope(field, raw, locator, currency);
            }

            if (envelope is not null)
            {
                result[field.Key] = JsonSerializer.SerializeToElement(envelope, CanonicalJson.Options);
            }
        }

        return result;
    }

    private static JsonNode? Envelope(FieldDescriptor field, JsonNode? raw, ProvenanceLocator locator, string? currency)
    {
        if (raw is null)
        {
            return null;
        }

        (JsonNode Value, ProvenanceMatch Match)? mapped = field.Kind switch
        {
            FieldKinds.Identifier when Text(raw) is { } text => (JsonSerializer.SerializeToNode(DocumentIdentifier.Create(text), CanonicalJson.Options)!, locator.LocateIdentifier(text)),
            FieldKinds.String or FieldKinds.Text or FieldKinds.Enum or FieldKinds.Unit when Text(raw) is { } text => (JsonValue.Create(text), locator.LocateText(text)),
            FieldKinds.Currency when Text(raw) is { } code && CurrencyCode.IsWellFormed(CurrencyCode.Normalize(code)) => (JsonValue.Create(CurrencyCode.Normalize(code)), locator.LocateText(code)),
            FieldKinds.Integer when Number(raw) is { } n && n == decimal.Truncate(n) => (JsonValue.Create(decimal.ToInt64(n)), locator.LocateNumber(n)),
            FieldKinds.Decimal or FieldKinds.Percent when Number(raw) is { } n => (JsonValue.Create(n), locator.LocateNumber(n)),
            FieldKinds.Money when Number(raw) is { } n && currency is not null => (JsonSerializer.SerializeToNode(new Money(n, currency), CanonicalJson.Options)!, locator.LocateNumber(n)),
            FieldKinds.Date when Date(raw) is { } d => (JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), locator.LocateDate(d)),
            FieldKinds.Period when Period(raw) is { } p => (p.Value, locator.LocateDate(p.First)),
            FieldKinds.Boolean when raw is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False => (JsonValue.Create(b.GetValue<bool>()), new ProvenanceMatch(MatchQuality.NotFound, null, null, null)),
            _ when FieldKinds.IsComposite(field.Kind) && raw is JsonObject composite => (composite.DeepClone(), locator.LocateText(composite.Select(m => Text(m.Value)).FirstOrDefault(t => t is not null))),
            _ => null,
        };
        if (mapped is not { } found)
        {
            return null;
        }

        var envelope = FieldValue.Extracted<JsonNode>(found.Value, found.Match.ToSource(), found.Match.Confidence);
        return JsonSerializer.SerializeToNode(envelope, CanonicalJson.Options);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static decimal? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<decimal>(out var number) ? number : null;

    /// <summary>A period as <c>{from, to}</c> with the ends that are dates (yyyy-MM-dd), and the first of them; null when neither is.</summary>
    private static (JsonObject Value, DateOnly First)? Period(JsonNode? node)
    {
        if (node is not JsonObject period)
        {
            return null;
        }

        var value = new JsonObject();
        DateOnly? first = null;
        foreach (var end in new[] { "from", "to" })
        {
            if (Date(period[end]) is { } date)
            {
                value[end] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                first ??= date;
            }
        }

        return first is { } found ? (value, found) : null;
    }

    private static DateOnly? Date(JsonNode? node) =>
        Text(node) is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
