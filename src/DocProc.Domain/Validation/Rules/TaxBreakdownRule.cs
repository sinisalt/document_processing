using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;

namespace DocProc.Domain.Validation.Rules;

/// <summary>TAX_MISMATCH when the breakdown rows do not reproduce the total tax, or a row's base × rate ≠ its tax.</summary>
public sealed class TaxBreakdownRule : IInvoiceRule
{
    public IEnumerable<DocumentFlag> Evaluate(Invoice invoice, ValidationContext context)
    {
        if (invoice.TaxBreakdown.Count == 0)
        {
            yield break;
        }

        var tol = context.Options.MoneyTolerance;

        for (var i = 0; i < invoice.TaxBreakdown.Count; i++)
        {
            var row = invoice.TaxBreakdown[i];
            var expected = row.ExpectedTax.Amount;
            if (Math.Abs(expected - row.TaxAmount.Amount) > tol)
            {
                yield return FlagCodes.Create(
                    FlagCodes.TaxMismatch,
                    $"{TotalsRule.Fmt(row.TaxableAmount.Amount)} at {TotalsRule.Fmt(row.Rate)} % should give {TotalsRule.Fmt(expected)} tax, but {TotalsRule.Fmt(row.TaxAmount.Amount)} is printed.",
                    $"taxBreakdown[{i}].taxAmount");
            }
        }

        if (invoice.TotalTax.HasValue)
        {
            var sum = invoice.TaxBreakdown.Sum(r => r.TaxAmount.Amount);
            var total = invoice.TotalTax.Value!.Amount;
            if (Math.Abs(sum - total) > tol)
            {
                yield return FlagCodes.Create(
                    FlagCodes.TaxMismatch,
                    $"Tax rows sum to {TotalsRule.Fmt(sum)} but the total tax is {TotalsRule.Fmt(total)}.",
                    "totalTax");
            }
        }

        if (invoice.SubtotalNet.HasValue)
        {
            var taxable = invoice.TaxBreakdown.Sum(r => r.TaxableAmount.Amount);
            var net = invoice.SubtotalNet.Value!.Amount;
            var adjustments = invoice.Charges.Sum(c => c.Amount.Amount) - invoice.Allowances.Sum(a => a.Amount.Amount);
            if (Math.Abs(taxable - net) > tol && Math.Abs(taxable - (net + adjustments)) > tol)
            {
                yield return FlagCodes.Create(
                    FlagCodes.TaxMismatch,
                    $"Taxable bases sum to {TotalsRule.Fmt(taxable)} but the net subtotal is {TotalsRule.Fmt(net)}.",
                    "taxBreakdown");
            }
        }
    }
}
