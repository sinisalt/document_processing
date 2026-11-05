using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Common;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// Writes <c>DocumentFieldIndex</c> (docs/11 §6.2): one row per value of every field the document's type marks
/// <c>indexed</c>, read from the stored tree like the list columns, and replaced with them whenever the tree changes
/// (the Extract stage, a reviewer's correction). Rows of fields the type no longer indexes are removed. A row field or
/// a list has one value per row; at most <see cref="MaxValuesPerField"/> of them are indexed, and at most
/// <see cref="MaxEntriesPerDocument"/> rows per document (<see cref="FieldIndexLimits"/>).
/// </summary>
public static class DocumentFieldIndexWriter
{
    /// <summary>The most values of one field a document indexes (<see cref="FieldIndexLimits.MaxValuesPerField"/>): its first ones, in row order.</summary>
    public const int MaxValuesPerField = FieldIndexLimits.MaxValuesPerField;

    /// <summary>
    /// The most index rows of one document (<see cref="FieldIndexLimits.MaxEntriesPerDocument"/>). Every field's first
    /// value comes first; the further values fill the rest, field by field in definition order.
    /// </summary>
    public const int MaxEntriesPerDocument = FieldIndexLimits.MaxEntriesPerDocument;

    /// <summary>The index rows of <paramref name="canonicalJson"/> for <paramref name="indexed"/>; a malformed tree has none.</summary>
    public static IReadOnlyList<DocumentFieldIndexEntry> Entries(Guid tenantId, Guid documentId, string? canonicalJson, IReadOnlyList<FieldDescriptor> indexed)
    {
        ArgumentNullException.ThrowIfNull(indexed);
        if (indexed.Count == 0 || string.IsNullOrWhiteSpace(canonicalJson))
        {
            return [];
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(canonicalJson) as JsonObject;
        }
        catch (JsonException)
        {
            return [];
        }

        if (root is null)
        {
            return [];
        }

        // Per field its first values (at most MaxValuesPerField; the walk stops there, whatever the row count).
        var byField = new List<List<DocumentFieldIndexEntry>>();
        foreach (var field in indexed)
        {
            if (field.Path.Length > DocumentFieldIndexEntry.MaxFieldKeyLength)
            {
                continue;
            }

            var values = new List<DocumentFieldIndexEntry>();
            foreach (var envelope in Envelopes(root, field))
            {
                if (Entry(tenantId, documentId, field, values.Count, DefinitionTree.ValueOf(envelope)) is { } entry)
                {
                    values.Add(entry);
                    if (values.Count == MaxValuesPerField)
                    {
                        break;
                    }
                }
            }

            byField.Add(values);
        }

        // Every field's first value before any field's second: the inbox column and the duplicate check read ordinal 0.
        var entries = new List<DocumentFieldIndexEntry>();
        entries.AddRange(byField.Where(v => v.Count > 0).Select(v => v[0]).Take(MaxEntriesPerDocument));
        foreach (var values in byField)
        {
            var room = MaxEntriesPerDocument - entries.Count;
            if (room <= 0)
            {
                break;
            }

            entries.AddRange(values.Skip(1).Take(room));
        }

        return entries;
    }

    /// <summary>
    /// Replaces the document's index rows with the ones its tree gives now: rows that stay are updated in place, the
    /// others removed or added, so the save that writes the tree writes the index with it.
    /// </summary>
    public static async Task RefreshAsync(DocProcDbContext db, Document document, IReadOnlyList<FieldDescriptor> indexed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(indexed);
        var wanted = Entries(document.TenantId, document.Id, document.CanonicalJson, indexed);
        var existing = await db.DocumentFieldIndex.Where(e => e.DocumentId == document.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byKey = existing.ToDictionary(e => (e.FieldKey, e.Ordinal));
        foreach (var entry in wanted)
        {
            if (byKey.Remove((entry.FieldKey, entry.Ordinal), out var row))
            {
                row.StringValue = entry.StringValue;
                row.NumberValue = entry.NumberValue;
                row.DateValue = entry.DateValue;
            }
            else
            {
                db.DocumentFieldIndex.Add(entry);
            }
        }

        db.DocumentFieldIndex.RemoveRange(byKey.Values);
    }

    /// <summary>The normalised string form an index row holds and a filter compares with.</summary>
    public static string Normalize(string value) => Truncate(Identifiers.Normalize(value), DocumentFieldIndexEntry.MaxStringLength);

    private static IEnumerable<JsonNode?> Envelopes(JsonObject root, FieldDescriptor field)
    {
        IEnumerable<JsonNode?> current = [root];
        var segments = field.Path.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var many = segment.EndsWith("[*]", StringComparison.Ordinal);
            var key = many ? segment[..^3] : segment;
            current = current.Select(n => n is JsonObject obj ? obj[key] : null);
            if (many)
            {
                current = current.SelectMany(n => n is JsonArray rows ? rows.AsEnumerable() : []);
            }
        }

        return field.IsList ? current.SelectMany(n => n is JsonArray list ? list.AsEnumerable() : []) : current;
    }

    private static DocumentFieldIndexEntry? Entry(Guid tenantId, Guid documentId, FieldDescriptor field, int ordinal, JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        string? text = null;
        decimal? number = null;
        DateOnly? date = null;
        switch (field.Kind)
        {
            case FieldKinds.Identifier:
                text = value is JsonObject identifier
                    ? Text(identifier["normalized"]) ?? (Text(identifier["verbatim"]) is { } verbatim ? Identifiers.Normalize(verbatim) : null)
                    : Text(value) is { } plain ? Identifiers.Normalize(plain) : null;
                break;
            case FieldKinds.Date:
                date = Text(value) is { } d && DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
                break;
            case FieldKinds.Integer or FieldKinds.Decimal or FieldKinds.Percent:
                number = Number(value);
                break;
            case FieldKinds.Money:
                number = value is JsonObject money ? Number(money["amount"]) : Number(value);
                break;
            case FieldKinds.String or FieldKinds.Text or FieldKinds.Enum or FieldKinds.Unit or FieldKinds.Currency:
                text = Text(value) is { } t ? Identifiers.Normalize(t) : null;
                break;
            default:
                return null;
        }

        if (text is null && number is null && date is null)
        {
            return null;
        }

        return new DocumentFieldIndexEntry
        {
            TenantId = tenantId,
            DocumentId = documentId,
            FieldKey = field.Path,
            Ordinal = ordinal,
            StringValue = text is null ? null : Truncate(text, DocumentFieldIndexEntry.MaxStringLength),
            NumberValue = number,
            DateValue = date,
        };
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// The bound (exclusive) on the absolute value of an indexed number: well inside the column's decimal(28,8), so a
    /// stored value and a filter value compared with it never overflow the column on SQL Server.
    /// </summary>
    public const decimal MaxIndexedNumber = 100_000_000_000_000_000m;

    /// <summary>Whether <paramref name="number"/> can be an indexed number (and so whether a filter needs to compare it as one).</summary>
    public static bool IsIndexableNumber(decimal number) => Math.Abs(number) < MaxIndexedNumber;

    private static decimal? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<decimal>(out var number) && IsIndexableNumber(number) ? number : null;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
