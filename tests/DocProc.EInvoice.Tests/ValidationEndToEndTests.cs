using DocProc.Domain.Flags;
using DocProc.Domain.Validation;

namespace DocProc.EInvoice.Tests;

/// <summary>
/// Parse, then run the domain validator with the tenant the documents are addressed to. Zero flags on the committed
/// fixtures proves the mapping and the fixture arithmetic end to end; deliberately broken copies prove the flags fire.
/// </summary>
public class ValidationEndToEndTests
{
    private static ValidationResult Validate(string xml) =>
        InvoiceValidator.Default.Validate(Fixtures.Parse(Fixtures.Utf8(xml)), Fixtures.Context);

    public static TheoryData<string> Consistent => new(Fixtures.All);

    [Theory]
    [MemberData(nameof(Consistent))]
    public void Consistent_fixtures_raise_no_flags_and_are_eligible_for_auto_approval(string fixture)
    {
        var result = InvoiceValidator.Default.Validate(Fixtures.Parse(fixture), Fixtures.Context);

        result.Flags.ShouldBeEmpty(fixture);
        result.IsEligibleForAutoApproval.ShouldBeTrue(fixture);
    }

    [Fact]
    public void Wrong_gross_total_in_ubl_is_a_blocking_totals_mismatch()
    {
        var broken = Fixtures.Text(Fixtures.PeppolInvoice)
            .Replace("437.99</cbc:TaxInclusiveAmount>", "447.99</cbc:TaxInclusiveAmount>", StringComparison.Ordinal);

        var result = Validate(broken);

        var flag = result.OfCode(FlagCodes.TotalsMismatch).ShouldHaveSingleItem();
        flag.Field.ShouldBe("totalGross");
        flag.Severity.ShouldBe(FlagSeverity.Blocking);
        result.IsEligibleForAutoApproval.ShouldBeFalse();
    }

    [Fact]
    public void Wrong_tax_row_in_ubl_is_a_tax_mismatch_on_the_row_and_on_the_total()
    {
        var broken = Fixtures.Text(Fixtures.PeppolInvoice)
            .Replace("<cbc:TaxAmount currencyID=\"EUR\">20.09</cbc:TaxAmount>", "<cbc:TaxAmount currencyID=\"EUR\">21.09</cbc:TaxAmount>", StringComparison.Ordinal);

        var result = Validate(broken);

        result.OfCode(FlagCodes.TaxMismatch).Select(f => f.Field).ShouldBe(["taxBreakdown[0].taxAmount", "totalTax"], ignoreOrder: true);
        result.OfCode(FlagCodes.TotalsMismatch).ShouldBeEmpty();
    }

    [Fact]
    public void Wrong_line_net_in_cii_is_a_line_arithmetic_warning_and_a_subtotal_mismatch()
    {
        var broken = Fixtures.Text(Fixtures.FacturXEn16931)
            .Replace("<ram:LineTotalAmount>230.00</ram:LineTotalAmount>", "<ram:LineTotalAmount>231.00</ram:LineTotalAmount>", StringComparison.Ordinal);

        var result = Validate(broken);

        result.OfCode(FlagCodes.LineArithmetic).ShouldHaveSingleItem().Field.ShouldBe("lines[0].lineNet");
        result.OfCode(FlagCodes.TotalsMismatch).ShouldHaveSingleItem().Field.ShouldBe("subtotalNet");
    }

    [Fact]
    public void Wrong_grand_total_in_cii_is_a_totals_mismatch_on_the_gross_and_on_the_now_exceeding_amount_due()
    {
        var broken = Fixtures.Text(Fixtures.FacturXEn16931)
            .Replace("<ram:GrandTotalAmount>437.99</ram:GrandTotalAmount>", "<ram:GrandTotalAmount>437.00</ram:GrandTotalAmount>", StringComparison.Ordinal);

        var result = Validate(broken);

        result.OfCode(FlagCodes.TotalsMismatch).Select(f => f.Field).ShouldBe(["totalGross", "amountDue"], ignoreOrder: true);
        result.OfCode(FlagCodes.TaxMismatch).ShouldBeEmpty();
    }

    [Fact]
    public void Credit_note_without_an_original_invoice_reference_is_missing_a_required_field()
    {
        var broken = Fixtures.Text(Fixtures.PeppolCreditNote)
            .Replace("<cbc:ID>RE-2026-10412</cbc:ID>", "<cbc:ID> </cbc:ID>", StringComparison.Ordinal);

        var result = Validate(broken);

        result.OfCode(FlagCodes.MissingRequired).ShouldHaveSingleItem().Field.ShouldBe("originalInvoiceReference");
    }

    [Fact]
    public void Document_addressed_to_another_company_is_a_customer_mismatch()
    {
        var broken = Fixtures.Text(Fixtures.ZugferdBasic)
            .Replace("<ram:Name>Gasthaus zur Post GmbH</ram:Name>", "<ram:Name>Hotel Alpenblick KG</ram:Name>", StringComparison.Ordinal);

        var result = Validate(broken);

        result.OfCode(FlagCodes.CustomerMismatch).ShouldHaveSingleItem().Field.ShouldBe("customer.name");
    }

    [Fact]
    public void Unreadable_amount_surfaces_as_missing_required_not_as_an_exception()
    {
        var broken = Fixtures.Text(Fixtures.XRechnungUbl)
            .Replace("<cbc:TaxInclusiveAmount currencyID=\"EUR\">142.80</cbc:TaxInclusiveAmount>", "<cbc:TaxInclusiveAmount currencyID=\"EUR\">142,80</cbc:TaxInclusiveAmount>", StringComparison.Ordinal);

        var result = Validate(broken);

        var flag = result.OfCode(FlagCodes.MissingRequired).ShouldHaveSingleItem();
        flag.Field.ShouldBe("totalGross");
        flag.Message.ShouldContain("could not be read");
    }
}
