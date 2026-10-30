using System.Xml.Linq;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice.Codes;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice.Cii;

/// <summary>CII ApplicableHeaderTradeSettlement to the canonical settlement fields.</summary>
internal static class CiiSettlementMapper
{
    private static readonly AllowanceChargeShape Shape = new(
        Indicator: e => e.Path("ChargeIndicator", "Indicator"),
        Reason: "Reason",
        Amount: "ActualAmount",
        Percent: e => e.Path("CategoryTradeTax", "RateApplicablePercent"));

    /// <summary>
    /// TaxTotalAmount may be repeated in the VAT accounting currency; the document-currency one (or the one without a
    /// currency attribute) is the total tax.
    /// </summary>
    public static XElement? SelectTaxTotal(XElement? summation, string? currency)
    {
        var totals = summation.Children("TaxTotalAmount").ToArray();
        return totals.FirstOrDefault(t => t.Attr("currencyID") is null || string.Equals(t.Attr("currencyID"), currency, StringComparison.OrdinalIgnoreCase))
            ?? totals.FirstOrDefault();
    }

    /// <summary>Breakdown rows from ApplicableTradeTax; rows without readable amounts are dropped so validation sees the gap.</summary>
    public static TaxBreakdownLine[] TaxBreakdown(XElement? settlement, string? currency)
    {
        var rows = new List<TaxBreakdownLine>();
        foreach (var tax in settlement.Children("ApplicableTradeTax"))
        {
            if (!XmlFields.TryAmount(tax.Child("BasisAmount"), currency, out var taxable)
                || !XmlFields.TryAmount(tax.Child("CalculatedAmount"), currency, out var amount)
                || !SettlementHelpers.TryRate(tax.Child("RateApplicablePercent"), out var rate))
            {
                continue;
            }

            rows.Add(new TaxBreakdownLine(
                rate,
                taxable,
                amount,
                TaxCategoryCodes.ToCategory(tax.Child("CategoryCode").Text())));
        }

        return rows.ToArray();
    }

    public static ChargeOrAllowance[] AllowanceCharges(XElement? settlement, string? currency, bool charges) =>
        SettlementHelpers.AllowanceCharges(settlement.Children("SpecifiedTradeAllowanceCharge"), currency, charges, Shape);

    /// <summary>Payment means entries are all read: the first type code and the first creditor account found win.</summary>
    public static FieldValue<PaymentTerms> PaymentTerms(XElement? settlement, FieldValue<DateOnly> dueDate)
    {
        var means = CiiPaymentMeans.All(settlement);
        var terms = settlement.Children("SpecifiedTradePaymentTerms").ToArray();
        var note = terms.Select(t => t.Child("Description")).JoinedText();
        if (means.Length == 0 && terms.Length == 0 && !dueDate.HasValue)
        {
            return FieldValue<PaymentTerms>.Missing();
        }

        var discountPercentText = terms
            .Select(t => t.Path("ApplicableTradePaymentDiscountTerms", "CalculationPercent").Text())
            .FirstOrDefault(p => p is not null);
        var account = CiiPaymentMeans.PayeeAccount(means);
        var value = new PaymentTerms(
            DueDate: dueDate.HasValue ? dueDate.Value : null,
            DiscountPercent: XmlFields.TryNumber(discountPercentText, out var discount) ? discount : null,
            PaymentMeans: PaymentMeansCodes.ToPaymentMeans(CiiPaymentMeans.Code(means)),
            PaymentReference: settlement.Child("PaymentReference").Text(),
            Iban: account.Iban,
            Bic: account.Bic,
            RawText: note);
        return XmlFields.Extracted(value, note);
    }
}
