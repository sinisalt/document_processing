using System.Globalization;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Extraction.EInvoice.Codes;

namespace DocProc.Extraction.EInvoice.Internal;

/// <summary>Line-level mapping shared by the UBL and CII mappers; the syntaxes differ only in element names.</summary>
internal static class LineHelpers
{
    /// <summary>The printed line id when it is a positive integer, else the 1-based position in the document.</summary>
    public static int LineNumber(string? lineId, int position) =>
        int.TryParse(lineId, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : position;

    /// <summary>
    /// The canonical unit price and line discount (docs/02-data-model.md: unit price before the line discount, so
    /// quantity × unit price − discount = line net). The net price (BT-146) is the unit price unless the document
    /// carries a price-level discount (BT-147, UBL Price/AllowanceCharge, CII AppliedTradeAllowanceCharge): then the
    /// gross price (BT-148, printed or net + discount) is the unit price and the discount, per unit × quantity,
    /// joins the line-level allowances (BG-27) in the discount amount. Price-level entries that print only a
    /// percentage are not used: without a gross price the arithmetic cannot be reconstructed exactly.
    /// </summary>
    public static LinePrice Price(LinePricing pricing)
    {
        var (lineDiscount, lineDiscountPercent) = Discounts(pricing.LineAllowances);
        var priceDiscount = Discounts(pricing.PriceAllowances).Amount;
        if (priceDiscount.Status == FieldStatus.Missing)
        {
            return new LinePrice(PerUnit(pricing.NetPrice, pricing), lineDiscount, lineDiscountPercent);
        }

        if (!priceDiscount.HasValue)
        {
            return new LinePrice(PerUnit(pricing.NetPrice, pricing), XmlFields.Unreadable<Money>(priceDiscount.Source?.RawText ?? "?"), lineDiscountPercent);
        }

        var gross = pricing.GrossPrice.HasValue ? pricing.GrossPrice : Sum(pricing.NetPrice, priceDiscount);
        var total = Multiply(PerUnit(priceDiscount, pricing), pricing.Quantity);
        var discountAmount = lineDiscount.Status == FieldStatus.Missing ? total : Sum(lineDiscount, total);
        return new LinePrice(PerUnit(gross, pricing), discountAmount, lineDiscountPercent);
    }

    /// <summary>
    /// A price "per base quantity" (12.34 per 100 pieces) expressed per single unit so the canonical line arithmetic
    /// quantity × unit price holds. A base quantity of 1, or none, leaves the price untouched. A base quantity in a
    /// different unit than the invoiced quantity (10.00 per 100 GRM, invoiced in KGM) cannot be reconciled without
    /// a conversion, so the price is Unreadable with the printed figures in the note; never a wrong per-unit value.
    /// An amount too large to divide is Unreadable as well: nothing in a well-formed document throws.
    /// </summary>
    public static FieldValue<Money> PerUnitPrice(FieldValue<Money> price, FieldValue<decimal> baseQuantity, string? baseUnitCode, string? unitCode)
    {
        if (!price.HasValue || !baseQuantity.HasValue)
        {
            return price;
        }

        var raw = string.Create(CultureInfo.InvariantCulture, $"{price.Source?.RawText} / {baseQuantity.Source?.RawText}");
        if (baseUnitCode is not null && !string.Equals(baseUnitCode, unitCode, StringComparison.OrdinalIgnoreCase))
        {
            return XmlFields.Unreadable<Money>(
                $"{price.Source?.RawText} per {baseQuantity.Source?.RawText} {baseUnitCode} (quantity in {unitCode ?? "no unit"})");
        }

        if (baseQuantity.Value is 0m or 1m)
        {
            return price;
        }

        try
        {
            return XmlFields.Extracted(price.Value! with { Amount = price.Value.Amount / baseQuantity.Value }, raw);
        }
        catch (OverflowException)
        {
            return XmlFields.Unreadable<Money>(raw);
        }
    }

    /// <summary>A unit field from a UN/ECE unit code attribute; Missing when the quantity carries no code.</summary>
    public static FieldValue<UnitOfMeasure> Unit(string? unitCode) =>
        unitCode is null ? FieldValue<UnitOfMeasure>.Missing() : XmlFields.Extracted(UnitCodes.ToUnit(unitCode), unitCode);

    /// <summary>A GTIN only when the identifier is declared as one (ISO 6523 scheme 0160 GTIN or 0088 EAN).</summary>
    public static FieldValue<string> Gtin(string? id, string? schemeId) =>
        id is not null && schemeId is "0160" or "0088" ? XmlFields.Extracted(id, id) : FieldValue<string>.Missing();

    /// <summary>
    /// Discounts from allowance entries (charge indicator false): the summed amounts when any are printed,
    /// otherwise the single percentage when exactly one is printed. Unparsable amounts, or amounts without a
    /// well-formed currency, make the discount Unreadable.
    /// </summary>
    public static (FieldValue<Money> Amount, FieldValue<decimal> Percent) Discounts(IReadOnlyList<LineAllowance> allowances)
    {
        var amounts = allowances.Where(a => a.AmountText is not null).ToArray();
        if (amounts.Length > 0)
        {
            var currency = XmlFields.WellFormedCurrency(amounts[0].Currency);
            var sum = 0m;
            foreach (var allowance in amounts)
            {
                var sameCurrency = currency is not null && XmlFields.WellFormedCurrency(allowance.Currency) == currency;
                if (!sameCurrency || !XmlFields.TryNumber(allowance.AmountText, out var value))
                {
                    return (XmlFields.Unreadable<Money>(allowance.AmountText!), FieldValue<decimal>.Missing());
                }

                sum += Math.Abs(value);
            }

            return (XmlFields.Extracted(new Money(sum, currency!), string.Join(" + ", amounts.Select(a => a.AmountText))), FieldValue<decimal>.Missing());
        }

        var percents = allowances.Where(a => a.PercentText is not null).ToArray();
        if (percents.Length != 1)
        {
            return (FieldValue<Money>.Missing(), FieldValue<decimal>.Missing());
        }

        var text = percents[0].PercentText!;
        return XmlFields.TryNumber(text, out var percent)
            ? (FieldValue<Money>.Missing(), XmlFields.Extracted(percent, text))
            : (FieldValue<Money>.Missing(), XmlFields.Unreadable<decimal>(text));
    }

    /// <summary>Lot number from free-form item attributes, by well-known attribute names.</summary>
    public static FieldValue<string> Lot(IEnumerable<ItemAttribute> attributes, string? batchId)
    {
        if (batchId is not null)
        {
            return XmlFields.Extracted(batchId, batchId);
        }

        var lot = attributes.FirstOrDefault(a => ItemPropertyNames.IsLot(a.Name) && a.Value is not null);
        return lot is null ? FieldValue<string>.Missing() : XmlFields.Extracted(lot.Value!, lot.Value);
    }

    /// <summary>Best-before date from free-form item attributes; an unparsable date is Unreadable.</summary>
    public static FieldValue<DateOnly> BestBefore(IEnumerable<ItemAttribute> attributes)
    {
        var attribute = attributes.FirstOrDefault(a => ItemPropertyNames.IsBestBefore(a.Name) && a.Value is not null);
        if (attribute is null)
        {
            return FieldValue<DateOnly>.Missing();
        }

        return XmlFields.TryAnyDate(attribute.Value, out var date)
            ? XmlFields.Extracted(date, attribute.Value)
            : XmlFields.Unreadable<DateOnly>(attribute.Value!);
    }

    private static FieldValue<Money> PerUnit(FieldValue<Money> amount, LinePricing pricing) =>
        PerUnitPrice(amount, pricing.BaseQuantity, pricing.BaseUnitCode, pricing.UnitCode);

    /// <summary>a + b when both are readable in one currency; Missing + anything is that thing; otherwise Unreadable.</summary>
    private static FieldValue<Money> Sum(FieldValue<Money> a, FieldValue<Money> b)
    {
        if (a.Status == FieldStatus.Missing)
        {
            return b;
        }

        var raw = $"{a.Source?.RawText ?? "?"} + {b.Source?.RawText ?? "?"}";
        if (!a.HasValue || !b.HasValue || a.Value!.Currency != b.Value!.Currency)
        {
            return XmlFields.Unreadable<Money>(raw);
        }

        try
        {
            return XmlFields.Extracted(a.Value + b.Value, raw);
        }
        catch (OverflowException)
        {
            return XmlFields.Unreadable<Money>(raw);
        }
    }

    /// <summary>A per-unit amount × the invoiced quantity; Unreadable when either side is not readable.</summary>
    private static FieldValue<Money> Multiply(FieldValue<Money> perUnit, FieldValue<decimal> quantity)
    {
        var raw = $"{perUnit.Source?.RawText ?? "?"} × {quantity.Source?.RawText ?? "?"}";
        if (!perUnit.HasValue || !quantity.HasValue)
        {
            return XmlFields.Unreadable<Money>(raw);
        }

        try
        {
            return XmlFields.Extracted(perUnit.Value! * quantity.Value, raw);
        }
        catch (OverflowException)
        {
            return XmlFields.Unreadable<Money>(raw);
        }
    }
}

/// <summary>A free-form item attribute: UBL AdditionalItemProperty or CII ApplicableProductCharacteristic.</summary>
internal sealed record ItemAttribute(string? Name, string? Value);

/// <summary>An allowance as printed: amount text with its resolved currency and/or a percentage text.</summary>
internal sealed record LineAllowance(string? AmountText, string? Currency, string? PercentText);

/// <summary>Everything one syntax prints about a line's price: net and gross price per base quantity, price-level allowances, the quantities and line-level allowances.</summary>
internal sealed record LinePricing(
    FieldValue<Money> NetPrice,
    FieldValue<Money> GrossPrice,
    IReadOnlyList<LineAllowance> PriceAllowances,
    FieldValue<decimal> BaseQuantity,
    string? BaseUnitCode,
    FieldValue<decimal> Quantity,
    string? UnitCode,
    IReadOnlyList<LineAllowance> LineAllowances);

/// <summary>The canonical unit price and discount fields of a line.</summary>
internal sealed record LinePrice(FieldValue<Money> UnitPrice, FieldValue<Money> DiscountAmount, FieldValue<decimal> DiscountPercent);
