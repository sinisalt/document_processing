using System.Collections.Frozen;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;

namespace DocProc.Domain.Validation;

/// <summary>The rule kinds the platform knows, by name (docs/11 §11). P0 registers the coded invoice kinds only.</summary>
public sealed class RuleRegistry
{
    private readonly FrozenDictionary<string, IDocumentRule> _rules;

    public RuleRegistry(IEnumerable<IDocumentRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToFrozenDictionary(r => r.Kind, StringComparer.Ordinal);
    }

    /// <summary>The coded invoice kinds (P0) and the generic kinds of P1 (<see cref="Rules.GenericRules"/>).</summary>
    public static RuleRegistry Default { get; } = new([.. InvoiceRuleAdapter.All, .. Rules.GenericRules.All]);

    public IEnumerable<string> Kinds => _rules.Keys;

    public IDocumentRule? Find(string kind) => _rules.GetValueOrDefault(kind);
}

/// <summary>
/// The rule lists of the coded types, until definitions carry their own (P1; the seeded std templates list the same
/// kinds). Like <c>RoleBindings.ForLegacyType</c>, this is the one place that says which rules a coded type runs.
/// </summary>
public static class StandardRuleSets
{
    /// <summary>The coded invoice kinds in <see cref="InvoiceValidator.Default"/>'s order, without parameters.</summary>
    public static IReadOnlyList<RuleReference> Invoice { get; } = [.. InvoiceRuleAdapter.All.Select(a => RuleReference.Of(a.Kind))];

    /// <summary>The rule list of a coded type, or null for a type that is not validated (it always goes to review).</summary>
    public static IReadOnlyList<RuleReference>? ForLegacyType(DocumentTypeKey type) =>
        InvoiceProjection.IsInvoiceCompatible(type) ? Invoice : null;
}

/// <summary>
/// Runs a type's rule list over a <see cref="DocumentView"/> (stage 7, docs/11 §11): every entry in order, each
/// through the kind the registry knows by that name. The flags come out in rule-list order, as
/// <see cref="InvoiceValidator"/> produces them.
/// </summary>
public sealed class DocumentValidator(RuleRegistry registry)
{
    public static DocumentValidator Default { get; } = new(RuleRegistry.Default);

    public ValidationResult Validate(DocumentView view, IReadOnlyList<RuleReference> rules, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(context);

        var flags = new List<DocumentFlag>();
        var notRun = new List<string>();
        foreach (var entry in rules)
        {
            // Publishing a definition refuses a kind this deployment does not declare (docs/11 §5.7). A declared kind
            // whose evaluator is not registered yet does not run, and the result says so, which keeps the document
            // in review (ValidationResult.IsEligibleForAutoApproval).
            if (registry.Find(entry.Kind) is not { } rule)
            {
                notRun.Add(entry.Id);
                continue;
            }

            flags.AddRange(rule.Evaluate(view, entry.Params, context).Select(f => Stamp(f, entry, rule, view.TypeKey)));
        }

        return new ValidationResult(flags, notRun.Count == 0 ? null : notRun);
    }

    /// <summary>
    /// A rule's flag as the definition asks for it: the entry's severity when it sets one, its English message when it
    /// has one, and, for the generic kinds, the rule id and the type key in the parameters (<c>RULE_FAILED {ruleId,
    /// typeKey}</c>, docs/11 §11). A coded invoice kind's flags are left exactly as the coded rule raised them.
    /// </summary>
    private static DocumentFlag Stamp(DocumentFlag flag, RuleReference entry, IDocumentRule rule, string typeKey)
    {
        if (rule is InvoiceRuleAdapter)
        {
            return entry.Severity is { } coded ? flag with { Severity = coded } : flag;
        }

        var parameters = new Dictionary<string, string>(flag.Params ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["ruleId"] = entry.Id,
            ["ruleKind"] = entry.Kind,
            ["typeKey"] = typeKey,
        };
        // A summary of the flag cap (FlagCap) keeps its own sentence after the rule's: the count is its point.
        var message = entry.Message is { } messages && messages.TryGetValue("en", out var english) && !string.IsNullOrWhiteSpace(english)
            ? FlagCap.IsSummary(flag) ? $"{english.TrimEnd()} {flag.Message}" : english
            : flag.Message;
        return flag with { Severity = entry.Severity ?? flag.Severity, Message = message, Params = parameters };
    }
}
