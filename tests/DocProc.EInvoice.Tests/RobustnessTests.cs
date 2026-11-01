using DocProc.Application.Abstractions;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice;
using static DocProc.EInvoice.Tests.FieldAssertions;

namespace DocProc.EInvoice.Tests;

/// <summary>Malformed input, invalid values, namespace quirks and the type-code rules, on minimal hand-built documents.</summary>
public class RobustnessTests
{
    private const string Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private const string Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    private static byte[] Ubl(string body, string root = "Invoice") => Fixtures.Utf8(
        $"<{root} xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:{root}-2\" xmlns:cbc=\"{Cbc}\" xmlns:cac=\"{Cac}\">{body}</{root}>");

    private static byte[] Cii(string exchangedDocument, string transaction = "") => Fixtures.Utf8(
        "<rsm:CrossIndustryInvoice xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\"" +
        " xmlns:ram=\"urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100\"" +
        " xmlns:udt=\"urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100\">" +
        $"<rsm:ExchangedDocument>{exchangedDocument}</rsm:ExchangedDocument>" +
        $"<rsm:SupplyChainTradeTransaction>{transaction}</rsm:SupplyChainTradeTransaction>" +
        "</rsm:CrossIndustryInvoice>");

    [Fact]
    public void Malformed_xml_throws_a_parse_exception_with_the_xml_error()
    {
        var detection = new EInvoiceDetection(EInvoiceFlavour.Ubl, null, "Invoice");
        var broken = Fixtures.Utf8("<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\"><ID>1</Invoice>");

        var ex = Should.Throw<EInvoiceParseException>(() => new EInvoiceParser().Parse(broken, detection));

        ex.Message.ShouldStartWith("The e-invoice XML is malformed");
        ex.InnerException.ShouldBeOfType<System.Xml.XmlException>();
    }

    [Fact]
    public void Parse_refuses_a_doctype_even_when_handed_a_detection()
    {
        var detection = new EInvoiceDetection(EInvoiceFlavour.Ubl, null, "Invoice");
        var xxe = Fixtures.Utf8(
            "<!DOCTYPE Invoice [<!ENTITY x SYSTEM \"file:///etc/hostname\">]>" +
            "<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\"><ID>&x;</ID></Invoice>");

        Should.Throw<EInvoiceParseException>(() => new EInvoiceParser().Parse(xxe, detection));
    }

    [Fact]
    public void Parse_refuses_a_detection_that_does_not_match_the_root()
    {
        var cii = Fixtures.Bytes(Fixtures.FacturXEn16931);
        var wrong = new EInvoiceDetection(EInvoiceFlavour.Ubl, null, "Invoice");

        var ex = Should.Throw<EInvoiceParseException>(() => new EInvoiceParser().Parse(cii, wrong));

        ex.Message.ShouldContain("CrossIndustryInvoice");
    }

    [Fact]
    public void An_empty_document_has_everything_missing_and_no_lines()
    {
        var invoice = Fixtures.Parse(Ubl(string.Empty));

        invoice.Kind.ShouldBeExtracted(InvoiceKind.Invoice);
        foreach (var (path, field) in invoice.EnumerateHeaderFields().Where(f => f.Path != "kind"))
        {
            field.Status.ShouldBe(FieldStatus.Missing, path);
        }

        invoice.Lines.ShouldBeEmpty();
        invoice.TaxBreakdown.ShouldBeEmpty();
        invoice.Charges.ShouldBeEmpty();
        invoice.Allowances.ShouldBeEmpty();
    }

