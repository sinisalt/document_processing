using System.Text.Json.Nodes;
using DocProc.Domain.Common;
using DocProc.Domain.DeliveryNotes;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.PurchaseOrders;
using DocProc.Infrastructure.Persistence;

namespace DocProc.Integration.Tests;

/// <summary>
/// docs/11 §5.6 "Round-trip safety": a property of the stored canonical JSON that the records do not declare (a
/// tenant's custom field) survives a typed deserialise and serialise instead of being dropped silently.
/// </summary>
public sealed class CanonicalRoundTripTests
{
    private static Invoice SampleInvoice() => new()
    {
        Kind = FieldValue.Extracted(InvoiceKind.Invoice),
        InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create("RE-2026-0042"), new FieldSource(SourceMethod.Llm, 1, new BoundingBox(10, 20, 30, 40), "RE-2026-0042"), 0.95),
        IssueDate = FieldValue.Extracted(new DateOnly(2026, 10, 28)),
        Currency = FieldValue.Extracted("EUR"),
        Supplier = FieldValue.Extracted(new Party("Nordwind Großhandel GmbH", VatId: "DE 811 111 111", Iban: "DE02 1203 0000 0000 2020 51")),
        TotalGross = FieldValue.Extracted(new Money(119m, "EUR")),
        Lines =
        [
            new InvoiceLine { LineNumber = 1, Description = FieldValue.Extracted("Kaffee Crema 1kg"), Quantity = FieldValue.Extracted(2m), LineNet = FieldValue.Extracted(new Money(100m, "EUR")) },
        ],
    };

    [Fact]
    public void An_invoice_without_unknown_properties_round_trips_byte_for_byte_and_has_no_extra_bag()
    {
        var json = CanonicalDocumentSerializer.Serialize(SampleInvoice());

        var read = CanonicalDocumentSerializer.DeserializeInvoice(json);

        read.Extra.ShouldBeNull();
        read.Supplier.Value!.Extra.ShouldBeNull();
        read.Lines[0].Extra.ShouldBeNull();
        CanonicalDocumentSerializer.Serialize(read).ShouldBe(json);
        json.ShouldNotContain("\"extra\"");
    }

    [Fact]
    public void Unknown_header_line_and_party_properties_survive_a_typed_round_trip()
    {
        var root = JsonNode.Parse(CanonicalDocumentSerializer.Serialize(SampleInvoice()))!.AsObject();
        root["supplierCustomerRef"] = new JsonObject { ["value"] = "123456789", ["status"] = "Extracted" };
        root["ext"] = new JsonObject { ["storeNo"] = new JsonObject { ["value"] = "0417", ["status"] = "Corrected" } };
        root["lines"]![0]!.AsObject()["ext"] = new JsonObject { ["listScope"] = "chilled" };
        root["supplier"]!["value"]!.AsObject()["erpVendorId"] = "V-10042";

        var read = CanonicalDocumentSerializer.DeserializeInvoice(root.ToJsonString());
        read.Extra.ShouldNotBeNull().ContainsKey("supplierCustomerRef").ShouldBeTrue();
        read.Lines[0].Extra.ShouldNotBeNull().ContainsKey("ext").ShouldBeTrue();
        read.Supplier.Value!.Extra.ShouldNotBeNull()["erpVendorId"].GetString().ShouldBe("V-10042");

        // A typed edit in between, as the pipeline and the projections do, keeps them too.
        var edited = read with { Notes = FieldValue.Extracted("Lieferung frei Haus") };
        var written = JsonNode.Parse(CanonicalDocumentSerializer.Serialize(edited))!.AsObject();

        written["supplierCustomerRef"]!["value"]!.GetValue<string>().ShouldBe("123456789");
        written["ext"]!["storeNo"]!["value"]!.GetValue<string>().ShouldBe("0417");
        written["lines"]![0]!["ext"]!["listScope"]!.GetValue<string>().ShouldBe("chilled");
        written["supplier"]!["value"]!["erpVendorId"]!.GetValue<string>().ShouldBe("V-10042");
        written["notes"]!["value"]!.GetValue<string>().ShouldBe("Lieferung frei Haus");
        written.ContainsKey("extra").ShouldBeFalse();
    }

    [Fact]
    public void Computed_party_properties_are_not_mistaken_for_unknown_ones()
    {
        // Party writes NormalizedVatId and NormalizedIban (get-only). Read back they must be ignored, not collected into
        // the extension bag, or the next write would emit them twice.
        var json = CanonicalDocumentSerializer.Serialize(SampleInvoice());
        json.ShouldContain("\"normalizedVatId\":\"DE811111111\"");

        var read = CanonicalDocumentSerializer.DeserializeInvoice(json);

        read.Supplier.Value!.Extra.ShouldBeNull();
        var written = CanonicalDocumentSerializer.Serialize(read);
        CountOf(written, "\"normalizedVatId\"").ShouldBe(1);
    }

    [Fact]
    public void Purchase_orders_and_delivery_notes_keep_unknown_properties_too()
    {
        var order = new PurchaseOrder { PoNumber = FieldValue.Extracted(DocumentIdentifier.Create("PO-7")), Lines = [new PurchaseOrderLine { LineNumber = 1 }] };
        var orderNode = JsonNode.Parse(CanonicalDocumentSerializer.Serialize(order))!.AsObject();
        orderNode["costCentre"] = "4711";
        orderNode["lines"]![0]!.AsObject()["projectCode"] = "P-1";
        var orderOut = JsonNode.Parse(CanonicalDocumentSerializer.Serialize(CanonicalDocumentSerializer.DeserializePurchaseOrder(orderNode.ToJsonString())))!;
        orderOut["costCentre"]!.GetValue<string>().ShouldBe("4711");
        orderOut["lines"]![0]!["projectCode"]!.GetValue<string>().ShouldBe("P-1");

        var note = new DeliveryNote { DeliveryNoteNumber = FieldValue.Extracted(DocumentIdentifier.Create("LS-9")), Lines = [new DeliveryNoteLine { LineNumber = 1 }] };
        var noteNode = JsonNode.Parse(CanonicalDocumentSerializer.Serialize(note))!.AsObject();
        noteNode["dockDoor"] = 3;
        noteNode["lines"]![0]!.AsObject()["pallet"] = "EUR-1";
        var noteOut = JsonNode.Parse(CanonicalDocumentSerializer.Serialize(CanonicalDocumentSerializer.DeserializeDeliveryNote(noteNode.ToJsonString())))!;
        noteOut["dockDoor"]!.GetValue<int>().ShouldBe(3);
        noteOut["lines"]![0]!["pallet"]!.GetValue<string>().ShouldBe("EUR-1");
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
