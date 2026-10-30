using System.Xml.Linq;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice.Codes;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice.Ubl;

/// <summary>UBL TaxTotal, AllowanceCharge, PaymentMeans and PaymentTerms to the canonical settlement fields.</summary>
internal static class UblSettlementMapper
{
    private static readonly AllowanceChargeShape Shape = new(
        Indicator: e => e.Child("ChargeIndicator"),
        Reason: "AllowanceChargeReason",
        Amount: "Amount",
        Percent: e => e.Path("TaxCategory", "Percent"));

    /// <summary>
    /// A document may carry two TaxTotal elements when the VAT accounting currency differs; the one with the
    /// breakdown rows is the document-currency one.
    /// </summary>
    public static XElement? SelectTaxTotal(XElement root)
    {
        var totals = root.Children("TaxTotal").ToArray();
        return totals.FirstOrDefault(t => t.Child("TaxSubtotal") is not null) ?? totals.FirstOrDefault();
    }

    /// <summary>Breakdown rows from TaxSubtotal; rows without readable amounts are dropped so validation sees the gap.</summary>
    public static TaxBreakdownLine[] TaxBreakdown(XElement? taxTotal, string? currency)
    {
        var rows = new List<TaxBreakdownLine>();
        foreach (var subtotal in taxTotal.Children("TaxSubtotal"))
        {
            var category = subtotal.Child("TaxCategory");
            if (!XmlFields.TryAmount(subtotal.Child("TaxableAmount"), currency, out var taxable)
                || !XmlFields.TryAmount(subtotal.Child("TaxAmount"), currency, out var tax)
                || !SettlementHelpers.TryRate(category.Child("Percent"), out var rate))
            {
                continue;
            }

            rows.Add(new TaxBreakdownLine(
                rate,
                taxable,
                tax,
                TaxCategoryCodes.ToCategory(category.Child("ID").Text())));
        }

        return rows.ToArray();
    }

    /// <summary>Document-level AllowanceCharge entries (direct children of the root), not the line or price ones.</summary>
    public static ChargeOrAllowance[] AllowanceCharges(XElement root, string? currency, bool charges) =>
        SettlementHelpers.AllowanceCharges(root.Children("AllowanceCharge"), currency, charges, Shape);

    /// <summary>PaymentMeans entries are all read: the first code, the first PaymentID and the first payee account found win.</summary>
    public static FieldValue<PaymentTerms> PaymentTerms(XElement root, FieldValue<DateOnly> dueDate)
    {
        var means = UblPaymentMeans.All(root);
        var note = root.Children("PaymentTerms").SelectMany(t => t.Children("Note")).JoinedText();
        if (means.Length == 0 && note is null && !dueDate.HasValue)
        {
            return FieldValue<PaymentTerms>.Missing();
        }

        var account = UblPaymentMeans.PayeeAccount(means);
        var terms = new PaymentTerms(
            DueDate: dueDate.HasValue ? dueDate.Value : null,
            PaymentMeans: PaymentMeansCodes.ToPaymentMeans(UblPaymentMeans.Code(means)),
            PaymentReference: UblPaymentMeans.PaymentId(means),
            Iban: account.Iban,
            Bic: account.Bic,
            RawText: note);
        return XmlFields.Extracted(terms, note);
    }
}
