using DocProc.Domain.Common;
using DocProc.Domain.Invoices;
using static DocProc.EInvoice.Tests.FieldAssertions;

namespace DocProc.EInvoice.Tests;

/// <summary>Field-by-field mapping of the Peppol BIS Billing 3.0 UBL invoice fixture.</summary>
public class UblInvoiceMappingTests
{
    private static readonly Invoice Invoice = Fixtures.Parse(Fixtures.PeppolInvoice);

    [Fact]
    public void Header_identity_and_dates()
    {
        Invoice.Kind.ShouldBeExtracted(InvoiceKind.Invoice);
        Invoice.Kind.Source!.RawText.ShouldBe("380");
        Invoice.InvoiceNumber.ShouldBeExtracted(DocumentIdentifier.Create("RE-2026-10412"));
        Invoice.IssueDate.ShouldBeExtracted(new DateOnly(2026, 10, 27));
        Invoice.DueDate.ShouldBeExtracted(new DateOnly(2026, 11, 10));
        Invoice.DeliveryDate.ShouldBeExtracted(new DateOnly(2026, 10, 26));
        Invoice.ServicePeriod.ShouldBeExtracted(new DatePeriod(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 26)));
        Invoice.Currency.ShouldBeExtracted("EUR");
        Invoice.Notes.ShouldBeExtracted("Lieferung frei Haus. Die Ware bleibt bis zur vollständigen Bezahlung unser Eigentum.");
    }

    [Fact]
    public void Supplier_prefers_the_registration_name_and_carries_vat_id_registration_address_contact_and_bank()
    {
        var supplier = Invoice.Supplier.ShouldBeExtracted(new Party(
            Name: "Fischhandel Müller GmbH",
            VatId: "DE812345678",
            TaxId: null,
            RegistrationNumber: "HRB 123456",
            Address: new Address("Große Elbstraße 27", "Halle 3", "22767", "Hamburg", null, "DE"),
            Email: "rechnung@fischhandel-mueller.example",
            Phone: "+49 40 123456-0",
            Iban: "DE89370400440532013000",
            Bic: "COBADEFFXXX"));

        supplier.NormalizedVatId.ShouldBe("DE812345678");
    }

    [Fact]
    public void Customer_is_the_tenant_with_its_vat_id_and_the_endpoint_email()
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
        Invoice.OrderReferences.Select(r => r.ShouldBeExtracted(r.Value!).Verbatim).ShouldBe(["KUECHE-ROSENHEIM", "PO-7781"]);
        Invoice.DeliveryNoteReferences.Select(r => r.ShouldBeExtracted(r.Value!).Verbatim).ShouldBe(["LS-55120"]);
        Invoice.OriginalInvoiceReference.ShouldBeMissing();
    }

    [Fact]
    public void Totals_and_tax_breakdown()
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
    }

    [Fact]
    public void Document_level_freight_charge_is_a_charge_with_its_tax_rate()
    {
        Invoice.Charges.ShouldBe([new ChargeOrAllowance("Frachtkosten", Eur(15.00m), 19m, IsCharge: true)]);
        Invoice.Allowances.ShouldBeEmpty();
    }

    [Fact]
    public void Payment_terms_combine_due_date_means_reference_bank_account_and_note()
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
    public void Lines_map_identifiers_quantities_units_prices_and_nets()
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
        salmon.DiscountAmount.ShouldBeMissing();
        salmon.DiscountPercent.ShouldBeMissing();
        salmon.LineNet.ShouldBeExtracted(Eur(230.00m));
        salmon.TaxRate.ShouldBeExtracted(7m);
        salmon.LotNumber.ShouldBeExtracted("L-2026-2991");
        salmon.BestBefore.ShouldBeExtracted(new DateOnly(2026, 11, 2));

        var oil = Invoice.Lines[1];
        oil.LineNumber.ShouldBe(2);
        oil.SupplierSku.ShouldBeExtracted("OEL-5L");
        oil.Gtin.ShouldBeExtracted("4012345678918");
        oil.Quantity.ShouldBeExtracted(6m);
        oil.Unit.ShouldBeExtracted(UnitOfMeasure.Litre);
        oil.UnitRaw.ShouldBe("LTR");
        oil.UnitPrice.ShouldBeExtracted(Eur(9.50m));
        oil.LineNet.ShouldBeExtracted(Eur(57.00m));
        oil.TaxRate.ShouldBeExtracted(7m);
        oil.LotNumber.ShouldBeMissing();
        oil.BestBefore.ShouldBeMissing();

        var tabs = Invoice.Lines[2];
        tabs.LineNumber.ShouldBe(3);
        tabs.SupplierSku.ShouldBeExtracted("SPT-100");
        tabs.Description.ShouldBeExtracted("Spülmaschinen-Tabs 100 Stück");
        tabs.Quantity.ShouldBeExtracted(4m);
        tabs.Unit.ShouldBeExtracted(UnitOfMeasure.Piece);
        tabs.UnitRaw.ShouldBe("C62");
        tabs.UnitPrice.ShouldBeExtracted(Eur(24.90m));
        tabs.DiscountAmount.ShouldBeExtracted(Eur(4.60m));
        tabs.LineNet.ShouldBeExtracted(Eur(95.00m));
        tabs.TaxRate.ShouldBeExtracted(19m);
    }

    [Fact]
    public void Every_present_field_has_structured_xml_provenance_at_full_confidence()
    {
        Invoice.ShouldHaveXmlProvenanceEverywhere();
        Invoice.EnumerateAllFields().Count(f => f.Field.HasValue).ShouldBeGreaterThan(40);
    }
}
