using System.Globalization;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;

namespace DocProc.Domain.Validation.Rules;

/// <summary>LOW_CONFIDENCE for every present field whose confidence is below the tenant threshold.</summary>
public sealed class ConfidenceRule : IInvoiceRule
{
    public IEnumerable<DocumentFlag> Evaluate(Invoice invoice, ValidationContext context)
    {
        var threshold = context.Options.ConfidenceThreshold;
        foreach (var (path, field) in invoice.EnumerateAllFields())
        {
            if (field.HasValue && field.Confidence is { } c && c < threshold)
            {
                yield return FlagCodes.Create(
                    FlagCodes.LowConfidence,
                    $"'{path}' has confidence {c.ToString("0.00", CultureInfo.InvariantCulture)}, below the threshold {threshold.ToString("0.00", CultureInfo.InvariantCulture)}.",
                    path,
                    FlagParams.Of(("field", path), ("confidence", FlagParams.Ratio(c)), ("threshold", FlagParams.Ratio(threshold))));
            }
        }
    }
}
