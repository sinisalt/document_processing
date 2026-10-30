using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation;
using static DocProc.Domain.Tests.InvoiceFixtures;

namespace DocProc.Domain.Tests;

public class InvoiceValidatorTests
{
    private static ValidationResult Validate(Invoice invoice, ValidationContext? context = null) =>
        InvoiceValidator.Default.Validate(invoice, context ?? Context);

    [Fact]
    public void Consistent_invoice_raises_no_flags_and_is_auto_approvable()
    {
        var result = Validate(Valid());

        result.Flags.ShouldBeEmpty();
        result.IsEligibleForAutoApproval.ShouldBeTrue();
    }

    [Fact]
    public void Missing_required_header_fields_are_blocking()
    {
        var invoice = Valid() with
        {
            InvoiceNumber = FieldValue<DocumentIdentifier>.Missing(),
            IssueDate = FieldValue<DateOnly>.Unreadable(),
        };

        var result = Validate(invoice);

        var flags = result.OfCode(FlagCodes.MissingRequired).ToList();
        flags.Select(f => f.Field).ShouldBe(["invoiceNumber", "issueDate"], ignoreOrder: true);
        flags.Single(f => f.Field == "issueDate").Message.ShouldContain("could not be read");
        flags.ShouldAllBe(f => f.Severity == FlagSeverity.Blocking);
        result.IsEligibleForAutoApproval.ShouldBeFalse();
    }

    [Fact]
    public void Credit_note_requires_original_invoice_reference()
    {
        var invoice = Valid() with { Kind = FieldValue.Extracted(InvoiceKind.CreditNote) };

        Validate(invoice).OfCode(FlagCodes.MissingRequired).ShouldContain(f => f.Field == "originalInvoiceReference");

        var withRef = invoice with { OriginalInvoiceReference = FieldValue.Extracted(DocumentIdentifier.Create("RE-2026-0001")) };
        Validate(withRef).OfCode(FlagCodes.MissingRequired).ShouldBeEmpty();
    }

    [Fact]
    public void Line_sum_not_matching_subtotal_is_a_totals_mismatch()
    {
        var invoice = Valid() with { SubtotalNet = FieldValue.Extracted(Eur(140.00m)) };

        var result = Validate(invoice);

        var flag = result.OfCode(FlagCodes.TotalsMismatch).Single(f => f.Field == "subtotalNet");
        flag.Severity.ShouldBe(FlagSeverity.Blocking);
        flag.Message.ShouldBe("Lines sum to 150.00 but the subtotal is 140.00.");
    }

    [Fact]
    public void Net_plus_tax_not_matching_gross_is_a_totals_mismatch()
    {
        var invoice = Valid() with { TotalGross = FieldValue.Extracted(Eur(170.00m)) };

        var flag = Validate(invoice).OfCode(FlagCodes.TotalsMismatch).ShouldHaveSingleItem();
        flag.Field.ShouldBe("totalGross");
    }

    [Fact]
    public void Rounding_differences_within_tolerance_are_accepted()
    {
        var invoice = Valid() with
        {
            SubtotalNet = FieldValue.Extracted(Eur(150.01m)),
            TotalGross = FieldValue.Extracted(Eur(166.52m)),
        };

        Validate(invoice).OfCode(FlagCodes.TotalsMismatch).ShouldBeEmpty();
    }

    [Fact]
    public void Document_level_charges_may_be_outside_the_subtotal()
    {
        // Freight is printed after the subtotal and taxed at 19 %: gross = 150 + 10 + (16.50 + 1.90).
        var invoice = Valid() with
        {
            Charges = [new ChargeOrAllowance("Fracht", Eur(10.00m), 19m, IsCharge: true)],
            TaxBreakdown =
            [
                new TaxBreakdownLine(7m, Eur(100.00m), Eur(7.00m)),
                new TaxBreakdownLine(19m, Eur(60.00m), Eur(11.40m)),
            ],
            TotalTax = FieldValue.Extracted(Eur(18.40m)),
            TotalGross = FieldValue.Extracted(Eur(178.40m)),
        };

        var result = Validate(invoice);

        result.OfCode(FlagCodes.TotalsMismatch).ShouldBeEmpty();
        result.OfCode(FlagCodes.TaxMismatch).ShouldBeEmpty();
    }

    [Fact]
    public void Deposit_lines_count_towards_the_subtotal()
    {
        var invoice = Valid();
        var lines = invoice.Lines.ToList();
        lines[1] = lines[1] with { DepositAmount = FieldValue.Extracted(Eur(15.00m)) };
        invoice = invoice with
        {
            Lines = lines,
            SubtotalNet = FieldValue.Extracted(Eur(165.00m)),
            TaxBreakdown = [new TaxBreakdownLine(7m, Eur(100m), Eur(7m)), new TaxBreakdownLine(19m, Eur(65m), Eur(12.35m))],
            TotalTax = FieldValue.Extracted(Eur(19.35m)),
            TotalGross = FieldValue.Extracted(Eur(184.35m)),
        };

        Validate(invoice).OfCode(FlagCodes.TotalsMismatch).ShouldBeEmpty();
    }

    [Fact]
    public void Tax_rows_that_do_not_reproduce_total_tax_are_a_warning()
    {
        var invoice = Valid() with
        {
            TaxBreakdown = [new TaxBreakdownLine(7m, Eur(100.00m), Eur(7.00m)), new TaxBreakdownLine(19m, Eur(50.00m), Eur(9.00m))],
        };

        var flags = Validate(invoice).OfCode(FlagCodes.TaxMismatch).ToList();

        flags.ShouldContain(f => f.Field == "taxBreakdown[1].taxAmount");
        flags.ShouldContain(f => f.Field == "totalTax");
        flags.ShouldAllBe(f => f.Severity == FlagSeverity.Warning);
    }

