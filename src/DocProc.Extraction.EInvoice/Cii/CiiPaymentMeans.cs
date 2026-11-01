using System.Xml.Linq;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice.Cii;

/// <summary>Every ram:SpecifiedTradeSettlementPaymentMeans of a CII document, read together (see <see cref="PaymentMeansHelpers"/>).</summary>
internal static class CiiPaymentMeans
{
    public static XElement[] All(XElement? settlement) => settlement.Children("SpecifiedTradeSettlementPaymentMeans").ToArray();

    /// <summary>The supplier's account: the first entry with a creditor IBAN, and the BIC printed beside it.</summary>
    public static BankAccount PayeeAccount(IReadOnlyList<XElement> means) =>
        PaymentMeansHelpers.FirstAccount(
            means,
            m => m.Path("PayeePartyCreditorFinancialAccount", "IBANID").Text(),
            m => m.Path("PayeeSpecifiedCreditorFinancialInstitution", "BICID").Text());

    /// <summary>The customer's direct-debit account; CII prints no BIC for the debtor.</summary>
    public static BankAccount PayerAccount(IReadOnlyList<XElement> means) =>
        PaymentMeansHelpers.FirstAccount(means, m => m.Path("PayerPartyDebtorFinancialAccount", "IBANID").Text(), _ => null);

    public static string? Code(IReadOnlyList<XElement> means) => PaymentMeansHelpers.First(means, m => m.Child("TypeCode").Text());
}
