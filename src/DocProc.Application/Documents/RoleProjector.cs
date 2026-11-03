using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;

namespace DocProc.Application.Documents;

/// <summary>
/// Which canonical field plays each summary role of a document type (docs/11 §6.2): the field the inbox shows as the
/// document number, its date, its currency and its total. A null path means the type does not bind that role, and the
/// matching list column is left alone. Paths are canonical field paths, dotted for nested objects.
/// <see cref="ValidFrom"/> and <see cref="ValidUntil"/> fill the validity columns of documents with a validity period
/// (price lists, docs/11 §5.8); <see cref="LineItems"/> names the repeating group whose row count is the
/// <c>LineCount</c> column (§6.2).
/// </summary>
public sealed record RoleBindings(string? DocumentNumber, string? DocumentDate, string? Currency, string? Total, string? ValidFrom = null, string? ValidUntil = null, string? LineItems = null)
{
    public static readonly RoleBindings Invoice = new("invoiceNumber", "issueDate", "currency", "totalGross", LineItems: "lines");

    public static readonly RoleBindings PurchaseOrder = new("poNumber", "orderDate", "currency", "totalGross", LineItems: "lines");

    public static readonly RoleBindings DeliveryNote = new("deliveryNoteNumber", "deliveryDate", Currency: null, Total: null, LineItems: "lines");

    /// <summary>
    /// The bindings of the coded types, until definitions carry their own (P1): the same fields
    /// <c>ExtractHandler</c> and the corrections endpoint filled before the projector replaced both. Null for types
    /// without canonical data, whose columns are never touched.
    /// </summary>
    public static RoleBindings? ForLegacyType(DocumentTypeKey type) => type.Value switch
    {
        WellKnownTypeKeys.Invoice or WellKnownTypeKeys.CreditNote => Invoice,
        WellKnownTypeKeys.PurchaseOrder => PurchaseOrder,
        WellKnownTypeKeys.DeliveryNote => DeliveryNote,
        _ => null,
    };
}

/// <summary>
/// The role values read from one canonical tree: each is null when its role is unbound (see <see cref="Bindings"/>)
/// or when the bound field has no usable value (Missing, Unreadable, empty or unreadable JSON).
/// </summary>
public sealed record RoleProjection(RoleBindings Bindings, DocumentIdentifier? DocumentNumber, DateOnly? DocumentDate, string? Currency, decimal? Total, DateOnly? ValidFrom = null, DateOnly? ValidUntil = null, int? LineCount = null);

/// <summary>
/// The single projector from a canonical tree to the denormalised summary columns (docs/11 §6.2). It replaces the two
/// copies that existed before (<c>ExtractHandler.Apply</c> and <c>DocumentEndpoints.RefreshDenormalizedColumns</c>),
/// so the pipeline and a reviewer's correction can never fill the inbox columns differently. It reads the stored JSON
/// tree rather than a typed record, so it works unchanged once types are data and trees are generic; it never throws
/// on a malformed leaf, it reads it as empty.
/// </summary>
public static class RoleProjector
{
    /// <summary>The roles of <paramref name="canonical"/> under <paramref name="bindings"/>.</summary>
    public static RoleProjection Project(RoleBindings bindings, JsonObject? canonical)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return new RoleProjection(
            bindings,
            ReadIdentifier(Leaf(canonical, bindings.DocumentNumber)),
            ReadDate(Leaf(canonical, bindings.DocumentDate)),
            ReadCurrency(Leaf(canonical, bindings.Currency)),
            ReadAmount(Leaf(canonical, bindings.Total)),
            ReadDate(Leaf(canonical, bindings.ValidFrom)),
            ReadDate(Leaf(canonical, bindings.ValidUntil)),
            RowCount(canonical, bindings.LineItems));
    }

    /// <summary>The number of rows of the repeating group at <paramref name="path"/>; null when unbound or not an array.</summary>
    private static int? RowCount(JsonObject? root, string? path)
    {
        if (root is null || string.IsNullOrEmpty(path))
        {
            return null;
        }

        JsonNode? node = root;
        foreach (var segment in path.Split('.'))
        {
            node = node is JsonObject obj && obj.TryGetPropertyValue(segment, out var child) ? child : null;
        }

        return node is JsonArray rows ? rows.Count : null;
    }

    /// <summary>The roles of a stored canonical JSON string; an empty or unparseable string projects every bound role as null.</summary>
    public static RoleProjection Project(RoleBindings bindings, string? canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        JsonObject? root = null;
        if (!string.IsNullOrWhiteSpace(canonicalJson))
        {
            try
            {
                root = JsonNode.Parse(canonicalJson) as JsonObject;
            }
            catch (JsonException)
            {
                root = null;
            }
        }

        return Project(bindings, root);
    }

    /// <summary>
    /// The value of the <see cref="FieldValue{T}"/> envelope at <paramref name="path"/>, or null when the field is
    /// absent, Missing or Unreadable (the same rule as <see cref="FieldValue{T}.HasValue"/>).
    /// </summary>
    private static JsonNode? Leaf(JsonObject? root, string? path)
    {
        if (root is null || string.IsNullOrEmpty(path))
        {
            return null;
        }

        JsonNode? node = root;
        foreach (var segment in path.Split('.'))
        {
            node = node is JsonObject obj && obj.TryGetPropertyValue(segment, out var child) ? child : null;
            if (node is null)
            {
                return null;
            }
        }

        if (node is not JsonObject envelope)
        {
            return null;
        }

        var status = FieldStatus.Missing;
        if (envelope.TryGetPropertyValue("status", out var statusNode)
            && statusNode is JsonValue statusValue
            && statusValue.TryGetValue<string>(out var statusText)
            && Enum.TryParse<FieldStatus>(statusText, ignoreCase: true, out var parsed))
        {
            status = parsed;
        }

        if (status is FieldStatus.Missing or FieldStatus.Unreadable)
        {
            return null;
        }

        return envelope.TryGetPropertyValue("value", out var value) ? value : null;
    }

    private static DocumentIdentifier? ReadIdentifier(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject identifier when Text(identifier["verbatim"]) is { } verbatim:
                return new DocumentIdentifier(verbatim, Text(identifier["normalized"]) ?? Identifiers.Normalize(verbatim));

            case JsonValue text when Text(text) is { } verbatim && !string.IsNullOrWhiteSpace(verbatim):
                return DocumentIdentifier.Create(verbatim);

            default:
                return null;
        }
    }

    private static DateOnly? ReadDate(JsonNode? value) =>
        Text(value) is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string? ReadCurrency(JsonNode? value) => Text(value);

    private static decimal? ReadAmount(JsonNode? value) => value switch
    {
        JsonObject money => Number(money["amount"]),
        JsonValue => Number(value),
        _ => null,
    };

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static decimal? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<decimal>(out var amount) ? amount : null;
}
