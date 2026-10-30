using DocProc.Domain.Common;
using DocProc.Domain.Invoices;
using static DocProc.EInvoice.Tests.FieldAssertions;

namespace DocProc.EInvoice.Tests;

/// <summary>XRechnung UBL: Leitweg-ID in BuyerReference, German tax number under scheme FC, a document allowance, a prefixed root.</summary>
public class XRechnungMappingTests
{
    private static readonly Invoice Invoice = Fixtures.Parse(Fixtures.XRechnungUbl);

    [Fact]
    public void Header()
    {
        Invoice.Kind.ShouldBeExtracted(InvoiceKind.Invoice);
        Invoice.InvoiceNumber.ShouldBeExtracted(DocumentIdentifier.Create("2026/4471"));
        Invoice.InvoiceNumber.Value!.Normalized.ShouldBe("2026/4471");
        Invoice.IssueDate.ShouldBeExtracted(new DateOnly(2026, 10, 29));
        Invoice.DueDate.ShouldBeExtracted(new DateOnly(2026, 11, 28));
        Invoice.DeliveryDate.ShouldBeExtracted(new DateOnly(2026, 10, 29));
        Invoice.Currency.ShouldBeExtracted("EUR");
    }

    [Fact]
    public void Leitweg_id_and_order_number_are_both_order_references()
    {
        Invoice.OrderReferences.Select(r => r.Value!.Verbatim).ShouldBe(["04011000-12345-67", "B-2026-3310"]);
    }

    [Fact]
    public void Several_notes_are_joined_line_by_line()
    {
        Invoice.Notes.ShouldBeExtracted("Getränkelieferung KW 44.\nLeergut wird separat abgerechnet.");
    }

    [Fact]
    public void Supplier_has_both_vat_id_and_german_tax_number()
    {
        Invoice.Supplier.ShouldBeExtracted(new Party(
            Name: "Getränke Huber GmbH & Co. KG",
            VatId: "DE276543210",
            TaxId: "143/167/89012",
            RegistrationNumber: "HRA 98765",
            Address: new Address("Landsberger Straße 410", null, "81241", "München", "Bayern", "DE"),
            Email: "faktura@getraenke-huber.example",
            Phone: "+49 89 555 0199",
            Iban: "DE75512108001245126199",
            Bic: "SOGEDEFFXXX"));
    }

    [Fact]
    public void Customer_contact_email_wins_over_the_endpoint()
    {
        var customer = Invoice.Customer.ShouldBeExtracted(Invoice.Customer.Value!);
        customer.Name.ShouldBe("Gasthaus zur Post GmbH");
        customer.VatId.ShouldBe("DE123456789");
        customer.Email.ShouldBe("einkauf@gasthaus-zur-post.example");
    }

    [Fact]
    public void Document_allowance_and_totals()
    {
        Invoice.Allowances.ShouldBe([new ChargeOrAllowance("Treuerabatt", Eur(10.00m), 19m, IsCharge: false)]);
        Invoice.Charges.ShouldBeEmpty();
        Invoice.SubtotalNet.ShouldBeExtracted(Eur(130.00m));
        Invoice.TaxBreakdown.ShouldBe([new TaxBreakdownLine(19m, Eur(120.00m), Eur(22.80m), TaxCategory.Standard)]);
        Invoice.TotalTax.ShouldBeExtracted(Eur(22.80m));
        Invoice.TotalGross.ShouldBeExtracted(Eur(142.80m));
        Invoice.AmountDue.ShouldBeExtracted(Eur(142.80m));
        Invoice.PaymentTerms.ShouldBeExtracted(new PaymentTerms(
            DueDate: new DateOnly(2026, 11, 28),
            PaymentMeans: PaymentMeans.BankTransfer,
            PaymentReference: "2026/4471",
            Iban: "DE75512108001245126199",
            Bic: "SOGEDEFFXXX",
            RawText: "Zahlbar bis 28.11.2026 ohne Abzug."));
    }

    [Fact]
    public void Lines_map_case_and_box_units_an_ean_gtin_and_a_price_per_base_quantity()
    {
        Invoice.Lines.Count.ShouldBe(2);

        var water = Invoice.Lines[0];
        water.SupplierSku.ShouldBeExtracted("MW-075-12");
        water.Gtin.ShouldBeExtracted("4260123456789");
        water.Quantity.ShouldBeExtracted(10m);
        water.Unit.ShouldBeExtracted(UnitOfMeasure.Case);
        water.UnitRaw.ShouldBe("CS");
        water.UnitPrice.ShouldBeExtracted(Eur(6.80m));
        water.LineNet.ShouldBeExtracted(Eur(68.00m));

        var juice = Invoice.Lines[1];
        juice.SupplierSku.ShouldBeExtracted("AS-100-6");
        juice.Gtin.ShouldBeMissing();
        juice.Quantity.ShouldBeExtracted(5m);
        juice.Unit.ShouldBeExtracted(UnitOfMeasure.Box);
        juice.UnitRaw.ShouldBe("BX");
        juice.UnitPrice.ShouldBeExtracted(Eur(12.40m));
        juice.UnitPrice.Source!.RawText.ShouldBe("62.00 / 5");
        juice.LineNet.ShouldBeExtracted(Eur(62.00m));
    }

    [Fact]
    public void Every_present_field_has_structured_xml_provenance_at_full_confidence()
    {
        Invoice.ShouldHaveXmlProvenanceEverywhere();
    }
}
