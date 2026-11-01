using System.Xml.Linq;

namespace DocProc.Extraction.EInvoice.Internal;

/// <summary>
/// Payment means may be repeated (Peppol BIS 3, XRechnung): a code 30 entry without an account followed by a code
/// 58 entry with the IBAN, or two bank accounts. Every entry is read and the first value of each kind wins.
/// </summary>
internal static class PaymentMeansHelpers
{
    /// <summary>
    /// The first IBAN across the entries, with the BIC printed beside it when there is one, else the first BIC
    /// across the entries. Both null when no entry carries them.
    /// </summary>
    public static BankAccount FirstAccount(IReadOnlyList<XElement> means, Func<XElement, string?> iban, Func<XElement, string?> bic)
    {
        var withIban = means.FirstOrDefault(m => iban(m) is not null);
        var bicValue = (withIban is null ? null : bic(withIban)) ?? First(means, bic);
        return new BankAccount(withIban is null ? null : iban(withIban), bicValue);
    }

    /// <summary>The first entry's value of the given kind, skipping entries that do not print it.</summary>
    public static string? First(IReadOnlyList<XElement> means, Func<XElement, string?> select) =>
        means.Select(select).FirstOrDefault(v => v is not null);
}

/// <summary>A bank account as printed in a payment means entry: either part may be absent.</summary>
internal readonly record struct BankAccount(string? Iban, string? Bic);
