using DocProc.Domain.Flags;
using DocProc.Domain.Validation.Rules;

namespace DocProc.Domain.Validation;

/// <summary>
/// Runs one of today's coded invoice rules (<see cref="IInvoiceRule"/>), unchanged, as a rule kind over the view's
/// <see cref="InvoiceProjection"/> (docs/11 §11). A view without an invoice projection (another type, no canonical
/// data) raises nothing: a coded invoice kind does not apply to it. The wrapped rule decides everything else, so the
/// flags are exactly those of <see cref="InvoiceValidator"/>.
/// </summary>
public sealed class InvoiceRuleAdapter : IDocumentRule
{
    public InvoiceRuleAdapter(string kind, IInvoiceRule rule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(rule);
        Kind = kind;
        Rule = rule;
    }

    public string Kind { get; }

    /// <summary>The wrapped coded rule.</summary>
    public IInvoiceRule Rule { get; }

    /// <summary>
    /// The coded invoice kinds, one per rule of <see cref="InvoiceValidator.Default"/> and in its order. docs/11 §11
    /// names five of them (<c>totals</c>, <c>taxBreakdown</c>, <c>lineArithmetic</c>, <c>currency</c>,
    /// <c>customerMatch</c>); the other three (<c>required</c>, <c>datePlausibility</c>, <c>confidence</c>) are coded
    /// kinds only until P1 brings the structural check and the generic kinds that replace them, so P0's flags are
    /// identical to today's.
    /// </summary>
    public static IReadOnlyList<InvoiceRuleAdapter> All { get; } = [.. InvoiceValidator.Default.Rules.Select(r => new InvoiceRuleAdapter(KindOf(r), r))];

    public IEnumerable<DocumentFlag> Evaluate(DocumentView view, RuleParams parameters, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(context);
        var invoice = InvoiceProjection.Of(view);
        return invoice is null ? [] : Rule.Evaluate(invoice, context);
    }

    /// <summary>The kind name of a coded rule; a rule this table does not know cannot be put in a rule list.</summary>
    public static string KindOf(IInvoiceRule rule) => rule switch
    {
        RequiredFieldsRule => InvoiceRuleKinds.Required,
        CurrencyRule => InvoiceRuleKinds.Currency,
        TotalsRule => InvoiceRuleKinds.Totals,
        TaxBreakdownRule => InvoiceRuleKinds.TaxBreakdown,
        LineArithmeticRule => InvoiceRuleKinds.LineArithmetic,
        DatePlausibilityRule => InvoiceRuleKinds.DatePlausibility,
        CustomerMatchRule => InvoiceRuleKinds.CustomerMatch,
        ConfidenceRule => InvoiceRuleKinds.Confidence,
        null => throw new ArgumentNullException(nameof(rule)),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.GetType().Name, "No rule kind is registered for this coded rule."),
    };
}

/// <summary>Kind names of the coded invoice rules (docs/11 §11, "Coded invoice kinds").</summary>
public static class InvoiceRuleKinds
{
    public const string Required = "invoice.required";
    public const string Currency = "invoice.currency";
    public const string Totals = "invoice.totals";
    public const string TaxBreakdown = "invoice.taxBreakdown";
    public const string LineArithmetic = "invoice.lineArithmetic";
    public const string DatePlausibility = "invoice.datePlausibility";
    public const string CustomerMatch = "invoice.customerMatch";
    public const string Confidence = "invoice.confidence";
}
