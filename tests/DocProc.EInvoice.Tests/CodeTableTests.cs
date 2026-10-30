using DocProc.Domain.Common;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice.Codes;

namespace DocProc.EInvoice.Tests;

public class CodeTableTests
{
    [Theory]
    [InlineData("KGM", UnitOfMeasure.Kilogram)]
    [InlineData("GRM", UnitOfMeasure.Gram)]
    [InlineData("LTR", UnitOfMeasure.Litre)]
    [InlineData("MLT", UnitOfMeasure.Millilitre)]
    [InlineData("C62", UnitOfMeasure.Piece)]
    [InlineData("H87", UnitOfMeasure.Piece)]
    [InlineData("EA", UnitOfMeasure.Piece)]
    [InlineData("BX", UnitOfMeasure.Box)]
    [InlineData("CS", UnitOfMeasure.Case)]
    [InlineData("CR", UnitOfMeasure.Crate)]
    [InlineData("PF", UnitOfMeasure.Pallet)]
    [InlineData("PX", UnitOfMeasure.Pallet)]
    [InlineData("HUR", UnitOfMeasure.Hour)]
    [InlineData("kgm", UnitOfMeasure.Kilogram)]
    [InlineData(" C62 ", UnitOfMeasure.Piece)]
    [InlineData("MTR", UnitOfMeasure.Other)]
    [InlineData("", UnitOfMeasure.Other)]
    [InlineData(null, UnitOfMeasure.Other)]
    public void Unece_rec20_unit_codes(string? code, UnitOfMeasure expected)
    {
        UnitCodes.ToUnit(code).ShouldBe(expected);
    }

    [Theory]
    [InlineData("S", TaxCategory.Standard)]
    [InlineData("Z", TaxCategory.Zero)]
    [InlineData("E", TaxCategory.Exempt)]
    [InlineData("AE", TaxCategory.ReverseCharge)]
    [InlineData("O", TaxCategory.OutsideScope)]
    [InlineData("K", TaxCategory.IntraCommunity)]
    [InlineData("G", TaxCategory.Export)]
    [InlineData("ae", TaxCategory.ReverseCharge)]
    [InlineData("L", null)]
    [InlineData(null, null)]
    public void Uncl5305_tax_categories(string? code, TaxCategory? expected)
    {
        TaxCategoryCodes.ToCategory(code).ShouldBe(expected);
    }

    [Theory]
    [InlineData("380", InvoiceKind.Invoice)]
    [InlineData("381", InvoiceKind.CreditNote)]
    [InlineData("384", InvoiceKind.Invoice)]
    [InlineData("386", InvoiceKind.Invoice)]
    [InlineData("389", InvoiceKind.Invoice)]
    [InlineData("261", InvoiceKind.CreditNote)]
    [InlineData("396", InvoiceKind.CreditNote)]
    [InlineData("875", InvoiceKind.Invoice)]
    [InlineData(" 380 ", InvoiceKind.Invoice)]
    [InlineData("999", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Uncl1001_document_type_codes(string? code, InvoiceKind? expected)
    {
        DocumentTypeCodes.ToKind(code).ShouldBe(expected);
    }

    [Theory]
    [InlineData("58", PaymentMeans.BankTransfer)]
    [InlineData("30", PaymentMeans.BankTransfer)]
    [InlineData("59", PaymentMeans.DirectDebit)]
    [InlineData("49", PaymentMeans.DirectDebit)]
    [InlineData("48", PaymentMeans.Card)]
    [InlineData("54", PaymentMeans.Card)]
    [InlineData("10", PaymentMeans.Cash)]
    [InlineData("ZZZ", PaymentMeans.Other)]
    [InlineData("1", PaymentMeans.Other)]
    [InlineData(null, null)]
    public void Uncl4461_payment_means_codes(string? code, PaymentMeans? expected)
    {
        PaymentMeansCodes.ToPaymentMeans(code).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Chargennummer", true, false)]
    [InlineData("lot number", true, false)]
    [InlineData("BATCH", true, false)]
    [InlineData("MHD", false, true)]
    [InlineData("Best before", false, true)]
    [InlineData("Mindesthaltbarkeitsdatum", false, true)]
    [InlineData("Colour", false, false)]
    [InlineData(null, false, false)]
    public void Item_property_names(string? name, bool lot, bool bestBefore)
    {
        ItemPropertyNames.IsLot(name).ShouldBe(lot);
        ItemPropertyNames.IsBestBefore(name).ShouldBe(bestBefore);
    }
}
