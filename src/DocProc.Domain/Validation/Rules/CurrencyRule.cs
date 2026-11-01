using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;

namespace DocProc.Domain.Validation.Rules;

/// <summary>CURRENCY_INFERRED when the currency was assumed; INVALID_VALUE for malformed codes or mixed currencies.</summary>
public sealed class CurrencyRule : IInvoiceRule
{
    public IEnumerable<DocumentFlag> Evaluate(Invoice invoice, ValidationContext context)
    {
        if (!invoice.Currency.HasValue)
        {
            yield break;
        }

        var currency = invoice.Currency.Value!;
        if (invoice.Currency.Status == FieldStatus.Inferred)
        {
            yield return FlagCodes.Create(FlagCodes.CurrencyInferred, $"No currency was printed; '{currency}' was assumed.", "currency", FlagParams.Of(("currency", currency)));
        }

        if (!CurrencyCode.IsWellFormed(currency))
        {
            yield return FlagCodes.Create(FlagCodes.InvalidValue, $"'{currency}' is not a valid ISO 4217 currency code.", "currency", FlagParams.Of((FlagParams.Reason, "currencyCode"), ("currency", currency)));
            yield break;
        }

        foreach (var (path, money) in Amounts(invoice))
        {
            if (!string.Equals(money.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                yield return FlagCodes.Create(FlagCodes.InvalidValue, $"'{path}' is in {money.Currency} but the document currency is {currency}.", path, FlagParams.Of((FlagParams.Reason, "currencyMismatch"), ("field", path), ("currency", money.Currency), ("documentCurrency", currency)));
            }
        }
    }

    private static IEnumerable<(string Path, Money Money)> Amounts(Invoice invoice)
    {
        foreach (var (path, field) in invoice.EnumerateAllFields())
        {
            if (field is { HasValue: true, BoxedValue: Money m })
            {
                yield return (path, m);
            }
        }

        for (var i = 0; i < invoice.TaxBreakdown.Count; i++)
        {
            yield return ($"taxBreakdown[{i}].taxableAmount", invoice.TaxBreakdown[i].TaxableAmount);
            yield return ($"taxBreakdown[{i}].taxAmount", invoice.TaxBreakdown[i].TaxAmount);
        }
    }
}
