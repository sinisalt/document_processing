using System.Xml.Linq;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice.Codes;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice.Cii;

/// <summary>
/// Maps a UN/CEFACT Cross Industry Invoice (Factur-X / ZUGFeRD profiles MINIMUM to EXTENDED, XRechnung CII) to the
/// canonical <see cref="Invoice"/> over the EN 16931 core elements (docs/03-pipeline.md stage 6a;
/// docs/04-known-format-automation.md, "the cheapest known format"). Every present value is Extracted at confidence
/// 1.0; absent elements stay Missing, so a MINIMUM profile simply yields a sparse invoice.
/// </summary>
public static class CiiInvoiceParser
{
    public static Invoice Parse(XElement root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var document = root.Child("ExchangedDocument");
        var transaction = root.Child("SupplyChainTradeTransaction");
        var agreement = transaction.Child("ApplicableHeaderTradeAgreement");
        var delivery = transaction.Child("ApplicableHeaderTradeDelivery");
        var settlement = transaction.Child("ApplicableHeaderTradeSettlement");
        var summation = settlement.Child("SpecifiedTradeSettlementHeaderMonetarySummation");
        var currencyField = HeaderHelpers.Currency(settlement.Child("InvoiceCurrencyCode"));
        var currency = currencyField.Value;
        var dueDate = XmlFields.CiiDate(settlement.Children("SpecifiedTradePaymentTerms")
            .Select(t => t.Child("DueDateDateTime"))
            .FirstOrDefault(d => d is not null));

        return new Invoice
        {
            Kind = Kind(document.Child("TypeCode")),
            InvoiceNumber = XmlFields.Identifier(document.Child("ID")),
            IssueDate = XmlFields.CiiDate(document.Child("IssueDateTime")),
            DeliveryDate = XmlFields.CiiDate(delivery.Path("ActualDeliverySupplyChainEvent", "OccurrenceDateTime")),
            ServicePeriod = HeaderHelpers.Period(
                XmlFields.CiiDate(settlement.Path("BillingSpecifiedPeriod", "StartDateTime")),
                XmlFields.CiiDate(settlement.Path("BillingSpecifiedPeriod", "EndDateTime"))),
            DueDate = dueDate,
            Currency = currencyField,
            Supplier = CiiPartyMapper.Supplier(agreement, settlement),
            Customer = CiiPartyMapper.Customer(agreement, settlement),
            OrderReferences = HeaderHelpers.References([agreement.Child("BuyerReference"), agreement.Path("BuyerOrderReferencedDocument", "IssuerAssignedID")]),
            DeliveryNoteReferences = HeaderHelpers.References(delivery.Children("DespatchAdviceReferencedDocument").Select(d => d.Child("IssuerAssignedID"))),
            OriginalInvoiceReference = XmlFields.Identifier(settlement.Path("InvoiceReferencedDocument", "IssuerAssignedID")),
            SubtotalNet = XmlFields.Amount(summation.Child("LineTotalAmount"), currency),
            TaxBreakdown = CiiSettlementMapper.TaxBreakdown(settlement, currency),
            TotalTax = XmlFields.Amount(CiiSettlementMapper.SelectTaxTotal(summation, currency), currency),
            TotalGross = XmlFields.Amount(summation.Child("GrandTotalAmount"), currency),
            AmountDue = XmlFields.Amount(summation.Child("DuePayableAmount"), currency),
            Rounding = XmlFields.Amount(summation.Child("RoundingAmount"), currency),
            Charges = CiiSettlementMapper.AllowanceCharges(settlement, currency, charges: true),
            Allowances = CiiSettlementMapper.AllowanceCharges(settlement, currency, charges: false),
            PaymentTerms = CiiSettlementMapper.PaymentTerms(settlement, dueDate),
            Notes = XmlFields.Text(document.Children("IncludedNote").Select(n => n.Child("Content")).JoinedText()),
            Lines = CiiLineMapper.Lines(transaction, currency),
        };
    }

    /// <summary>The CII root is neutral, so the kind comes from ExchangedDocument/TypeCode alone: absent is Missing, unknown is Unreadable.</summary>
    private static FieldValue<InvoiceKind> Kind(XElement? typeCode)
    {
        var code = typeCode.Text();
        if (code is null)
        {
            return FieldValue<InvoiceKind>.Missing();
        }

        return DocumentTypeCodes.ToKind(code) is { } kind ? XmlFields.Extracted(kind, code) : XmlFields.Unreadable<InvoiceKind>(code);
    }
}
