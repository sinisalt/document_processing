using DocProc.Domain.Common;
using DocProc.Domain.Invoices;
using static DocProc.EInvoice.Tests.FieldAssertions;

namespace DocProc.EInvoice.Tests;

/// <summary>Field-by-field mapping of the Factur-X EN 16931 CII fixture, which carries the same business content as the Peppol UBL invoice.</summary>
public class CiiInvoiceMappingTests
{
    private static readonly Invoice Invoice = Fixtures.Parse(Fixtures.FacturXEn16931);

    [Fact]
    public void Header_identity_and_format_102_dates()
    {
        Invoice.Kind.ShouldBeExtracted(InvoiceKind.Invoice);
        Invoice.Kind.Source!.RawText.ShouldBe("380");
        Invoice.InvoiceNumber.ShouldBeExtracted(DocumentIdentifier.Create("RE-2026-10412"));
        Invoice.IssueDate.ShouldBeExtracted(new DateOnly(2026, 10, 27));
        Invoice.IssueDate.Source!.RawText.ShouldBe("20261027");
        Invoice.DueDate.ShouldBeExtracted(new DateOnly(2026, 11, 10));
        Invoice.DeliveryDate.ShouldBeExtracted(new DateOnly(2026, 10, 26));
        Invoice.ServicePeriod.ShouldBeExtracted(new DatePeriod(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 26)));
        Invoice.Currency.ShouldBeExtracted("EUR");
        Invoice.Notes.ShouldBeExtracted("Lieferung frei Haus. Die Ware bleibt bis zur vollständigen Bezahlung unser Eigentum.");
    }

    [Fact]
    public void Seller_carries_vat_id_from_scheme_VA_and_tax_number_from_scheme_FC()
    {
        Invoice.Supplier.ShouldBeExtracted(new Party(
            Name: "Fischhandel Müller GmbH",
            VatId: "DE812345678",
            TaxId: "22/123/45678",
            RegistrationNumber: "HRB 123456",
            Address: new Address("Große Elbstraße 27", "Halle 3", "22767", "Hamburg", null, "DE"),
            Email: "rechnung@fischhandel-mueller.example",
            Phone: "+49 40 123456-0",
            Iban: "DE89370400440532013000",
            Bic: "COBADEFFXXX"));
    }

    [Fact]
    public void Buyer_is_the_tenant_with_its_electronic_address_as_email()
    {
        Invoice.Customer.ShouldBeExtracted(new Party(
            Name: "Gasthaus zur Post GmbH",
            VatId: "DE123456789",
            Address: new Address("Hauptstraße 12", null, "83022", "Rosenheim", null, "DE"),
            Email: "einkauf@gasthaus-zur-post.example"));
    }

    [Fact]
    public void References()
    {
        Invoice.OrderReferences.Select(r => r.Value!.Verbatim).ShouldBe(["PO-7781"]);
        Invoice.DeliveryNoteReferences.Select(r => r.Value!.Verbatim).ShouldBe(["LS-55120"]);
        Invoice.OriginalInvoiceReference.ShouldBeMissing();
    }

    [Fact]
    public void Totals_tax_breakdown_and_freight_charge()
    {
        Invoice.SubtotalNet.ShouldBeExtracted(Eur(382.00m));
        Invoice.TotalTax.ShouldBeExtracted(Eur(40.99m));
        Invoice.TotalGross.ShouldBeExtracted(Eur(437.99m));
        Invoice.AmountDue.ShouldBeExtracted(Eur(437.99m));
        Invoice.Rounding.ShouldBeMissing();
        Invoice.TaxBreakdown.ShouldBe(
        [
            new TaxBreakdownLine(7m, Eur(287.00m), Eur(20.09m), TaxCategory.Standard),
            new TaxBreakdownLine(19m, Eur(110.00m), Eur(20.90m), TaxCategory.Standard),
        ]);
        Invoice.Charges.ShouldBe([new ChargeOrAllowance("Frachtkosten", Eur(15.00m), 19m, IsCharge: true)]);
        Invoice.Allowances.ShouldBeEmpty();
    }

    [Fact]
    public void Payment_terms()
    {
        Invoice.PaymentTerms.ShouldBeExtracted(new PaymentTerms(
            DueDate: new DateOnly(2026, 11, 10),
            PaymentMeans: PaymentMeans.BankTransfer,
            PaymentReference: "RE-2026-10412",
            Iban: "DE89370400440532013000",
            Bic: "COBADEFFXXX",
            RawText: "Zahlbar innerhalb von 14 Tagen ohne Abzug."));
    }

    [Fact]
    public void Lines_including_a_line_allowance_and_product_characteristics()
    {
        Invoice.Lines.Count.ShouldBe(3);

        var salmon = Invoice.Lines[0];
        salmon.LineNumber.ShouldBe(1);
        salmon.SupplierSku.ShouldBeExtracted("LAX-F-01");
        salmon.Gtin.ShouldBeExtracted("4012345678901");
        salmon.Description.ShouldBeExtracted("Lachsfilet frisch");
        salmon.Quantity.ShouldBeExtracted(12.5m);
        salmon.Unit.ShouldBeExtracted(UnitOfMeasure.Kilogram);
        salmon.UnitRaw.ShouldBe("KGM");
        salmon.UnitPrice.ShouldBeExtracted(Eur(18.40m));
        salmon.LineNet.ShouldBeExtracted(Eur(230.00m));
        salmon.TaxRate.ShouldBeExtracted(7m);
        salmon.LotNumber.ShouldBeExtracted("L-2026-2991");
        salmon.BestBefore.ShouldBeExtracted(new DateOnly(2026, 11, 2));

        var oil = Invoice.Lines[1];
        oil.Unit.ShouldBeExtracted(UnitOfMeasure.Litre);
        oil.Quantity.ShouldBeExtracted(6m);
        oil.UnitPrice.ShouldBeExtracted(Eur(9.50m));
        oil.LineNet.ShouldBeExtracted(Eur(57.00m));

        var tabs = Invoice.Lines[2];
        tabs.Unit.ShouldBeExtracted(UnitOfMeasure.Piece);
        tabs.UnitRaw.ShouldBe("C62");
        tabs.Quantity.ShouldBeExtracted(4m);
        tabs.UnitPrice.ShouldBeExtracted(Eur(24.90m));
        tabs.DiscountAmount.ShouldBeExtracted(Eur(4.60m));
        tabs.LineNet.ShouldBeExtracted(Eur(95.00m));
        tabs.TaxRate.ShouldBeExtracted(19m);
    }

    [Fact]
    public void Cii_and_ubl_renderings_of_the_same_invoice_map_to_the_same_values()
    {
        var ubl = Fixtures.Parse(Fixtures.PeppolInvoice);

        Invoice.InvoiceNumber.Value.ShouldBe(ubl.InvoiceNumber.Value);
        Invoice.IssueDate.Value.ShouldBe(ubl.IssueDate.Value);
        Invoice.DueDate.Value.ShouldBe(ubl.DueDate.Value);
        Invoice.DeliveryDate.Value.ShouldBe(ubl.DeliveryDate.Value);
        Invoice.ServicePeriod.Value.ShouldBe(ubl.ServicePeriod.Value);
        Invoice.Supplier.Value!.ShouldBe(ubl.Supplier.Value! with { TaxId = Invoice.Supplier.Value!.TaxId });
        Invoice.Customer.Value.ShouldBe(ubl.Customer.Value);
        Invoice.OrderReferences.Select(r => r.Value).ShouldBe(ubl.OrderReferences.Select(r => r.Value));
        Invoice.DeliveryNoteReferences.Select(r => r.Value).ShouldBe(ubl.DeliveryNoteReferences.Select(r => r.Value));
        Invoice.SubtotalNet.Value.ShouldBe(ubl.SubtotalNet.Value);
        Invoice.TaxBreakdown.ShouldBe(ubl.TaxBreakdown);
        Invoice.TotalTax.Value.ShouldBe(ubl.TotalTax.Value);
        Invoice.TotalGross.Value.ShouldBe(ubl.TotalGross.Value);
        Invoice.AmountDue.Value.ShouldBe(ubl.AmountDue.Value);
        Invoice.Charges.ShouldBe(ubl.Charges);
        Invoice.PaymentTerms.Value.ShouldBe(ubl.PaymentTerms.Value);
        Invoice.Notes.Value.ShouldBe(ubl.Notes.Value);

        foreach (var (cii, peppol) in Invoice.Lines.Zip(ubl.Lines))
        {
            cii.LineNumber.ShouldBe(peppol.LineNumber);
            cii.SupplierSku.Value.ShouldBe(peppol.SupplierSku.Value);
            cii.Gtin.Value.ShouldBe(peppol.Gtin.Value);
            cii.Description.Value.ShouldBe(peppol.Description.Value);
            cii.Quantity.Value.ShouldBe(peppol.Quantity.Value);
            cii.Unit.Value.ShouldBe(peppol.Unit.Value);
            cii.UnitRaw.ShouldBe(peppol.UnitRaw);
            cii.UnitPrice.Value.ShouldBe(peppol.UnitPrice.Value);
            cii.DiscountAmount.Value.ShouldBe(peppol.DiscountAmount.Value);
            cii.LineNet.Value.ShouldBe(peppol.LineNet.Value);
            cii.TaxRate.Value.ShouldBe(peppol.TaxRate.Value);
            cii.LotNumber.Value.ShouldBe(peppol.LotNumber.Value);
            cii.BestBefore.Value.ShouldBe(peppol.BestBefore.Value);
        }
    }

    [Fact]
    public void Every_present_field_has_structured_xml_provenance_at_full_confidence()
    {
        Invoice.ShouldHaveXmlProvenanceEverywhere();
    }
}
