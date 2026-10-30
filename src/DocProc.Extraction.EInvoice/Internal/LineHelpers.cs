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
    /// A unit price "per base quantity" (12.34 per 100 pieces) is expressed per single unit so the canonical line
    /// arithmetic quantity × unit price holds. A base quantity of 1, or none, leaves the price untouched.
    /// </summary>
    public static FieldValue<Money> PerUnitPrice(FieldValue<Money> price, FieldValue<decimal> baseQuantity)
    {
        if (!price.HasValue || !baseQuantity.HasValue || baseQuantity.Value is 0m or 1m)
        {
            return price;
        }

        var perUnit = price.Value! with { Amount = price.Value.Amount / baseQuantity.Value };
        var raw = string.Create(CultureInfo.InvariantCulture, $"{price.Source?.RawText} / {baseQuantity.Source?.RawText}");
        return XmlFields.Extracted(perUnit, raw);
    }

    /// <summary>A unit field from a UN/ECE unit code attribute; Missing when the quantity carries no code.</summary>
    public static FieldValue<UnitOfMeasure> Unit(string? unitCode) =>
        unitCode is null ? FieldValue<UnitOfMeasure>.Missing() : XmlFields.Extracted(UnitCodes.ToUnit(unitCode), unitCode);

    /// <summary>A GTIN only when the identifier is declared as one (ISO 6523 scheme 0160 GTIN or 0088 EAN).</summary>
    public static FieldValue<string> Gtin(string? id, string? schemeId) =>
        id is not null && schemeId is "0160" or "0088" ? XmlFields.Extracted(id, id) : FieldValue<string>.Missing();

    /// <summary>
    /// Line discounts from allowance entries (charge indicator false): the summed amounts when any are printed,
    /// otherwise the single percentage when exactly one is printed. Unparsable amounts make the discount Unreadable.
    /// </summary>
    public static (FieldValue<Money> Amount, FieldValue<decimal> Percent) Discounts(IReadOnlyList<LineAllowance> allowances)
    {
        var amounts = allowances.Where(a => a.AmountText is not null).ToArray();
        if (amounts.Length > 0)
        {
            var currency = amounts[0].Currency is { } first ? CurrencyCode.Normalize(first) : null;
            var sum = 0m;
            foreach (var allowance in amounts)
            {
                var sameCurrency = allowance.Currency is { } c && CurrencyCode.Normalize(c) == currency;
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
}

/// <summary>A free-form item attribute: UBL AdditionalItemProperty or CII ApplicableProductCharacteristic.</summary>
internal sealed record ItemAttribute(string? Name, string? Value);

/// <summary>A line-level allowance as printed: amount text with its resolved currency and/or a percentage text.</summary>
internal sealed record LineAllowance(string? AmountText, string? Currency, string? PercentText);
