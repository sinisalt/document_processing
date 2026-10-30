using System.Xml.Linq;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice.Ubl;

/// <summary>Every cac:PaymentMeans of a UBL document, read together (see <see cref="PaymentMeansHelpers"/>).</summary>
internal static class UblPaymentMeans
{
    public static XElement[] All(XElement root) => root.Children("PaymentMeans").ToArray();

    /// <summary>The supplier's account: the first PayeeFinancialAccount with an ID.</summary>
    public static BankAccount PayeeAccount(IReadOnlyList<XElement> means) =>
        PaymentMeansHelpers.FirstAccount(
            means,
            m => m.Path("PayeeFinancialAccount", "ID").Text(),
            m => m.Path("PayeeFinancialAccount", "FinancialInstitutionBranch", "ID").Text());

    /// <summary>The customer's direct-debit account, directly under the means or inside its PaymentMandate.</summary>
    public static BankAccount PayerAccount(IReadOnlyList<XElement> means) =>
        PaymentMeansHelpers.FirstAccount(
            means,
            m => Payer(m).Child("ID").Text(),
            m => Payer(m).Path("FinancialInstitutionBranch", "ID").Text());

    public static string? Code(IReadOnlyList<XElement> means) => PaymentMeansHelpers.First(means, m => m.Child("PaymentMeansCode").Text());

    public static string? PaymentId(IReadOnlyList<XElement> means) => PaymentMeansHelpers.First(means, m => m.Child("PaymentID").Text());

    public static XElement? PaymentDueDate(IReadOnlyList<XElement> means) => means.Select(m => m.Child("PaymentDueDate")).FirstOrDefault(d => d is not null);

    private static XElement? Payer(XElement means) => means.Child("PayerFinancialAccount") ?? means.Path("PaymentMandate", "PayerFinancialAccount");
}
