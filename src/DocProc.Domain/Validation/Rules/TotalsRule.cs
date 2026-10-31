using System.Globalization;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;

namespace DocProc.Domain.Validation.Rules;

/// <summary>
/// TOTALS_MISMATCH when Σ line net ≠ subtotal, or subtotal + tax ≠ gross, beyond tolerance.
/// Document-level charges and allowances may be included in the printed subtotal or not; both readings are accepted.
/// </summary>
public sealed class TotalsRule : IInvoiceRule
{
    public IEnumerable<DocumentFlag> Evaluate(Invoice invoice, ValidationContext context)
    {
        var tol = context.Options.MoneyTolerance;
        var adjustments = Sum(invoice.Charges.Select(c => c.Amount.Amount)) - Sum(invoice.Allowances.Select(a => a.Amount.Amount));

        if (invoice.SubtotalNet.HasValue && invoice.Lines.Count > 0)
        {
            var lineNets = invoice.Lines.Where(l => l.LineNet.HasValue).Select(l => l.LineNet.Value!.Amount).ToList();
            if (lineNets.Count == invoice.Lines.Count)
            {
                var deposits = Sum(invoice.Lines.Where(l => l.DepositAmount.HasValue).Select(l => l.DepositAmount.Value!.Amount));
                var subtotal = invoice.SubtotalNet.Value!.Amount;
                var lineTol = tol + (context.Options.PerLineRoundingTolerance * invoice.Lines.Count);
                var sum = Sum(lineNets);
                var candidates = new[] { sum, sum + deposits, sum + adjustments, sum + deposits + adjustments };
                if (!candidates.Any(c => Math.Abs(c - subtotal) <= lineTol))
                {
                    yield return FlagCodes.Create(
                        FlagCodes.TotalsMismatch,
                        $"Lines sum to {Fmt(sum)} but the subtotal is {Fmt(subtotal)}.",
                        "subtotalNet",
                        FlagParams.Of((FlagParams.Reason, "lines"), ("sum", FlagParams.Amount(sum)), ("subtotal", FlagParams.Amount(subtotal))));
                }
            }
        }

        if (invoice.SubtotalNet.HasValue && invoice.TotalTax.HasValue && invoice.TotalGross.HasValue)
        {
            var net = invoice.SubtotalNet.Value!.Amount;
            var tax = invoice.TotalTax.Value!.Amount;
            var gross = invoice.TotalGross.Value!.Amount;
            var rounding = invoice.Rounding.HasValue ? invoice.Rounding.Value!.Amount : 0m;
            var candidates = new[] { net + tax + rounding, net + adjustments + tax + rounding };
            if (!candidates.Any(c => Math.Abs(c - gross) <= tol))
            {
                yield return FlagCodes.Create(
                    FlagCodes.TotalsMismatch,
                    $"Net {Fmt(net)} plus tax {Fmt(tax)} is {Fmt(net + tax)}, but the gross total is {Fmt(gross)}.",
                    "totalGross",
                    FlagParams.Of((FlagParams.Reason, "gross"), ("net", FlagParams.Amount(net)), ("tax", FlagParams.Amount(tax)), ("sum", FlagParams.Amount(net + tax)), ("gross", FlagParams.Amount(gross))));
            }
        }

        if (invoice.AmountDue.HasValue && invoice.TotalGross.HasValue
            && invoice.AmountDue.Value!.Amount > invoice.TotalGross.Value!.Amount + tol)
        {
            yield return FlagCodes.Create(
                FlagCodes.TotalsMismatch,
                $"Amount due {Fmt(invoice.AmountDue.Value!.Amount)} exceeds the gross total {Fmt(invoice.TotalGross.Value!.Amount)}.",
                "amountDue",
                FlagParams.Of((FlagParams.Reason, "amountDue"), ("amountDue", FlagParams.Amount(invoice.AmountDue.Value!.Amount)), ("gross", FlagParams.Amount(invoice.TotalGross.Value!.Amount))));
        }
    }

    private static decimal Sum(IEnumerable<decimal> values) => values.Aggregate(0m, (a, b) => a + b);

    internal static string Fmt(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
