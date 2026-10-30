using System.Xml.Linq;
using DocProc.Domain.Invoices;

namespace DocProc.Extraction.EInvoice.Internal;

/// <summary>Settlement mapping shared by the UBL and CII mappers.</summary>
internal static class SettlementHelpers
{
    /// <summary>
    /// A VAT rate for a breakdown row. EN 16931 omits the rate for exempt, zero-rated and out-of-scope categories,
    /// where 0 is the only possible value; a standard-rate row without a rate is left at 0 so validation reports it.
    /// </summary>
    public static decimal Rate(XElement? percent) => XmlFields.TryNumber(percent.Text(), out var rate) ? rate : 0m;

    /// <summary>
    /// Document-level charges (indicator true) or allowances (indicator false). Entries without a readable amount or
    /// indicator are dropped: a <see cref="ChargeOrAllowance"/> has no Unreadable state, and a dropped entry surfaces
    /// through the totals rules instead of a guessed amount.
    /// </summary>
    public static ChargeOrAllowance[] AllowanceCharges(IEnumerable<XElement> entries, string? currency, bool charges, AllowanceChargeShape shape)
    {
        var result = new List<ChargeOrAllowance>();
        foreach (var entry in entries)
        {
            if (XmlFields.Indicator(shape.Indicator(entry).Text()) != charges)
            {
                continue;
            }

            if (!XmlFields.TryAmount(entry.Child(shape.Amount), currency, out var amount))
            {
                continue;
            }

            decimal? rate = XmlFields.TryNumber(shape.Percent(entry).Text(), out var percent) ? percent : null;
            result.Add(new ChargeOrAllowance(entry.Child(shape.Reason).Text(), amount, rate, charges));
        }

        return result.ToArray();
    }
}

/// <summary>Where one syntax keeps the parts of an allowance/charge entry.</summary>
internal sealed record AllowanceChargeShape(
    Func<XElement, XElement?> Indicator,
    string Reason,
    string Amount,
    Func<XElement, XElement?> Percent);
