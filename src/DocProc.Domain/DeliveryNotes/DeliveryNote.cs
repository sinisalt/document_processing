using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;

namespace DocProc.Domain.DeliveryNotes;

/// <summary>Cold-chain temperature reading printed on a delivery note.</summary>
public sealed record TemperatureReading(decimal Celsius, string? Location = null, string? RawText = null);

public sealed record DeliveryNoteLine
{
    public required int LineNumber { get; init; }
    public FieldValue<string> SupplierSku { get; init; } = FieldValue<string>.Missing();
    public FieldValue<string> Description { get; init; } = FieldValue<string>.Missing();
    public FieldValue<decimal> QuantityDelivered { get; init; } = FieldValue<decimal>.Missing();
    public FieldValue<UnitOfMeasure> Unit { get; init; } = FieldValue<UnitOfMeasure>.Missing();
    public string? UnitRaw { get; init; }
    public FieldValue<decimal> QuantityOrdered { get; init; } = FieldValue<decimal>.Missing();
    public FieldValue<string> LotNumber { get; init; } = FieldValue<string>.Missing();
    public FieldValue<DateOnly> BestBefore { get; init; } = FieldValue<DateOnly>.Missing();
    public FieldValue<decimal> BackorderQuantity { get; init; } = FieldValue<decimal>.Missing();

    /// <summary>Properties of the stored JSON this record does not declare; kept across typed round trips (see <see cref="DeliveryNote.Extra"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Delivery note / packing slip (docs/02-data-model.md §4.4).</summary>
public sealed record DeliveryNote
{
    public FieldValue<DocumentIdentifier> DeliveryNoteNumber { get; init; } = FieldValue<DocumentIdentifier>.Missing();
    public FieldValue<DateOnly> DeliveryDate { get; init; } = FieldValue<DateOnly>.Missing();
    public FieldValue<Party> Supplier { get; init; } = FieldValue<Party>.Missing();
    public FieldValue<Party> Recipient { get; init; } = FieldValue<Party>.Missing();
    public IReadOnlyList<FieldValue<DocumentIdentifier>> OrderReferences { get; init; } = [];
    public FieldValue<string> Carrier { get; init; } = FieldValue<string>.Missing();
    public FieldValue<string> SignedBy { get; init; } = FieldValue<string>.Missing();
    public IReadOnlyList<TemperatureReading> TemperatureReadings { get; init; } = [];
    public FieldValue<string> Notes { get; init; } = FieldValue<string>.Missing();
    public IReadOnlyList<DeliveryNoteLine> Lines { get; init; } = [];

    /// <summary>
    /// Properties of the stored JSON that this record does not declare (custom fields of a tenant type, docs/11 §5.6),
    /// kept so a typed load, patch and save round trip does not drop them. Null when there are none.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
