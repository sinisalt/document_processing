using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Documents;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Infrastructure.Persistence;

namespace DocProc.Api.Tests;

public sealed class CanonicalPatcherTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);

    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value, CanonicalDocumentSerializer.Options);

    private static CanonicalPatchResult Apply(string json, params FieldCorrection[] corrections) =>
        CanonicalPatcher.Apply(typeof(Invoice), json, corrections, "anna", At);

    [Fact]
    public void Header_correction_keeps_the_previous_value_and_provenance_in_the_history()
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());

        var result = Apply(json, new FieldCorrection("issueDate", Value(new DateOnly(2026, 10, 28)), "misread"));

        var invoice = CanonicalDocumentSerializer.DeserializeInvoice(result.Json);
        invoice.IssueDate.Value.ShouldBe(new DateOnly(2026, 10, 28));
        invoice.IssueDate.Status.ShouldBe(FieldStatus.Corrected);
        invoice.IssueDate.Confidence.ShouldBe(1);
        invoice.IssueDate.Source.ShouldBe(FieldSource.Human);
        var previous = invoice.IssueDate.History.ShouldHaveSingleItem();
        previous.Value.ShouldBe(new DateOnly(2026, 10, 27));
        previous.Status.ShouldBe(FieldStatus.Extracted);
        previous.Confidence.ShouldBe(0.97);
        previous.Source!.Method.ShouldBe(SourceMethod.Llm);
        previous.By.ShouldBe("anna");
        previous.At.ShouldBe(At);

        var applied = result.Applied.ShouldHaveSingleItem();
        applied.Path.ShouldBe("issueDate");
        applied.OldValueJson.ShouldBe("\"2026-10-27\"");
        applied.NewValueJson.ShouldBe("\"2026-10-28\"");
        applied.Reason.ShouldBe("misread");
    }

    [Fact]
    public void Everything_that_is_not_corrected_stays_byte_for_byte()
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());

        var result = Apply(json, new FieldCorrection("lines[0].quantity", Value(11m), null));

        var before = JsonNode.Parse(json)!.AsObject();
        var after = JsonNode.Parse(result.Json)!.AsObject();
        foreach (var property in before)
        {
            if (property.Key != "lines")
            {
                after[property.Key]!.ToJsonString().ShouldBe(property.Value!.ToJsonString(), property.Key);
            }
        }

        after["lines"]![1]!.ToJsonString().ShouldBe(before["lines"]![1]!.ToJsonString());
        foreach (var field in before["lines"]![0]!.AsObject())
        {
            if (field.Key != "quantity")
            {
                after["lines"]![0]![field.Key]!.ToJsonString().ShouldBe(field.Value!.ToJsonString(), field.Key);
            }
        }

        after["lines"]![0]!["quantity"]!["value"]!.GetValue<decimal>().ShouldBe(11m);
    }

    [Fact]
    public void Shorthands_a_string_for_an_identifier_and_a_number_for_an_amount()
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());

        var result = Apply(
            json,
            new FieldCorrection("invoiceNumber", Value("re 2026/0099"), null),
            new FieldCorrection("lines[1].unitPrice", Value(12.5m), null),
            new FieldCorrection("amountDue", Value("150.00"), null),
            new FieldCorrection("rounding", Value(new Money(0.01m, "USD")), null));

        var invoice = CanonicalDocumentSerializer.DeserializeInvoice(result.Json);
        invoice.InvoiceNumber.Value!.Verbatim.ShouldBe("re 2026/0099");
        invoice.InvoiceNumber.Value.Normalized.ShouldBe("RE 2026/0099");
        invoice.Lines[1].UnitPrice.Value.ShouldBe(new Money(12.5m, "EUR"), "currency of the existing value");
        invoice.AmountDue.Value.ShouldBe(new Money(150.00m, "EUR"), "a Missing amount takes the document currency");
        invoice.AmountDue.Status.ShouldBe(FieldStatus.Corrected);
        invoice.Rounding.Value.ShouldBe(new Money(0.01m, "USD"), "an explicit Money object is taken as is");
    }

    [Fact]
    public void A_property_inside_a_value_is_patched_and_the_whole_field_is_corrected()
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());

        var result = Apply(
            json,
            new FieldCorrection("supplier.address.city", Value("Düsseldorf"), null),
            new FieldCorrection("customer.name", Value("Gasthaus zur Post"), null),
            new FieldCorrection("paymentTerms.iban", Value("DE89 3704 0044 0532 0130 00"), null));

        var invoice = CanonicalDocumentSerializer.DeserializeInvoice(result.Json);
        invoice.Supplier.Value.ShouldBe(new Party("METRO Deutschland GmbH", VatId: "DE811177936", Address: new Address(City: "Düsseldorf")));
        invoice.Supplier.Status.ShouldBe(FieldStatus.Corrected);
        invoice.Supplier.History.ShouldHaveSingleItem().Value!.Address.ShouldBeNull();
        invoice.Customer.Value!.Name.ShouldBe("Gasthaus zur Post");
        invoice.Customer.Value.VatId.ShouldBe("DE 123 456 789", "siblings are kept");
        invoice.PaymentTerms.Value.ShouldBe(new PaymentTerms(Iban: "DE89 3704 0044 0532 0130 00"), "a Missing field gets a fresh value");
        result.Applied.Select(a => a.Path).ShouldBe(["supplier.address.city", "customer.name", "paymentTerms.iban"]);
    }

    [Fact]
    public void Null_clears_a_reference_field_and_corrections_apply_in_order()
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());

        var result = Apply(
            json,
            new FieldCorrection("notes", Value("call before delivery"), null),
            new FieldCorrection("notes", null, "typed in the wrong field"),
            new FieldCorrection("dueDate", Value(new DateOnly(2026, 12, 1)), null));

        var invoice = CanonicalDocumentSerializer.DeserializeInvoice(result.Json);
        invoice.Notes.Value.ShouldBeNull();
        invoice.Notes.Status.ShouldBe(FieldStatus.Corrected);
        invoice.Notes.History.Count.ShouldBe(2);
        invoice.Notes.History[1].Value.ShouldBe("call before delivery");
        invoice.DueDate.Value.ShouldBe(new DateOnly(2026, 12, 1));
    }

    [Fact]
    public void List_fields_of_field_values_are_addressed_by_index()
    {
        var invoice = TestInvoices.Valid() with { OrderReferences = [FieldValue.Extracted(DocumentIdentifier.Create("PO-1"), new FieldSource(SourceMethod.Llm), 0.9)] };
        var json = CanonicalDocumentSerializer.Serialize(invoice);

        var result = Apply(json, new FieldCorrection("orderReferences[0]", Value("PO-0001"), null));

        var corrected = CanonicalDocumentSerializer.DeserializeInvoice(result.Json);
        corrected.OrderReferences.ShouldHaveSingleItem().Value!.Verbatim.ShouldBe("PO-0001");
        corrected.OrderReferences[0].History.ShouldHaveSingleItem().Value!.Verbatim.ShouldBe("PO-1");

        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("orderReferences[1]", Value("PO-2"), null))).Message.ShouldContain("index 1");
        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("orderReferences", Value("PO-2"), null))).Message.ShouldContain("[n]");
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("invoiceNumbr", "not a field")]
    [InlineData("lines[7].quantity", "index 7")]
    [InlineData("lines.quantity", "list")]
    [InlineData("lines[0]", "does not address a field")]
    [InlineData("lines[0].unitRaw", "no provenance")]
    [InlineData("lines[0].lineNumber", "no provenance")]
    [InlineData("taxBreakdown[0].rate", "no provenance")]
    [InlineData("invoiceNumber[0]", "single field")]
    [InlineData("currency.code", "cannot be addressed inside")]
    [InlineData("supplier.vatid", "not a property")]
    [InlineData("supplier.normalizedVatId", "not a property")]
    [InlineData("supplier.normalizedIban", "not a property")]
    [InlineData("invoiceNumber.normalized", "corrected as a whole")]
    [InlineData("invoiceNumber.verbatim", "corrected as a whole")]
    [InlineData("lines[0]quantity", "not a valid path segment")]
    public void Unknown_or_unaddressable_paths_are_refused_with_the_path(string path, string expected)
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());

        var ex = Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection(path, Value("x"), null)));

        ex.Path.ShouldBe(path);
        ex.Message.ShouldContain(expected);
    }

    [Fact]
    public void Values_of_the_wrong_shape_are_refused()
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());

        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("issueDate", Value("yesterday"), null))).Message.ShouldContain("DateOnly");
        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("lines[0].quantity", Value("many"), null))).Message.ShouldContain("Decimal");
        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("lines[0].quantity", null, null))).Message.ShouldContain("cannot be cleared");
        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("invoiceNumber", Value("   "), null))).Message.ShouldContain("blank");
        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("kind", Value("Receipt"), null))).Message.ShouldContain("InvoiceKind");

        var noCurrency = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid() with { Currency = FieldValue<string>.Missing() });
        Should.Throw<CanonicalPatchException>(() => CanonicalPatcher.Apply(typeof(Invoice), noCurrency, [new FieldCorrection("amountDue", Value(1m), null)], "anna", At)).Message.ShouldContain("currency");
        CanonicalDocumentSerializer.DeserializeInvoice(CanonicalPatcher.Apply(typeof(Invoice), noCurrency, [new FieldCorrection("amountDue", Value(1m), null)], "anna", At, fallbackCurrency: "HRK").Json)
            .AmountDue.Value.ShouldBe(new Money(1m, "HRK"), "the tenant default is the last resort");
    }

    [Fact]
    public void Computed_properties_are_not_addressable_so_a_correction_never_becomes_a_recorded_no_op()
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());

        var ex = Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("supplier.normalizedVatId", Value("DE811177936"), null)));

        ex.Path.ShouldBe("supplier.normalizedVatId");
        ex.Message.ShouldContain("not a property");

        // The real property next to it still works, and the computed one follows it.
        var result = Apply(json, new FieldCorrection("supplier.vatId", Value("DE 811 177 937"), null));
        var invoice = CanonicalDocumentSerializer.DeserializeInvoice(result.Json);
        invoice.Supplier.Value!.VatId.ShouldBe("DE 811 177 937");
        invoice.Supplier.Value.NormalizedVatId.ShouldBe("DE811177937");
    }

    [Fact]
    public void Identifiers_are_rebuilt_from_the_verbatim_text_whatever_the_input_shape()
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());

        var asObject = Apply(json, new FieldCorrection("invoiceNumber", Value(new { verbatim = "re 2026/0100", normalized = "SOMETHING ELSE" }), null));
        var invoice = CanonicalDocumentSerializer.DeserializeInvoice(asObject.Json);
        invoice.InvoiceNumber.Value!.Verbatim.ShouldBe("re 2026/0100");
        invoice.InvoiceNumber.Value.Normalized.ShouldBe("RE 2026/0100", "the normalized form is derived, never taken from the client");

        var asString = Apply(json, new FieldCorrection("invoiceNumber", Value("re 2026/0100"), null));
        CanonicalDocumentSerializer.DeserializeInvoice(asString.Json).InvoiceNumber.Value.ShouldBe(invoice.InvoiceNumber.Value);

        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("invoiceNumber", Value(new { verbatim = "   " }), null))).Message.ShouldContain("blank");
        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("invoiceNumber", Value(new { normalized = "X" }), null))).Message.ShouldContain("blank");
        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("invoiceNumber", Value(42), null))).Message.ShouldContain("string");
        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("orderReferences[0]", Value(new { normalized = "PO-1" }), null)))
            .Path.ShouldBe("orderReferences[0]");
    }

    [Fact]
    public void Currency_codes_are_normalised_and_anything_but_a_three_letter_code_is_refused()
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());

        var result = Apply(
            json,
            new FieldCorrection("currency", Value(" usd "), null),
            new FieldCorrection("totalGross", Value(new { amount = 166.50m, currency = "usd" }), null),
            new FieldCorrection("lines[0].unitPrice", Value(new Money(10m, "chf")), null));

        var invoice = CanonicalDocumentSerializer.DeserializeInvoice(result.Json);
        invoice.Currency.Value.ShouldBe("USD");
        invoice.TotalGross.Value.ShouldBe(new Money(166.50m, "USD"));
        invoice.Lines[0].UnitPrice.Value.ShouldBe(new Money(10m, "CHF"));
        result.Applied[0].NewValueJson.ShouldBe("\"USD\"");

        foreach (var (path, value) in new (string, object)[]
        {
            ("currency", "EURO"),
            ("currency", "E"),
            ("currency", "12E"),
            ("totalGross", new { amount = 1, currency = "EURO" }),
            ("totalGross", new { amount = 1, currency = "" }),
            ("lines[1].unitPrice", new { amount = 1, currency = "Euro€" }),
        })
        {
            var ex = Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection(path, Value(value), null)), path);
            ex.Path.ShouldBe(path);
            ex.Message.ShouldContain("ISO 4217");
        }

        // Money is a value type: its currency is corrected with the amount, never on its own.
        Should.Throw<CanonicalPatchException>(() => Apply(json, new FieldCorrection("totalGross.currency", Value("EURO"), null))).Message.ShouldContain("cannot be addressed inside");

        // Nothing else is a currency: a note that happens to be long is a note.
        CanonicalDocumentSerializer.DeserializeInvoice(Apply(json, new FieldCorrection("notes", Value("EURO"), null)).Json).Notes.Value.ShouldBe("EURO");
    }

    [Fact]
    public void Root_types_follow_the_document_type()
    {
        CanonicalPatcher.RootTypeFor(DocumentType.Invoice).ShouldBe(typeof(Invoice));
        CanonicalPatcher.RootTypeFor(DocumentType.CreditNote).ShouldBe(typeof(Invoice));
        CanonicalPatcher.RootTypeFor(DocumentType.PurchaseOrder).ShouldBe(typeof(DocProc.Domain.PurchaseOrders.PurchaseOrder));
        CanonicalPatcher.RootTypeFor(DocumentType.DeliveryNote).ShouldBe(typeof(DocProc.Domain.DeliveryNotes.DeliveryNote));
        CanonicalPatcher.RootTypeFor(DocumentType.Statement).ShouldBeNull();
        CanonicalPatcher.RootTypeFor(DocumentType.Unknown).ShouldBeNull();
    }
}
