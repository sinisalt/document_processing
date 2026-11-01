using DocProc.Domain.Common;
using DocProc.Domain.Invoices;
using static DocProc.EInvoice.Tests.FieldAssertions;

namespace DocProc.EInvoice.Tests;

/// <summary>The ZUGFeRD BASIC profile carries fewer elements; everything it omits must stay Missing, not guessed.</summary>
public class CiiBasicMappingTests
{
    private static readonly Invoice Invoice = Fixtures.Parse(Fixtures.ZugferdBasic);

    [Fact]
    public void Header()
    {
        Invoice.Kind.ShouldBeExtracted(InvoiceKind.Invoice);
        Invoice.InvoiceNumber.ShouldBeExtracted(DocumentIdentifier.Create("2026-0918"));
        Invoice.IssueDate.ShouldBeExtracted(new DateOnly(2026, 10, 28));
        Invoice.DueDate.ShouldBeExtracted(new DateOnly(2026, 11, 11));
        Invoice.DeliveryDate.ShouldBeExtracted(new DateOnly(2026, 10, 28));
        Invoice.Currency.ShouldBeExtracted("EUR");
        Invoice.ServicePeriod.ShouldBeMissing();
        Invoice.Notes.ShouldBeMissing();
        Invoice.OrderReferences.ShouldBeEmpty();
        Invoice.DeliveryNoteReferences.ShouldBeEmpty();
        Invoice.OriginalInvoiceReference.ShouldBeMissing();
    }

    [Fact]
    public void Parties_with_only_name_address_and_seller_vat_id()
    {
        Invoice.Supplier.ShouldBeExtracted(new Party(
            Name: "Bäckerei Hofer e.K.",
            VatId: "DE298765432",
            Address: new Address("Münchener Straße 4", null, "83022", "Rosenheim", null, "DE"),
            Iban: "DE02120300000000202051"));

        Invoice.Customer.ShouldBeExtracted(new Party(
            Name: "Gasthaus zur Post GmbH",
            Address: new Address("Hauptstraße 12", null, "83022", "Rosenheim", null, "DE")));
    }

    [Fact]
    public void Totals_with_a_single_tax_rate_and_no_adjustments()
    {
        Invoice.SubtotalNet.ShouldBeExtracted(Eur(134.00m));
        Invoice.TaxBreakdown.ShouldBe([new TaxBreakdownLine(7m, Eur(134.00m), Eur(9.38m), TaxCategory.Standard)]);
        Invoice.TotalTax.ShouldBeExtracted(Eur(9.38m));
        Invoice.TotalGross.ShouldBeExtracted(Eur(143.38m));
        Invoice.AmountDue.ShouldBeExtracted(Eur(143.38m));
        Invoice.Rounding.ShouldBeMissing();
        Invoice.Charges.ShouldBeEmpty();
        Invoice.Allowances.ShouldBeEmpty();
        Invoice.PaymentTerms.ShouldBeExtracted(new PaymentTerms(
            DueDate: new DateOnly(2026, 11, 11),
            PaymentMeans: PaymentMeans.BankTransfer,
            Iban: "DE02120300000000202051"));
    }

    [Fact]
    public void Lines_without_identifiers_keep_sku_and_gtin_missing()
    {
        Invoice.Lines.Count.ShouldBe(2);

        var rolls = Invoice.Lines[0];
        rolls.LineNumber.ShouldBe(1);
        rolls.SupplierSku.ShouldBeMissing();
        rolls.Gtin.ShouldBeMissing();
        rolls.Description.ShouldBeExtracted("Brötchen Weizen");
        rolls.Quantity.ShouldBeExtracted(200m);
        rolls.Unit.ShouldBeExtracted(UnitOfMeasure.Piece);
        rolls.UnitPrice.ShouldBeExtracted(Eur(0.35m));
        rolls.LineNet.ShouldBeExtracted(Eur(70.00m));
        rolls.TaxRate.ShouldBeExtracted(7m);
        rolls.LotNumber.ShouldBeMissing();
        rolls.BestBefore.ShouldBeMissing();

        var bread = Invoice.Lines[1];
        bread.LineNumber.ShouldBe(2);
        bread.Quantity.ShouldBeExtracted(20m);
        bread.UnitPrice.ShouldBeExtracted(Eur(3.20m));
        bread.LineNet.ShouldBeExtracted(Eur(64.00m));
    }

    [Fact]
    public void Every_present_field_has_structured_xml_provenance_at_full_confidence()
    {
        Invoice.ShouldHaveXmlProvenanceEverywhere();
    }
}