    [Fact]
    public void Quantity_times_price_not_matching_line_net_flags_that_line_only()
    {
        var invoice = Valid();
        var lines = invoice.Lines.ToList();
        lines[0] = lines[0] with { UnitPrice = FieldValue.Extracted(Eur(9.50m)) };
        invoice = invoice with { Lines = lines };

        var flag = Validate(invoice).OfCode(FlagCodes.LineArithmetic).ShouldHaveSingleItem();
        flag.Field.ShouldBe("lines[0].lineNet");
        flag.Message.ShouldContain("Line 1");
    }

    [Fact]
    public void Line_discounts_are_applied_before_comparing()
    {
        var invoice = Valid();
        var lines = invoice.Lines.ToList();
        lines[0] = lines[0] with
        {
            UnitPrice = FieldValue.Extracted(Eur(12.50m)),
            DiscountPercent = FieldValue.Extracted(20m),
        };
        invoice = invoice with { Lines = lines };

        Validate(invoice).OfCode(FlagCodes.LineArithmetic).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(2027, 1, 15, "in the future")]
    [InlineData(2025, 1, 15, "days old")]
    public void Implausible_issue_dates_are_flagged(int y, int m, int d, string expectedMessagePart)
    {
        var invoice = Valid() with { IssueDate = FieldValue.Extracted(new DateOnly(y, m, d)), DueDate = FieldValue<DateOnly>.Missing() };

        var flag = Validate(invoice).OfCode(FlagCodes.DateImplausible).ShouldHaveSingleItem();
        flag.Field.ShouldBe("issueDate");
        flag.Message.ShouldContain(expectedMessagePart);
    }

    [Fact]
    public void Due_date_before_issue_date_is_flagged()
    {
        var invoice = Valid() with { DueDate = FieldValue.Extracted(new DateOnly(2026, 10, 1)) };

        Validate(invoice).OfCode(FlagCodes.DateImplausible).ShouldHaveSingleItem().Field.ShouldBe("dueDate");
    }

    [Fact]
    public void Inferred_currency_is_informational()
    {
        var invoice = Valid() with { Currency = FieldValue.Inferred("EUR") };

        var flag = Validate(invoice).OfCode(FlagCodes.CurrencyInferred).ShouldHaveSingleItem();
        flag.Severity.ShouldBe(FlagSeverity.Info);
        Validate(invoice).IsEligibleForAutoApproval.ShouldBeTrue();
    }

    [Fact]
    public void Malformed_currency_and_mixed_currencies_are_invalid_values()
    {
        Validate(Valid() with { Currency = FieldValue.Extracted("Euro") })
            .OfCode(FlagCodes.InvalidValue).ShouldHaveSingleItem().Field.ShouldBe("currency");

        var mixed = Valid() with { TotalGross = FieldValue.Extracted(new Money(166.50m, "USD")) };
        Validate(mixed).OfCode(FlagCodes.InvalidValue).ShouldHaveSingleItem().Field.ShouldBe("totalGross");
    }

    [Fact]
    public void Low_confidence_field_blocks_auto_approval_but_is_only_a_warning()
    {
        var invoice = Valid() with { TotalGross = FieldValue.Extracted(Eur(166.50m), confidence: 0.4) };

        var result = Validate(invoice);

        var flag = result.OfCode(FlagCodes.LowConfidence).ShouldHaveSingleItem();
        flag.Field.ShouldBe("totalGross");
        flag.Severity.ShouldBe(FlagSeverity.Warning);
        result.HasBlocking.ShouldBeFalse();
        result.IsEligibleForAutoApproval.ShouldBeFalse();
    }

    [Fact]
    public void Customer_vat_id_mismatch_is_blocking()
    {
        var invoice = Valid() with { Customer = FieldValue.Extracted(new Party("Gasthaus zur Post GmbH", VatId: "DE999999999")) };

        var flag = Validate(invoice).OfCode(FlagCodes.CustomerMismatch).ShouldHaveSingleItem();
        flag.Field.ShouldBe("customer.vatId");
        flag.Severity.ShouldBe(FlagSeverity.Blocking);
    }

    [Fact]
    public void Customer_name_is_compared_when_no_vat_id_is_printed()
    {
        Validate(Valid() with { Customer = FieldValue.Extracted(new Party("GASTHAUS ZUR POST")) })
            .OfCode(FlagCodes.CustomerMismatch).ShouldBeEmpty();

        Validate(Valid() with { Customer = FieldValue.Extracted(new Party("Pizzeria Napoli")) })
            .OfCode(FlagCodes.CustomerMismatch).ShouldHaveSingleItem().Field.ShouldBe("customer.name");
    }

    [Fact]
    public void Customer_check_is_skipped_without_tenant_context()
    {
        var invoice = Valid() with { Customer = FieldValue.Extracted(new Party("Pizzeria Napoli")) };

        Validate(invoice, ValidationContext.For(Today)).OfCode(FlagCodes.CustomerMismatch).ShouldBeEmpty();
    }

    [Fact]
    public void Every_raised_flag_uses_a_catalogued_code()
    {
        var broken = new Invoice { Lines = [new InvoiceLine { LineNumber = 1 }] };

        var result = Validate(broken);

        result.Flags.ShouldNotBeEmpty();
        result.Flags.ShouldAllBe(f => FlagCodes.IsKnown(f.Code));
    }
}
