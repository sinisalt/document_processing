using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation.Rules;

namespace DocProc.Domain.Validation;

/// <summary>Runs the rule set over an invoice. Identical for every extraction path.</summary>
public sealed class InvoiceValidator(IReadOnlyList<IInvoiceRule> rules)
{
    public static InvoiceValidator Default { get; } = new(
    [
        new RequiredFieldsRule(),
        new CurrencyRule(),
        new TotalsRule(),
        new TaxBreakdownRule(),
        new LineArithmeticRule(),
        new DatePlausibilityRule(),
        new CustomerMatchRule(),
        new ConfidenceRule(),
    ]);

    public IReadOnlyList<IInvoiceRule> Rules { get; } = rules;

    public ValidationResult Validate(Invoice invoice, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(context);

        var flags = new List<DocumentFlag>();
        foreach (var rule in Rules)
        {
            flags.AddRange(rule.Evaluate(invoice, context));
        }

        return new ValidationResult(flags);
    }
}
