using DocProc.Domain.Common;
using DocProc.Domain.Invoices;
using static DocProc.EInvoice.Tests.FieldAssertions;

namespace DocProc.EInvoice.Tests;

/// <summary>Field-by-field mapping of the UBL credit note that references the Peppol invoice fixture.</summary>
public class UblCreditNoteMappingTests
{
    private static readonly Invoice CreditNote = Fixtures.Parse(Fixtures.PeppolCreditNote);

    [Fact]
    public void Credit_note_root_yields_the_credit_note_kind_with_the_type_code_as_raw_text()
    {
        CreditNote.Kind.ShouldBeExtracted(InvoiceKind.CreditNote);
        CreditNote.Kind.Source!.RawText.ShouldBe("381");
    }

    [Fact]
    public void Header_references_the_original_invoice()
    {
        CreditNote.InvoiceNumber.ShouldBeExtracted(DocumentIdentifier.Create("GS-2026-0077"));
        CreditNote.IssueDate.ShouldBeExtracted(new DateOnly(2026, 10, 29));
        CreditNote.OriginalInvoiceReference.ShouldBeExtracted(DocumentIdentifier.Create("RE-2026-10412"));
        CreditNote.OrderReferences.ShouldBeEmpty();
        CreditNote.DeliveryNoteReferences.ShouldBeEmpty();
        CreditNote.Currency.ShouldBeExtracted("EUR");
        CreditNote.Notes.ShouldBeExtracted("Gutschrift für die Retoure vom 28.10.2026 (Qualitätsmangel).");
        CreditNote.DeliveryDate.ShouldBeMissing();
        CreditNote.ServicePeriod.ShouldBeMissing();
    }

    [Fact]
    public void Due_date_comes_from_payment_means_because_the_credit_note_schema_has_no_due_date_element()
    {
        CreditNote.DueDate.ShouldBeExtracted(new DateOnly(2026, 11, 12));
        CreditNote.PaymentTerms.ShouldBeExtracted(new PaymentTerms(
            DueDate: new DateOnly(2026, 11, 12),
            PaymentMeans: PaymentMeans.BankTransfer,
            PaymentReference: "GS-2026-0077",
            Iban: "DE89370400440532013000",
            Bic: "COBADEFFXXX"));
    }

    [Fact]
    public void Parties_without_a_party_name_block_still_get_their_legal_names()
    {
        var supplier = CreditNote.Supplier.ShouldBeExtracted(CreditNote.Supplier.Value!);
        supplier.Name.ShouldBe("Fischhandel Müller GmbH");
        supplier.VatId.ShouldBe("DE812345678");
        supplier.Email.ShouldBe("rechnung@fischhandel-mueller.example");
        supplier.Iban.ShouldBe("DE89370400440532013000");

        var customer = CreditNote.Customer.ShouldBeExtracted(CreditNote.Customer.Value!);
        customer.Name.ShouldBe("Gasthaus zur Post GmbH");
        customer.VatId.ShouldBe("DE123456789");
        customer.Email.ShouldBeNull();
        customer.Iban.ShouldBeNull();
    }

    [Fact]
    public void Totals_and_the_single_credited_line()
    {
        CreditNote.SubtotalNet.ShouldBeExtracted(Eur(46.00m));
        CreditNote.TaxBreakdown.ShouldBe([new TaxBreakdownLine(7m, Eur(46.00m), Eur(3.22m), TaxCategory.Standard)]);
        CreditNote.TotalTax.ShouldBeExtracted(Eur(3.22m));
        CreditNote.TotalGross.ShouldBeExtracted(Eur(49.22m));
        CreditNote.AmountDue.ShouldBeExtracted(Eur(49.22m));
        CreditNote.Charges.ShouldBeEmpty();
        CreditNote.Allowances.ShouldBeEmpty();

        var line = CreditNote.Lines.ShouldHaveSingleItem();
        line.LineNumber.ShouldBe(1);
        line.SupplierSku.ShouldBeExtracted("LAX-F-01");
        line.Gtin.ShouldBeExtracted("4012345678901");
        line.Description.ShouldBeExtracted("Lachsfilet frisch");
        line.Quantity.ShouldBeExtracted(2.5m);
        line.Unit.ShouldBeExtracted(UnitOfMeasure.Kilogram);
        line.UnitRaw.ShouldBe("KGM");
        line.UnitPrice.ShouldBeExtracted(Eur(18.40m));
        line.LineNet.ShouldBeExtracted(Eur(46.00m));
        line.TaxRate.ShouldBeExtracted(7m);
    }

    [Fact]
    public void Every_present_field_has_structured_xml_provenance_at_full_confidence()
    {
        CreditNote.ShouldHaveXmlProvenanceEverywhere();
    }
}
