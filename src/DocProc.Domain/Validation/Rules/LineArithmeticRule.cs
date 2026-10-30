using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;

namespace DocProc.Domain.Validation.Rules;

/// <summary>LINE_ARITHMETIC per line where quantity × unit price (less discount) ≠ line net.</summary>
public sealed class LineArithmeticRule : IInvoiceRule
{
    public IEnumerable<DocumentFlag> Evaluate(Invoice invoice, ValidationContext context)
    {
        var tol = context.Options.MoneyTolerance;
        for (var i = 0; i < invoice.Lines.Count; i++)
        {
            var line = invoice.Lines[i];
            if (!line.Quantity.HasValue || !line.UnitPrice.HasValue || !line.LineNet.HasValue)
            {
                continue;
            }

            var expected = line.Quantity.Value * line.UnitPrice.Value!.Amount;
            if (line.DiscountAmount.HasValue)
            {
                expected -= Math.Abs(line.DiscountAmount.Value!.Amount);
            }
            else if (line.DiscountPercent.HasValue)
            {
                expected *= 1 - (line.DiscountPercent.Value / 100m);
            }

            var actual = line.LineNet.Value!.Amount;

            // Credit notes and return lines may print magnitudes with the sign carried elsewhere.
            if (Math.Abs(expected - actual) > tol && Math.Abs(Math.Abs(expected) - Math.Abs(actual)) > tol)
            {
                yield return FlagCodes.Create(
                    FlagCodes.LineArithmetic,
                    $"Line {line.LineNumber}: {TotalsRule.Fmt(line.Quantity.Value)} × {TotalsRule.Fmt(line.UnitPrice.Value!.Amount)} = {TotalsRule.Fmt(expected)}, but the line net is {TotalsRule.Fmt(actual)}.",
                    $"lines[{i}].lineNet");
            }
        }
    }
}
