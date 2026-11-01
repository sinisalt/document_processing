using System.Xml.Linq;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice.Ubl;

/// <summary>UBL InvoiceLine / CreditNoteLine to the canonical <see cref="InvoiceLine"/>.</summary>
internal static class UblLineMapper
{
    public static InvoiceLine[] Lines(XElement root, bool isCreditNote, string? currency)
    {
        var lineName = isCreditNote ? "CreditNoteLine" : "InvoiceLine";
        var quantityName = isCreditNote ? "CreditedQuantity" : "InvoicedQuantity";
        return root.Children(lineName).Select((line, index) => Map(line, index + 1, quantityName, currency)).ToArray();
    }

    private static InvoiceLine Map(XElement line, int position, string quantityName, string? currency)
    {
        var item = line.Child("Item");
        var price = line.Child("Price");
        var quantity = line.Child(quantityName);
        var unitCode = quantity.Attr("unitCode");
        var baseQuantity = price.Child("BaseQuantity");
        var standardId = item.Path("StandardItemIdentification", "ID");
        var attributes = item.Children("AdditionalItemProperty")
            .Select(p => new ItemAttribute(p.Child("Name").Text(), p.Child("Value").Text()))
            .ToArray();
        var priceAllowances = Allowances(price, currency);
        var pricing = LineHelpers.Price(new LinePricing(
            NetPrice: XmlFields.Amount(price.Child("PriceAmount"), currency),
            GrossPrice: XmlFields.Amount(Allowances(price).Select(a => a.Child("BaseAmount")).FirstOrDefault(b => b is not null), currency),
            PriceAllowances: priceAllowances,
            BaseQuantity: XmlFields.Number(baseQuantity),
            BaseUnitCode: baseQuantity.Attr("unitCode"),
            Quantity: XmlFields.Number(quantity),
            UnitCode: unitCode,
            LineAllowances: Allowances(line, currency)));

        return new InvoiceLine
        {
            LineNumber = LineHelpers.LineNumber(line.Child("ID").Text(), position),
            SupplierSku = XmlFields.Text(item.Path("SellersItemIdentification", "ID")),
            Gtin = LineHelpers.Gtin(standardId.Text(), standardId.Attr("schemeID")),
            Description = XmlFields.Text(item.Child("Name") ?? item.Child("Description")),
            Quantity = XmlFields.Number(quantity),
            Unit = LineHelpers.Unit(unitCode),
            UnitRaw = unitCode,
            UnitPrice = pricing.UnitPrice,
            DiscountAmount = pricing.DiscountAmount,
            DiscountPercent = pricing.DiscountPercent,
            LineNet = XmlFields.Amount(line.Child("LineExtensionAmount"), currency),
            TaxRate = XmlFields.Number(item.Path("ClassifiedTaxCategory", "Percent")),
            LotNumber = LineHelpers.Lot(attributes, batchId: null),
            BestBefore = LineHelpers.BestBefore(attributes),
        };
    }

    /// <summary>AllowanceCharge entries with the charge indicator false, under a line (BG-27) or under its Price (BT-147).</summary>
    private static XElement[] Allowances(XElement? parent) =>
        parent.Children("AllowanceCharge")
            .Where(a => XmlFields.Indicator(a.Child("ChargeIndicator").Text()) == false)
            .ToArray();

    private static LineAllowance[] Allowances(XElement? parent, string? currency) =>
        Allowances(parent)
            .Select(a => new LineAllowance(
                a.Child("Amount").Text(),
                a.Child("Amount").Attr("currencyID") ?? currency,
                a.Child("MultiplierFactorNumeric").Text()))
            .ToArray();
}
