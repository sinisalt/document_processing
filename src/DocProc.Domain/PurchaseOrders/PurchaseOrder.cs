using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;

namespace DocProc.Domain.PurchaseOrders;

public enum PurchaseOrderStatus
{
    Open,
    PartiallyDelivered,
    Delivered,
    Invoiced,
    Cancelled,
}

public sealed record PurchaseOrderLine
{
    public required int LineNumber { get; init; }
    public FieldValue<string> SupplierSku { get; init; } = FieldValue<string>.Missing();
    public FieldValue<string> Description { get; init; } = FieldValue<string>.Missing();
    public FieldValue<decimal> QuantityOrdered { get; init; } = FieldValue<decimal>.Missing();
    public FieldValue<UnitOfMeasure> Unit { get; init; } = FieldValue<UnitOfMeasure>.Missing();
    public string? UnitRaw { get; init; }
    public FieldValue<Money> UnitPrice { get; init; } = FieldValue<Money>.Missing();
    public FieldValue<Money> LineNet { get; init; } = FieldValue<Money>.Missing();

    /// <summary>Properties of the stored JSON this record does not declare; kept across typed round trips (see <see cref="PurchaseOrder.Extra"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Purchase order issued by the tenant to a supplier (docs/02-data-model.md §4.3).</summary>
public sealed record PurchaseOrder
{
    public FieldValue<DocumentIdentifier> PoNumber { get; init; } = FieldValue<DocumentIdentifier>.Missing();
    public FieldValue<DateOnly> OrderDate { get; init; } = FieldValue<DateOnly>.Missing();
    public FieldValue<DateOnly> RequestedDeliveryDate { get; init; } = FieldValue<DateOnly>.Missing();
    public FieldValue<Party> Supplier { get; init; } = FieldValue<Party>.Missing();
    public FieldValue<Party> Buyer { get; init; } = FieldValue<Party>.Missing();
    public FieldValue<Address> DeliveryAddress { get; init; } = FieldValue<Address>.Missing();
    public FieldValue<string> Currency { get; init; } = FieldValue<string>.Missing();
    public FieldValue<Money> TotalNet { get; init; } = FieldValue<Money>.Missing();
    public FieldValue<Money> TotalGross { get; init; } = FieldValue<Money>.Missing();
    public PurchaseOrderStatus Status { get; init; } = PurchaseOrderStatus.Open;
    public IReadOnlyList<PurchaseOrderLine> Lines { get; init; } = [];

    /// <summary>
    /// Properties of the stored JSON that this record does not declare (custom fields of a tenant type, docs/11 §5.6),
    /// kept so a typed load, patch and save round trip does not drop them. Null when there are none.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
