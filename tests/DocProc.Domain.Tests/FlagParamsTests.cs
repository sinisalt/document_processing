using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation;
using static DocProc.Domain.Tests.InvoiceFixtures;

namespace DocProc.Domain.Tests;

/// <summary>
/// Every validation flag carries its facts as invariant parameters, so the apps can phrase it in the UI language
/// (docs/02 §5 "Flag parameters") while <c>Message</c> stays the English text for API clients.
/// </summary>
public class FlagParamsTests
{
    private static ValidationResult Validate(Invoice invoice, ValidationContext? context = null) =>
        InvoiceValidator.Default.Validate(invoice, context ?? Context);

    private static void ShouldHave(DocumentFlag flag, params (string Key, string Value)[] expected)
    {
        flag.Params.ShouldNotBeNull();
        flag.Params.ShouldBe(expected.ToDictionary(e => e.Key, e => e.Value), ignoreOrder: true);
    }

    [Fact]
    public void Low_confidence_names_the_field_its_confidence_and_the_threshold()
    {
        var invoice = Valid() with { DueDate = FieldValue.Extracted(new DateOnly(2026, 11, 30), confidence: 0.5) };

        var flag = Validate(invoice).OfCode(FlagCodes.LowConfidence).ShouldHaveSingleItem();

        flag.Message.ShouldBe("'dueDate' has confidence 0.50, below the threshold 0.80.");
        ShouldHave(flag, ("field", "dueDate"), ("confidence", "0.50"), ("threshold", "0.80"));
    }

    [Fact]
    public void Totals_mismatch_names_its_variant_and_amounts()
    {
        var lines = Validate(Valid() with { SubtotalNet = FieldValue.Extracted(Eur(140.00m)) })
            .OfCode(FlagCodes.TotalsMismatch).Single(f => f.Field == "subtotalNet");
        ShouldHave(lines, ("reason", "lines"), ("sum", "150.00"), ("subtotal", "140.00"));

        var gross = Validate(Valid() with { TotalGross = FieldValue.Extracted(Eur(170.00m)) })
            .OfCode(FlagCodes.TotalsMismatch).ShouldHaveSingleItem();
        gross.Params!["reason"].ShouldBe("gross");
        gross.Params["gross"].ShouldBe("170.00");
    }

    [Theory]
    [InlineData(2027, 1, 15, "future")]
    [InlineData(2025, 1, 15, "tooOld")]
    public void Implausible_dates_name_the_reason_and_the_iso_date(int y, int m, int d, string reason)
    {
        var invoice = Valid() with { IssueDate = FieldValue.Extracted(new DateOnly(y, m, d)), DueDate = FieldValue<DateOnly>.Missing() };

        var flag = Validate(invoice).OfCode(FlagCodes.DateImplausible).ShouldHaveSingleItem();

        flag.Params!["reason"].ShouldBe(reason);
        flag.Params["issueDate"].ShouldBe($"{y:0000}-{m:00}-{d:00}");
    }

    [Fact]
    public void Missing_required_tells_not_found_from_unreadable()
    {
        var invoice = Valid() with { InvoiceNumber = FieldValue<DocumentIdentifier>.Missing(), IssueDate = FieldValue<DateOnly>.Unreadable() };

        var flags = Validate(invoice).OfCode(FlagCodes.MissingRequired).ToList();

        ShouldHave(flags.Single(f => f.Field == "invoiceNumber"), ("reason", "notFound"), ("field", "invoiceNumber"));
        ShouldHave(flags.Single(f => f.Field == "issueDate"), ("reason", "unreadable"), ("field", "issueDate"));
    }

    [Fact]
    public void Line_arithmetic_carries_the_line_and_its_numbers()
    {
        var invoice = Valid();
        var lines = invoice.Lines.ToList();
        lines[0] = lines[0] with { UnitPrice = FieldValue.Extracted(Eur(9.50m)) };

        var flag = Validate(invoice with { Lines = lines }).OfCode(FlagCodes.LineArithmetic).ShouldHaveSingleItem();

        flag.Params!["line"].ShouldBe("1");
        flag.Params["unitPrice"].ShouldBe("9.50");
        flag.Params.Keys.ShouldBe(["line", "quantity", "unitPrice", "expected", "actual"], ignoreOrder: true);
    }

    [Fact]
    public void Customer_and_currency_flags_quote_the_values_they_compare()
    {
        var vat = Validate(Valid() with { Customer = FieldValue.Extracted(new Party("Gasthaus zur Post GmbH", VatId: "DE999999999")) })
            .OfCode(FlagCodes.CustomerMismatch).ShouldHaveSingleItem();
        ShouldHave(vat, ("reason", "vatId"), ("customerVatId", "DE999999999"), ("tenantVatId", "DE123456789"));

        var mixed = Validate(Valid() with { TotalGross = FieldValue.Extracted(new Money(166.50m, "USD")) })
            .OfCode(FlagCodes.InvalidValue).ShouldHaveSingleItem();
        ShouldHave(mixed, ("reason", "currencyMismatch"), ("field", "totalGross"), ("currency", "USD"), ("documentCurrency", "EUR"));
    }

    [Fact]
    public void Every_flag_of_a_code_with_several_messages_names_its_variant()
    {
        // A code whose rules phrase it more than one way needs `reason`, or a UI could not pick the right sentence.
        string[] multiVariant = [FlagCodes.DateImplausible, FlagCodes.TotalsMismatch, FlagCodes.TaxMismatch, FlagCodes.InvalidValue, FlagCodes.CustomerMismatch, FlagCodes.MissingRequired];
        var broken = Valid() with
        {
            InvoiceNumber = FieldValue<DocumentIdentifier>.Missing(),
            IssueDate = FieldValue.Extracted(new DateOnly(2027, 1, 15)),
            DueDate = FieldValue.Extracted(new DateOnly(2026, 12, 1)),
            Currency = FieldValue.Extracted("Euro"),
            Customer = FieldValue.Extracted(new Party("Pizzeria Napoli")),
            TotalGross = FieldValue.Extracted(Eur(170.00m)),
            TaxBreakdown = [new TaxBreakdownLine(7m, Eur(100.00m), Eur(7.00m)), new TaxBreakdownLine(19m, Eur(50.00m), Eur(9.00m))],
        };

        var flags = Validate(broken).Flags.Where(f => multiVariant.Contains(f.Code)).ToList();

        flags.Select(f => f.Code).Distinct().Count().ShouldBeGreaterThanOrEqualTo(5);
        flags.ShouldAllBe(f => f.Params != null && f.Params.ContainsKey(FlagParams.Reason));
    }

    [Fact]
    public void A_flag_without_parameters_has_none()
    {
        FlagCodes.Create(FlagCodes.TypeUnknown, "The document type could not be determined.").Params.ShouldBeNull();
        FlagCodes.Create(FlagCodes.TypeUnknown, "x", parameters: FlagParams.Of(("a", null))).Params.ShouldBeNull();
    }
}
