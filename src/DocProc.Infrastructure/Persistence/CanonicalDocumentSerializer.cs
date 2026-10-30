using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DocProc.Domain.DeliveryNotes;
using DocProc.Domain.Invoices;
using DocProc.Domain.PurchaseOrders;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// Serialises the canonical model into the Document's json column. The shape is versioned
/// (<see cref="SchemaVersion"/>) so a later change to the domain records can migrate stored documents.
/// </summary>
public static class CanonicalDocumentSerializer
{
    public const int SchemaVersion = 1;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = false,
    };

    public static string Serialize(Invoice invoice) => JsonSerializer.Serialize(invoice, Options);

    public static string Serialize(PurchaseOrder purchaseOrder) => JsonSerializer.Serialize(purchaseOrder, Options);

    public static string Serialize(DeliveryNote deliveryNote) => JsonSerializer.Serialize(deliveryNote, Options);

    public static Invoice DeserializeInvoice(string json) =>
        JsonSerializer.Deserialize<Invoice>(json, Options) ?? throw new JsonException("Canonical invoice JSON was null.");

    public static PurchaseOrder DeserializePurchaseOrder(string json) =>
        JsonSerializer.Deserialize<PurchaseOrder>(json, Options) ?? throw new JsonException("Canonical purchase order JSON was null.");

    public static DeliveryNote DeserializeDeliveryNote(string json) =>
        JsonSerializer.Deserialize<DeliveryNote>(json, Options) ?? throw new JsonException("Canonical delivery note JSON was null.");
}
