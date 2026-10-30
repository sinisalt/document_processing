using System.Xml.Linq;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice.Cii;

/// <summary>CII IncludedSupplyChainTradeLineItem to the canonical <see cref="InvoiceLine"/>.</summary>
internal static class CiiLineMapper
{
    public static InvoiceLine[] Lines(XElement? transaction, string? currency) =>
        transaction.Children("IncludedSupplyChainTradeLineItem").Select((line, index) => Map(line, index + 1, currency)).ToArray();

    private static InvoiceLine Map(XElement line, int position, string? currency)
    {
        var product = line.Child("SpecifiedTradeProduct");
        var agreement = line.Child("SpecifiedLineTradeAgreement");
        var delivery = line.Child("SpecifiedLineTradeDelivery");
        var settlement = line.Child("SpecifiedLineTradeSettlement");
        var quantity = delivery.Child("BilledQuantity");
        var unitCode = quantity.Attr("unitCode");
        var netPrice = agreement.Child("NetPriceProductTradePrice");
        var grossPrice = agreement.Child("GrossPriceProductTradePrice");
        var basisQuantity = netPrice.Child("BasisQuantity");
        var globalId = product.Child("GlobalID");
        var attributes = product.Children("ApplicableProductCharacteristic")
            .Select(c => new ItemAttribute(c.Child("Description").Text(), c.Child("Value").Text()))
            .ToArray();
        var pricing = LineHelpers.Price(new LinePricing(
            NetPrice: XmlFields.Amount(netPrice.Child("ChargeAmount"), currency),
            GrossPrice: XmlFields.Amount(grossPrice.Child("ChargeAmount"), currency),
            PriceAllowances: Allowances(grossPrice.Children("AppliedTradeAllowanceCharge"), currency),
            BaseQuantity: XmlFields.Number(basisQuantity),
            BaseUnitCode: basisQuantity.Attr("unitCode"),
            Quantity: XmlFields.Number(quantity),
            UnitCode: unitCode,
            LineAllowances: Allowances(settlement.Children("SpecifiedTradeAllowanceCharge"), currency)));

        return new InvoiceLine
        {
            LineNumber = LineHelpers.LineNumber(line.Path("AssociatedDocumentLineDocument", "LineID").Text(), position),
            SupplierSku = XmlFields.Text(product.Child("SellerAssignedID")),
            Gtin = LineHelpers.Gtin(globalId.Text(), globalId.Attr("schemeID")),
            Description = XmlFields.Text(product.Child("Name") ?? product.Child("Description")),
            Quantity = XmlFields.Number(quantity),
            Unit = LineHelpers.Unit(unitCode),
            UnitRaw = unitCode,
            UnitPrice = pricing.UnitPrice,
            DiscountAmount = pricing.DiscountAmount,
            DiscountPercent = pricing.DiscountPercent,
            LineNet = XmlFields.Amount(settlement.Path("SpecifiedTradeSettlementLineMonetarySummation", "LineTotalAmount"), currency),
            TaxRate = XmlFields.Number(settlement.Path("ApplicableTradeTax", "RateApplicablePercent")),
            LotNumber = LineHelpers.Lot(attributes, product.Path("IndividualTradeProductInstance", "BatchID").Text()),
            BestBefore = LineHelpers.BestBefore(attributes),
        };
    }

    /// <summary>Allowance entries (indicator false) at line level (SpecifiedTradeAllowanceCharge) or price level (AppliedTradeAllowanceCharge).</summary>
    private static LineAllowance[] Allowances(IEnumerable<XElement> entries, string? currency) =>
        entries
            .Where(a => XmlFields.Indicator(a.Path("ChargeIndicator", "Indicator").Text()) == false)
            .Select(a => new LineAllowance(
                a.Child("ActualAmount").Text(),
                a.Child("ActualAmount").Attr("currencyID") ?? currency,
                a.Child("CalculationPercent").Text()))
            .ToArray();
}