    [Fact]
    public void Invalid_decimal_is_unreadable_with_the_raw_text()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>" +
            "<cac:LegalMonetaryTotal><cbc:LineExtensionAmount currencyID=\"EUR\">1,234.50</cbc:LineExtensionAmount></cac:LegalMonetaryTotal>" +
            "<cac:InvoiceLine><cbc:InvoicedQuantity unitCode=\"C62\">zwei</cbc:InvoicedQuantity></cac:InvoiceLine>"));

        invoice.SubtotalNet.ShouldBeUnreadable().ShouldBe("1,234.50");
        invoice.Lines.ShouldHaveSingleItem().Quantity.ShouldBeUnreadable().ShouldBe("zwei");
    }

    [Theory]
    [InlineData("27.10.2026")]
    [InlineData("2026-13-01")]
    [InlineData("20261027")]
    public void Invalid_ubl_date_is_unreadable(string text)
    {
        var invoice = Fixtures.Parse(Ubl($"<cbc:IssueDate>{text}</cbc:IssueDate>"));

        invoice.IssueDate.ShouldBeUnreadable().ShouldBe(text);
    }

    [Fact]
    public void Ubl_date_with_a_time_zone_suffix_is_read()
    {
        var invoice = Fixtures.Parse(Ubl("<cbc:IssueDate>2026-10-27+01:00</cbc:IssueDate><cbc:DueDate>2026-11-10Z</cbc:DueDate>"));

        invoice.IssueDate.ShouldBeExtracted(new DateOnly(2026, 10, 27));
        invoice.DueDate.ShouldBeExtracted(new DateOnly(2026, 11, 10));
    }

    [Theory]
    [InlineData("102", "2026-10-27")]
    [InlineData("610", "202610")]
    [InlineData("102", "20261345")]
    public void Invalid_or_unsupported_cii_date_is_unreadable(string format, string text)
    {
        var invoice = Fixtures.Parse(Cii($"<ram:IssueDateTime><udt:DateTimeString format=\"{format}\">{text}</udt:DateTimeString></ram:IssueDateTime>"));

        invoice.IssueDate.ShouldBeUnreadable().ShouldBe(text);
    }

    [Fact]
    public void Cii_date_without_a_format_attribute_is_read_as_format_102()
    {
        var invoice = Fixtures.Parse(Cii("<ram:IssueDateTime><udt:DateTimeString>20261027</udt:DateTimeString></ram:IssueDateTime>"));

        invoice.IssueDate.ShouldBeExtracted(new DateOnly(2026, 10, 27));
    }

    [Fact]
    public void Amount_without_any_currency_is_unreadable_rather_than_guessed()
    {
        var invoice = Fixtures.Parse(Ubl("<cac:LegalMonetaryTotal><cbc:PayableAmount>12.00</cbc:PayableAmount></cac:LegalMonetaryTotal>"));

        invoice.Currency.ShouldBeMissing();
        invoice.AmountDue.ShouldBeUnreadable().ShouldBe("12.00 (no currency)");
    }

    [Fact]
    public void Amount_currency_attribute_wins_over_the_document_currency()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cbc:DocumentCurrencyCode>eur</cbc:DocumentCurrencyCode>" +
            "<cac:LegalMonetaryTotal><cbc:PayableAmount currencyID=\"CHF\">12.00</cbc:PayableAmount><cbc:TaxInclusiveAmount>12.00</cbc:TaxInclusiveAmount></cac:LegalMonetaryTotal>"));

        invoice.Currency.ShouldBeExtracted("EUR");
        invoice.AmountDue.ShouldBeExtracted(new Money(12.00m, "CHF"));
        invoice.TotalGross.ShouldBeExtracted(new Money(12.00m, "EUR"));
    }

    [Theory]
    [InlineData("380", InvoiceKind.Invoice)]
    [InlineData("381", InvoiceKind.CreditNote)]
    [InlineData("384", InvoiceKind.Invoice)]
    [InlineData("386", InvoiceKind.Invoice)]
    [InlineData("389", InvoiceKind.Invoice)]
    [InlineData("261", InvoiceKind.CreditNote)]
    public void Ubl_invoice_root_follows_its_type_code(string code, InvoiceKind kind)
    {
        Fixtures.Parse(Ubl($"<cbc:InvoiceTypeCode>{code}</cbc:InvoiceTypeCode>")).Kind.ShouldBeExtracted(kind);
    }

    [Fact]
    public void Ubl_credit_note_root_is_a_credit_note_whatever_the_code_says()
    {
        Fixtures.Parse(Ubl("<cbc:CreditNoteTypeCode>380</cbc:CreditNoteTypeCode>", "CreditNote")).Kind.ShouldBeExtracted(InvoiceKind.CreditNote);
        Fixtures.Parse(Ubl(string.Empty, "CreditNote")).Kind.ShouldBeExtracted(InvoiceKind.CreditNote);
    }

    [Fact]
    public void Unknown_type_code_is_unreadable_and_an_absent_cii_type_code_is_missing()
    {
        Fixtures.Parse(Ubl("<cbc:InvoiceTypeCode>999</cbc:InvoiceTypeCode>")).Kind.ShouldBeUnreadable().ShouldBe("999");
        Fixtures.Parse(Cii("<ram:TypeCode>999</ram:TypeCode>")).Kind.ShouldBeUnreadable().ShouldBe("999");
        Fixtures.Parse(Cii("<ram:ID>1</ram:ID>")).Kind.ShouldBeMissing();
        Fixtures.Parse(Cii("<ram:TypeCode>381</ram:TypeCode>")).Kind.ShouldBeExtracted(InvoiceKind.CreditNote);
    }

    [Fact]
    public void Non_numeric_line_ids_fall_back_to_the_position()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cac:InvoiceLine><cbc:ID>A</cbc:ID></cac:InvoiceLine>" +
            "<cac:InvoiceLine><cbc:ID>7</cbc:ID></cac:InvoiceLine>" +
            "<cac:InvoiceLine></cac:InvoiceLine>"));

        invoice.Lines.Select(l => l.LineNumber).ShouldBe([1, 7, 3]);
    }

    [Fact]
    public void Service_period_with_only_one_end_is_unreadable()
    {
        var invoice = Fixtures.Parse(Ubl("<cac:InvoicePeriod><cbc:StartDate>2026-10-01</cbc:StartDate></cac:InvoicePeriod>"));

        invoice.ServicePeriod.ShouldBeUnreadable().ShouldBe("2026-10-01 - ?");
    }

    [Fact]
    public void Line_allowance_with_only_a_percentage_becomes_a_discount_percent()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>" +
            "<cac:InvoiceLine><cac:AllowanceCharge><cbc:ChargeIndicator>false</cbc:ChargeIndicator><cbc:MultiplierFactorNumeric>5</cbc:MultiplierFactorNumeric></cac:AllowanceCharge></cac:InvoiceLine>"));

        var line = invoice.Lines.ShouldHaveSingleItem();
        line.DiscountPercent.ShouldBeExtracted(5m);
        line.DiscountAmount.ShouldBeMissing();
    }

    [Fact]
    public void Line_charges_are_not_discounts()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>" +
            "<cac:InvoiceLine><cac:AllowanceCharge><cbc:ChargeIndicator>true</cbc:ChargeIndicator><cbc:Amount currencyID=\"EUR\">1.00</cbc:Amount></cac:AllowanceCharge></cac:InvoiceLine>"));

        var line = invoice.Lines.ShouldHaveSingleItem();
        line.DiscountAmount.ShouldBeMissing();
        line.DiscountPercent.ShouldBeMissing();
    }

    [Fact]
    public void Unknown_unit_code_maps_to_other_and_keeps_the_raw_code()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cac:InvoiceLine><cbc:InvoicedQuantity unitCode=\"ZZ\">1</cbc:InvoicedQuantity></cac:InvoiceLine>" +
            "<cac:InvoiceLine><cbc:InvoicedQuantity>1</cbc:InvoicedQuantity></cac:InvoiceLine>"));

        invoice.Lines[0].Unit.ShouldBeExtracted(UnitOfMeasure.Other);
        invoice.Lines[0].UnitRaw.ShouldBe("ZZ");
        invoice.Lines[1].Unit.ShouldBeMissing();
        invoice.Lines[1].UnitRaw.ShouldBeNull();
    }

    [Fact]
    public void Standard_item_identification_with_another_scheme_is_not_a_gtin()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cac:InvoiceLine><cac:Item><cac:StandardItemIdentification><cbc:ID schemeID=\"0199\">1234</cbc:ID></cac:StandardItemIdentification></cac:Item></cac:InvoiceLine>" +
            "<cac:InvoiceLine><cac:Item><cac:StandardItemIdentification><cbc:ID>4012345678901</cbc:ID></cac:StandardItemIdentification></cac:Item></cac:InvoiceLine>"));

        invoice.Lines[0].Gtin.ShouldBeMissing();
        invoice.Lines[1].Gtin.ShouldBeMissing();
    }

    [Fact]
    public void Inner_elements_are_matched_by_local_name_regardless_of_prefix_or_namespace()
    {
        var invoice = Fixtures.Parse(Fixtures.Utf8(
            "<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\" xmlns:x=\"urn:example:stale\">" +
            "<ID>RE-1</ID><x:IssueDate>2026-10-27</x:IssueDate><LegalMonetaryTotal><PayableAmount currencyID=\"EUR\">1.00</PayableAmount></LegalMonetaryTotal></Invoice>"));

        invoice.InvoiceNumber.ShouldBeExtracted(DocumentIdentifier.Create("RE-1"));
        invoice.IssueDate.ShouldBeExtracted(new DateOnly(2026, 10, 27));
        invoice.AmountDue.ShouldBeExtracted(new Money(1.00m, "EUR"));
    }

    [Fact]
    public void Ubl_tax_total_in_the_accounting_currency_is_ignored_in_favour_of_the_one_with_rows()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>" +
            "<cac:TaxTotal><cbc:TaxAmount currencyID=\"SEK\">110.00</cbc:TaxAmount></cac:TaxTotal>" +
            "<cac:TaxTotal><cbc:TaxAmount currencyID=\"EUR\">10.00</cbc:TaxAmount>" +
            "<cac:TaxSubtotal><cbc:TaxableAmount currencyID=\"EUR\">100.00</cbc:TaxableAmount><cbc:TaxAmount currencyID=\"EUR\">10.00</cbc:TaxAmount>" +
            "<cac:TaxCategory><cbc:ID>Z</cbc:ID></cac:TaxCategory></cac:TaxSubtotal></cac:TaxTotal>"));

        invoice.TotalTax.ShouldBeExtracted(Eur(10.00m));
        invoice.TaxBreakdown.ShouldBe([new TaxBreakdownLine(0m, Eur(100.00m), Eur(10.00m), TaxCategory.Zero)]);
    }

    [Fact]
    public void Cii_tax_total_in_the_document_currency_is_chosen_and_unreadable_rows_are_dropped()
    {
        var invoice = Fixtures.Parse(Cii(string.Empty,
            "<ram:ApplicableHeaderTradeSettlement><ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>" +
            "<ram:ApplicableTradeTax><ram:CalculatedAmount>abc</ram:CalculatedAmount><ram:BasisAmount>100.00</ram:BasisAmount><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:ApplicableTradeTax>" +
            "<ram:ApplicableTradeTax><ram:CalculatedAmount>0.00</ram:CalculatedAmount><ram:BasisAmount>50.00</ram:BasisAmount><ram:CategoryCode>AE</ram:CategoryCode></ram:ApplicableTradeTax>" +
            "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>" +
            "<ram:TaxTotalAmount currencyID=\"SEK\">210.00</ram:TaxTotalAmount><ram:TaxTotalAmount currencyID=\"EUR\">19.00</ram:TaxTotalAmount>" +
            "</ram:SpecifiedTradeSettlementHeaderMonetarySummation></ram:ApplicableHeaderTradeSettlement>"));

        invoice.TotalTax.ShouldBeExtracted(Eur(19.00m));
        invoice.TaxBreakdown.ShouldBe([new TaxBreakdownLine(0m, Eur(50.00m), Eur(0.00m), TaxCategory.ReverseCharge)]);
    }

    [Fact]
    public void Cii_payment_discount_terms_and_batch_id_are_read()
    {
        var invoice = Fixtures.Parse(Cii("<ram:TypeCode>380</ram:TypeCode>",
            "<ram:IncludedSupplyChainTradeLineItem><ram:SpecifiedTradeProduct><ram:IndividualTradeProductInstance><ram:BatchID>B-77</ram:BatchID></ram:IndividualTradeProductInstance></ram:SpecifiedTradeProduct></ram:IncludedSupplyChainTradeLineItem>" +
            "<ram:ApplicableHeaderTradeSettlement><ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>" +
            "<ram:SpecifiedTradePaymentTerms><ram:Description>2 % Skonto innerhalb 10 Tagen</ram:Description>" +
            "<ram:ApplicableTradePaymentDiscountTerms><ram:BasisPeriodMeasure unitCode=\"DAY\">10</ram:BasisPeriodMeasure><ram:CalculationPercent>2.00</ram:CalculationPercent></ram:ApplicableTradePaymentDiscountTerms>" +
            "</ram:SpecifiedTradePaymentTerms></ram:ApplicableHeaderTradeSettlement>"));

        var terms = invoice.PaymentTerms.ShouldBeExtracted(invoice.PaymentTerms.Value!);
        terms.DiscountPercent.ShouldBe(2.00m);
        terms.DueDate.ShouldBeNull();
        terms.RawText.ShouldBe("2 % Skonto innerhalb 10 Tagen");
        invoice.Lines.ShouldHaveSingleItem().LotNumber.ShouldBeExtracted("B-77");
    }

    [Fact]
    public void Unparsable_best_before_attribute_is_unreadable()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cac:InvoiceLine><cac:Item><cac:AdditionalItemProperty><cbc:Name>MHD</cbc:Name><cbc:Value>Ende Nov.</cbc:Value></cac:AdditionalItemProperty></cac:Item></cac:InvoiceLine>"));

        invoice.Lines.ShouldHaveSingleItem().BestBefore.ShouldBeUnreadable().ShouldBe("Ende Nov.");
    }

    [Theory]
    [InlineData("0204", "04011000-12345-67")]
    [InlineData("0088", "4012345000004")]
    public void Cii_electronic_address_under_a_non_email_scheme_is_not_an_email(string scheme, string id)
    {
        var invoice = Fixtures.Parse(Cii(string.Empty,
            "<ram:ApplicableHeaderTradeAgreement>" +
            $"<ram:SellerTradeParty><ram:Name>Lieferant</ram:Name><ram:URIUniversalCommunication><ram:URIID schemeID=\"{scheme}\">{id}</ram:URIID></ram:URIUniversalCommunication></ram:SellerTradeParty>" +
            $"<ram:BuyerTradeParty><ram:Name>Kunde</ram:Name><ram:URIUniversalCommunication><ram:URIID schemeID=\"{scheme}\">{id}</ram:URIID></ram:URIUniversalCommunication></ram:BuyerTradeParty>" +
            "</ram:ApplicableHeaderTradeAgreement>"));

        invoice.Supplier.Value!.Email.ShouldBeNull();
        invoice.Customer.Value!.Email.ShouldBeNull();
    }

    [Fact]
    public void Cii_electronic_address_under_scheme_em_is_the_email_unless_a_contact_email_is_printed()
    {
        var invoice = Fixtures.Parse(Cii(string.Empty,
            "<ram:ApplicableHeaderTradeAgreement>" +
            "<ram:SellerTradeParty><ram:Name>Lieferant</ram:Name><ram:URIUniversalCommunication><ram:URIID schemeID=\"EM\">faktura@lieferant.example</ram:URIID></ram:URIUniversalCommunication></ram:SellerTradeParty>" +
            "</ram:ApplicableHeaderTradeAgreement>"));

        invoice.Supplier.Value!.Email.ShouldBe("faktura@lieferant.example");
    }

    [Fact]
    public void Ubl_iban_and_bic_come_from_the_first_payment_means_that_carries_an_account()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cac:AccountingSupplierParty><cac:Party><cac:PartyName><cbc:Name>Lieferant</cbc:Name></cac:PartyName></cac:Party></cac:AccountingSupplierParty>" +
            "<cac:PaymentMeans><cbc:PaymentMeansCode>30</cbc:PaymentMeansCode></cac:PaymentMeans>" +
            "<cac:PaymentMeans><cbc:PaymentMeansCode>58</cbc:PaymentMeansCode><cbc:PaymentID>RE-1</cbc:PaymentID>" +
            "<cac:PayeeFinancialAccount><cbc:ID>DE75512108001245126199</cbc:ID><cac:FinancialInstitutionBranch><cbc:ID>SOGEDEFFXXX</cbc:ID></cac:FinancialInstitutionBranch></cac:PayeeFinancialAccount></cac:PaymentMeans>"));

        invoice.Supplier.Value!.Iban.ShouldBe("DE75512108001245126199");
        invoice.Supplier.Value.Bic.ShouldBe("SOGEDEFFXXX");
        var terms = invoice.PaymentTerms.Value.ShouldNotBeNull();
        terms.PaymentMeans.ShouldBe(PaymentMeans.BankTransfer);
        terms.PaymentReference.ShouldBe("RE-1");
        terms.Iban.ShouldBe("DE75512108001245126199");
        terms.Bic.ShouldBe("SOGEDEFFXXX");
    }

    [Fact]
    public void Cii_iban_and_bic_come_from_the_first_payment_means_that_carries_an_account()
    {
        var invoice = Fixtures.Parse(Cii(string.Empty,
            "<ram:ApplicableHeaderTradeAgreement><ram:SellerTradeParty><ram:Name>Lieferant</ram:Name></ram:SellerTradeParty></ram:ApplicableHeaderTradeAgreement>" +
            "<ram:ApplicableHeaderTradeSettlement><ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>" +
            "<ram:SpecifiedTradeSettlementPaymentMeans><ram:TypeCode>30</ram:TypeCode></ram:SpecifiedTradeSettlementPaymentMeans>" +
            "<ram:SpecifiedTradeSettlementPaymentMeans><ram:TypeCode>58</ram:TypeCode>" +
            "<ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE75512108001245126199</ram:IBANID></ram:PayeePartyCreditorFinancialAccount>" +
            "<ram:PayeeSpecifiedCreditorFinancialInstitution><ram:BICID>SOGEDEFFXXX</ram:BICID></ram:PayeeSpecifiedCreditorFinancialInstitution>" +
            "</ram:SpecifiedTradeSettlementPaymentMeans></ram:ApplicableHeaderTradeSettlement>"));

        invoice.Supplier.Value!.Iban.ShouldBe("DE75512108001245126199");
        invoice.Supplier.Value.Bic.ShouldBe("SOGEDEFFXXX");
        var terms = invoice.PaymentTerms.Value.ShouldNotBeNull();
        terms.PaymentMeans.ShouldBe(PaymentMeans.BankTransfer);
        terms.Iban.ShouldBe("DE75512108001245126199");
        terms.Bic.ShouldBe("SOGEDEFFXXX");
    }

    [Fact]
    public void Price_amount_too_large_to_divide_by_the_base_quantity_is_unreadable_and_never_throws()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>" +
            "<cac:InvoiceLine><cbc:InvoicedQuantity unitCode=\"C62\">1</cbc:InvoicedQuantity>" +
            "<cac:Price><cbc:PriceAmount currencyID=\"EUR\">79228162514264337593543950335</cbc:PriceAmount><cbc:BaseQuantity unitCode=\"C62\">0.0000001</cbc:BaseQuantity></cac:Price></cac:InvoiceLine>"));

        invoice.Lines.ShouldHaveSingleItem().UnitPrice.ShouldBeUnreadable().ShouldBe("79228162514264337593543950335 / 0.0000001");
    }

    [Fact]
    public void Price_per_base_quantity_in_another_unit_than_the_invoiced_quantity_is_unreadable_with_the_printed_figures()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>" +
            "<cac:InvoiceLine><cbc:InvoicedQuantity unitCode=\"KGM\">2</cbc:InvoicedQuantity><cbc:LineExtensionAmount currencyID=\"EUR\">200.00</cbc:LineExtensionAmount>" +
            "<cac:Price><cbc:PriceAmount currencyID=\"EUR\">10.00</cbc:PriceAmount><cbc:BaseQuantity unitCode=\"GRM\">100</cbc:BaseQuantity></cac:Price></cac:InvoiceLine>" +
            "<cac:InvoiceLine><cbc:InvoicedQuantity unitCode=\"KGM\">2</cbc:InvoicedQuantity>" +
            "<cac:Price><cbc:PriceAmount currencyID=\"EUR\">10.00</cbc:PriceAmount><cbc:BaseQuantity>100</cbc:BaseQuantity></cac:Price></cac:InvoiceLine>"));

        invoice.Lines[0].UnitPrice.ShouldBeUnreadable().ShouldBe("10.00 per 100 GRM (quantity in KGM)");
        invoice.Lines[1].UnitPrice.ShouldBeExtracted(Eur(0.10m));
    }

    [Fact]
    public void Ubl_price_level_allowance_makes_the_gross_price_the_unit_price_and_feeds_the_discount_amount()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>" +
            "<cac:InvoiceLine><cbc:InvoicedQuantity unitCode=\"C62\">10</cbc:InvoicedQuantity><cbc:LineExtensionAmount currencyID=\"EUR\">80.00</cbc:LineExtensionAmount>" +
            "<cac:Price><cbc:PriceAmount currencyID=\"EUR\">8.00</cbc:PriceAmount>" +
            "<cac:AllowanceCharge><cbc:ChargeIndicator>false</cbc:ChargeIndicator><cbc:Amount currencyID=\"EUR\">2.00</cbc:Amount><cbc:BaseAmount currencyID=\"EUR\">10.00</cbc:BaseAmount></cac:AllowanceCharge>" +
            "</cac:Price></cac:InvoiceLine>"));

        var line = invoice.Lines.ShouldHaveSingleItem();
        line.UnitPrice.ShouldBeExtracted(Eur(10.00m));
        line.DiscountAmount.ShouldBeExtracted(Eur(20.00m));
        line.DiscountAmount.Source!.RawText.ShouldBe("2.00 × 10");
        (line.Quantity.Value * line.UnitPrice.Value!.Amount - line.DiscountAmount.Value!.Amount).ShouldBe(line.LineNet.Value!.Amount);
    }

    [Fact]
    public void Cii_price_level_allowance_without_a_gross_price_adds_it_back_to_the_net_price()
    {
        var invoice = Fixtures.Parse(Cii(string.Empty,
            "<ram:IncludedSupplyChainTradeLineItem>" +
            "<ram:SpecifiedLineTradeAgreement><ram:GrossPriceProductTradePrice>" +
            "<ram:AppliedTradeAllowanceCharge><ram:ChargeIndicator><udt:Indicator>false</udt:Indicator></ram:ChargeIndicator><ram:ActualAmount>2.00</ram:ActualAmount></ram:AppliedTradeAllowanceCharge>" +
            "</ram:GrossPriceProductTradePrice><ram:NetPriceProductTradePrice><ram:ChargeAmount>8.00</ram:ChargeAmount></ram:NetPriceProductTradePrice></ram:SpecifiedLineTradeAgreement>" +
            "<ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode=\"C62\">10</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery>" +
            "<ram:SpecifiedLineTradeSettlement><ram:SpecifiedTradeAllowanceCharge><ram:ChargeIndicator><udt:Indicator>false</udt:Indicator></ram:ChargeIndicator><ram:ActualAmount>5.00</ram:ActualAmount></ram:SpecifiedTradeAllowanceCharge>" +
            "<ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>75.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation></ram:SpecifiedLineTradeSettlement>" +
            "</ram:IncludedSupplyChainTradeLineItem>" +
            "<ram:ApplicableHeaderTradeSettlement><ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode></ram:ApplicableHeaderTradeSettlement>"));

        var line = invoice.Lines.ShouldHaveSingleItem();
        line.UnitPrice.ShouldBeExtracted(Eur(10.00m));
        line.UnitPrice.Source!.RawText.ShouldBe("8.00 + 2.00");
        line.DiscountAmount.ShouldBeExtracted(Eur(25.00m));
        line.DiscountAmount.Source!.RawText.ShouldBe("5.00 + 2.00 × 10");
    }

    [Fact]
    public void Tax_breakdown_row_with_an_unparsable_percent_is_dropped_rather_than_rated_zero()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>" +
            "<cac:TaxTotal><cbc:TaxAmount currencyID=\"EUR\">19.00</cbc:TaxAmount>" +
            "<cac:TaxSubtotal><cbc:TaxableAmount currencyID=\"EUR\">100.00</cbc:TaxableAmount><cbc:TaxAmount currencyID=\"EUR\">19.00</cbc:TaxAmount>" +
            "<cac:TaxCategory><cbc:ID>S</cbc:ID><cbc:Percent>19,00</cbc:Percent></cac:TaxCategory></cac:TaxSubtotal>" +
            "<cac:TaxSubtotal><cbc:TaxableAmount currencyID=\"EUR\">50.00</cbc:TaxableAmount><cbc:TaxAmount currencyID=\"EUR\">0.00</cbc:TaxAmount>" +
            "<cac:TaxCategory><cbc:ID>E</cbc:ID></cac:TaxCategory></cac:TaxSubtotal></cac:TaxTotal>"));

        invoice.TaxBreakdown.ShouldBe([new TaxBreakdownLine(0m, Eur(50.00m), Eur(0.00m), TaxCategory.Exempt)]);
    }

    [Theory]
    [InlineData("2026-10-27+01:00trailing")]
    [InlineData("2026-10-27-garbage")]
    [InlineData("2026-10-27Z1")]
    [InlineData("2026-10-27+1:00")]
    public void Ubl_date_with_an_invalid_suffix_is_unreadable(string text)
    {
        var invoice = Fixtures.Parse(Ubl($"<cbc:IssueDate>{text}</cbc:IssueDate>"));

        invoice.IssueDate.ShouldBeUnreadable().ShouldBe(text);
    }

    [Fact]
    public void Currency_that_is_not_a_three_letter_code_is_unreadable_and_so_are_the_amounts_keyed_on_it()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cbc:DocumentCurrencyCode>Euro</cbc:DocumentCurrencyCode>" +
            "<cac:LegalMonetaryTotal><cbc:PayableAmount>12.00</cbc:PayableAmount><cbc:TaxInclusiveAmount currencyID=\"CHF\">12.00</cbc:TaxInclusiveAmount></cac:LegalMonetaryTotal>"));

        invoice.Currency.ShouldBeUnreadable().ShouldBe("Euro");
        invoice.AmountDue.ShouldBeUnreadable().ShouldBe("12.00 (invalid currency 'Euro')");
        invoice.TotalGross.ShouldBeExtracted(new Money(12.00m, "CHF"));
    }

    [Fact]
    public void Buyer_reference_is_not_an_order_reference_in_either_syntax()
    {
        Fixtures.Parse(Ubl("<cbc:BuyerReference>04011000-12345-67</cbc:BuyerReference><cac:OrderReference><cbc:ID>PO-1</cbc:ID></cac:OrderReference>"))
            .OrderReferences.Select(r => r.Value!.Verbatim).ShouldBe(["PO-1"]);
        Fixtures.Parse(Cii(string.Empty, "<ram:ApplicableHeaderTradeAgreement><ram:BuyerReference>04011000-12345-67</ram:BuyerReference></ram:ApplicableHeaderTradeAgreement>"))
            .OrderReferences.ShouldBeEmpty();
    }

    [Fact]
    public void Customer_direct_debit_account_is_read_from_the_payment_mandate()
    {
        var invoice = Fixtures.Parse(Ubl(
            "<cac:AccountingCustomerParty><cac:Party><cac:PartyName><cbc:Name>Gasthaus zur Post</cbc:Name></cac:PartyName></cac:Party></cac:AccountingCustomerParty>" +
            "<cac:PaymentMeans><cbc:PaymentMeansCode>59</cbc:PaymentMeansCode><cac:PaymentMandate><cac:PayerFinancialAccount><cbc:ID>DE12500105170648489890</cbc:ID></cac:PayerFinancialAccount></cac:PaymentMandate></cac:PaymentMeans>"));

        invoice.Customer.Value!.Iban.ShouldBe("DE12500105170648489890");
        invoice.PaymentTerms.Value!.PaymentMeans.ShouldBe(PaymentMeans.DirectDebit);
        invoice.PaymentTerms.Value.Iban.ShouldBeNull();
    }
}
