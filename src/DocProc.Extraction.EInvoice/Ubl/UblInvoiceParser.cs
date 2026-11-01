using System.Xml.Linq;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice.Codes;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice.Ubl;

/// <summary>
/// Maps a UBL 2.1 Invoice or CreditNote (Peppol BIS Billing 3.0, XRechnung UBL) to the canonical
/// <see cref="Invoice"/> over the EN 16931 core elements (docs/03-pipeline.md stage 6a; docs/04-known-format-automation.md,
/// "the cheapest known format"). Every present value is Extracted at confidence 1.0; absent elements stay Missing.
/// </summary>
public static class UblInvoiceParser
{
    public static Invoice Parse(XElement root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var isCreditNote = root.Name.LocalName == EInvoiceNamespaces.UblCreditNoteRoot;
        var currencyElement = root.Child("DocumentCurrencyCode");
        var currencyField = HeaderHelpers.Currency(currencyElement);
        var currency = currencyElement.Text();
        var totals = root.Child("LegalMonetaryTotal");
        var taxTotal = UblSettlementMapper.SelectTaxTotal(root);
        var dueDate = XmlFields.IsoDate(root.Child("DueDate") ?? UblPaymentMeans.PaymentDueDate(UblPaymentMeans.All(root)));

        return new Invoice
        {
            Kind = Kind(root, isCreditNote),
            InvoiceNumber = XmlFields.Identifier(root.Child("ID")),
            IssueDate = XmlFields.IsoDate(root.Child("IssueDate")),
            DeliveryDate = XmlFields.IsoDate(root.Path("Delivery", "ActualDeliveryDate")),
            ServicePeriod = HeaderHelpers.Period(
                XmlFields.IsoDate(root.Path("InvoicePeriod", "StartDate")),
                XmlFields.IsoDate(root.Path("InvoicePeriod", "EndDate"))),
            DueDate = dueDate,
            Currency = currencyField,
            Supplier = UblPartyMapper.Supplier(root),
            Customer = UblPartyMapper.Customer(root),
            // BuyerReference (BT-10) is a routing id (the XRechnung Leitweg-ID), not a purchase order number
            // (docs/02-data-model.md: orderReferences match PurchaseOrders), so it stays out of OrderReferences.
            OrderReferences = HeaderHelpers.References([root.Path("OrderReference", "ID")]),
            DeliveryNoteReferences = HeaderHelpers.References(root.Children("DespatchDocumentReference").Select(r => r.Child("ID"))),
            OriginalInvoiceReference = XmlFields.Identifier(root.Path("BillingReference", "InvoiceDocumentReference", "ID")),
            SubtotalNet = XmlFields.Amount(totals.Child("LineExtensionAmount"), currency),
            TaxBreakdown = UblSettlementMapper.TaxBreakdown(taxTotal, currency),
            TotalTax = XmlFields.Amount(taxTotal.Child("TaxAmount"), currency),
            TotalGross = XmlFields.Amount(totals.Child("TaxInclusiveAmount"), currency),
            AmountDue = XmlFields.Amount(totals.Child("PayableAmount"), currency),
            Rounding = XmlFields.Amount(totals.Child("PayableRoundingAmount"), currency),
            Charges = UblSettlementMapper.AllowanceCharges(root, currency, charges: true),
            Allowances = UblSettlementMapper.AllowanceCharges(root, currency, charges: false),
            PaymentTerms = UblSettlementMapper.PaymentTerms(root, dueDate),
            Notes = XmlFields.Text(root.Children("Note").JoinedText()),
            Lines = UblLineMapper.Lines(root, isCreditNote, currency),
        };
    }

    /// <summary>
    /// The CreditNote root is a credit note whatever its type code says; an Invoice root follows its InvoiceTypeCode
    /// (381 is a credit note issued in invoice syntax), is an invoice when no code is printed, and is Unreadable for a
    /// code this mapper does not know.
    /// </summary>
    private static FieldValue<InvoiceKind> Kind(XElement root, bool isCreditNote)
    {
        var code = root.Child(isCreditNote ? "CreditNoteTypeCode" : "InvoiceTypeCode").Text();
        if (isCreditNote)
        {
            return XmlFields.Extracted(InvoiceKind.CreditNote, code ?? root.Name.LocalName);
        }

        if (code is null)
        {
            return XmlFields.Extracted(InvoiceKind.Invoice, root.Name.LocalName);
        }

        return DocumentTypeCodes.ToKind(code) is { } kind ? XmlFields.Extracted(kind, code) : XmlFields.Unreadable<InvoiceKind>(code);
    }
}
