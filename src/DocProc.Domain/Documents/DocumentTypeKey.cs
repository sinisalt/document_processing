using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DocProc.Domain.Documents;

/// <summary>
/// The key of a document type (docs/11 §6.2): a tenant's type is data, so its key is a string, not an enum member.
/// The legacy types keep their names as keys (<see cref="WellKnownTypeKeys"/>), so every stored <c>Documents.Type</c>
/// value is already a valid key and the API's <c>type</c> values do not change.
/// <para>
/// The default value is <see cref="Unknown"/>, the reserved key of a document whose type is not decided. Keys compare
/// ordinally (a tenant can have <c>price-list</c> and <c>Price-List</c>, though it should not); reading a key from the
/// API accepts the well-known names in any case, as the enum did (<see cref="Parse"/>).
/// </para>
/// </summary>
[JsonConverter(typeof(DocumentTypeKeyJsonConverter))]
public readonly partial record struct DocumentTypeKey : IComparable<DocumentTypeKey>
{
    /// <summary>The longest key (the <c>Type nvarchar(64)</c> column).</summary>
    public const int MaxLength = 64;

    private readonly string? _value;

    /// <summary>A key with the value <paramref name="value"/>; throws when it is not of the key syntax (<see cref="IsValid"/>).</summary>
    public DocumentTypeKey(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"'{value}' is not a valid document type key (a letter, then letters, digits, '_', '.' or '-', at most {MaxLength}).", nameof(value));
        }

        _value = value;
    }

    public static DocumentTypeKey Unknown => default;

    public static DocumentTypeKey Invoice { get; } = new(WellKnownTypeKeys.Invoice);

    public static DocumentTypeKey CreditNote { get; } = new(WellKnownTypeKeys.CreditNote);

    public static DocumentTypeKey PurchaseOrder { get; } = new(WellKnownTypeKeys.PurchaseOrder);

    public static DocumentTypeKey DeliveryNote { get; } = new(WellKnownTypeKeys.DeliveryNote);

    public static DocumentTypeKey Statement { get; } = new(WellKnownTypeKeys.Statement);

    public static DocumentTypeKey Reminder { get; } = new(WellKnownTypeKeys.Reminder);

    public static DocumentTypeKey Other { get; } = new(WellKnownTypeKeys.Other);

    /// <summary>The legacy types in the order of the former enum, <see cref="Unknown"/> first.</summary>
    public static IReadOnlyList<DocumentTypeKey> Legacy { get; } = [.. WellKnownTypeKeys.Legacy.Select(k => new DocumentTypeKey(k))];

    /// <summary>The key as stored and sent; never null.</summary>
    public string Value => _value ?? WellKnownTypeKeys.Unknown;

    /// <summary>Whether this is the reserved key of an undecided type.</summary>
    public bool IsUnknown => string.Equals(Value, WellKnownTypeKeys.Unknown, StringComparison.Ordinal);

    /// <summary>Whether this is one of the legacy keys, the former enum members (<see cref="Unknown"/> included).</summary>
    public bool IsLegacy => WellKnownTypeKeys.Legacy.Contains(Value, StringComparer.Ordinal);

    /// <summary>
    /// The coded invoice family (<c>Invoice</c>, <c>CreditNote</c>): the types the invoice extractors, the invoice
    /// projection and the coded invoice rules serve.
    /// </summary>
    public bool IsInvoiceFamily => Value is WellKnownTypeKeys.Invoice or WellKnownTypeKeys.CreditNote;

    /// <summary>Whether <paramref name="value"/> has the key syntax <c>^[A-Za-z][A-Za-z0-9_.-]{0,63}$</c> (docs/11 §5.7).</summary>
    public static bool IsValid([NotNullWhen(true)] string? value) => value is not null && KeyPattern().IsMatch(value);

    /// <summary>
    /// The key <paramref name="value"/> names: a well-known name in any case becomes that name (<c>invoice</c> is
    /// <see cref="Invoice"/>, as the enum parsed it), any other valid key is taken as it is. False for an invalid key.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out DocumentTypeKey key)
    {
        key = default;
        if (!IsValid(value))
        {
            return false;
        }

        var known = WellKnownTypeKeys.Legacy.FirstOrDefault(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase));
        key = new DocumentTypeKey(known ?? value);
        return true;
    }

    /// <summary><see cref="TryParse"/>, throwing <see cref="FormatException"/> for an invalid key.</summary>
    public static DocumentTypeKey Parse(string value) =>
        TryParse(value, out var key) ? key : throw new FormatException($"'{value}' is not a valid document type key.");

    /// <summary>A key read from storage, where it was validated when written; no case folding.</summary>
    public static DocumentTypeKey FromStored(string? value) => string.IsNullOrEmpty(value) ? Unknown : new DocumentTypeKey(value);

    public bool Equals(DocumentTypeKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(DocumentTypeKey other) => string.CompareOrdinal(Value, other.Value);

    public static bool operator <(DocumentTypeKey left, DocumentTypeKey right) => left.CompareTo(right) < 0;

    public static bool operator <=(DocumentTypeKey left, DocumentTypeKey right) => left.CompareTo(right) <= 0;

    public static bool operator >(DocumentTypeKey left, DocumentTypeKey right) => left.CompareTo(right) > 0;

    public static bool operator >=(DocumentTypeKey left, DocumentTypeKey right) => left.CompareTo(right) >= 0;

    /// <summary>Test data and tables: <c>(DocumentTypeKey)"Invoice"</c>; the same check as the constructor.</summary>
    public static explicit operator DocumentTypeKey(string value) => new(value);

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>
/// The keys of the legacy types (docs/11 §6.2): the former <c>DocumentType</c> enum members, which every existing
/// tenant adopts the standard templates under (§21 step 2). <see cref="Unknown"/> stays reserved.
/// </summary>
public static class WellKnownTypeKeys
{
    public const string Unknown = "Unknown";
    public const string Invoice = "Invoice";
    public const string CreditNote = "CreditNote";
    public const string PurchaseOrder = "PurchaseOrder";
    public const string DeliveryNote = "DeliveryNote";
    public const string Statement = "Statement";
    public const string Reminder = "Reminder";
    public const string Other = "Other";

    /// <summary>Every legacy key, in the order of the former enum.</summary>
    public static IReadOnlyList<string> Legacy { get; } = [Unknown, Invoice, CreditNote, PurchaseOrder, DeliveryNote, Statement, Reminder, Other];
}

/// <summary>Writes a key as its string; reads one with <see cref="DocumentTypeKey.TryParse"/> (well-known names in any case).</summary>
public sealed class DocumentTypeKeyJsonConverter : JsonConverter<DocumentTypeKey>
{
    public override DocumentTypeKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A document type key must be a string.");
        }

        return DocumentTypeKey.TryParse(reader.GetString(), out var key)
            ? key
            : throw new JsonException($"'{reader.GetString()}' is not a valid document type key.");
    }

    public override void Write(Utf8JsonWriter writer, DocumentTypeKey value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }

    public override DocumentTypeKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DocumentTypeKey.TryParse(reader.GetString(), out var key)
            ? key
            : throw new JsonException($"'{reader.GetString()}' is not a valid document type key.");

    public override void WriteAsPropertyName(Utf8JsonWriter writer, DocumentTypeKey value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.Value);
    }
}
