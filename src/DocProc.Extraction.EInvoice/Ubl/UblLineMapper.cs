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
        var standardId = item.Path("StandardItemIdentification", "ID");
        var attributes = item.Children("AdditionalItemProperty")
            .Select(p => new ItemAttribute(p.Child("Name").Text(), p.Child("Value").Text()))
            .ToArray();
        var allowances = line.Children("AllowanceCharge")
            .Where(a => XmlFields.Indicator(a.Child("ChargeIndicator").Text()) == false)
            .Select(a => new LineAllowance(
                a.Child("Amount").Text(),
                a.Child("Amount").Attr("currencyID") ?? currency,
                a.Child("MultiplierFactorNumeric").Text()))
            .ToArray();
        var (discountAmount, discountPercent) = LineHelpers.Discounts(allowances);

        return new InvoiceLine
        {
            LineNumber = LineHelpers.LineNumber(line.Child("ID").Text(), position),
            SupplierSku = XmlFields.Text(item.Path("SellersItemIdentification", "ID")),
            Gtin = LineHelpers.Gtin(standardId.Text(), standardId.Attr("schemeID")),
            Description = XmlFields.Text(item.Child("Name") ?? item.Child("Description")),
            Quantity = XmlFields.Number(quantity),
            Unit = LineHelpers.Unit(unitCode),
            UnitRaw = unitCode,
            UnitPrice = LineHelpers.PerUnitPrice(
                XmlFields.Amount(price.Child("PriceAmount"), currency),
                XmlFields.Number(price.Child("BaseQuantity"))),
            DiscountAmount = discountAmount,
            DiscountPercent = discountPercent,
            LineNet = XmlFields.Amount(line.Child("LineExtensionAmount"), currency),
            TaxRate = XmlFields.Number(item.Path("ClassifiedTaxCategory", "Percent")),
            LotNumber = LineHelpers.Lot(attributes, batchId: null),
            BestBefore = LineHelpers.BestBefore(attributes),
        };
    }
}
