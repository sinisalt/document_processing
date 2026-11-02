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

    public static RuleRegistry Default { get; } = new(InvoiceRuleAdapter.All);

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
    public static IReadOnlyList<RuleReference>? ForLegacyType(DocumentType type) =>
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
        foreach (var entry in rules)
        {
            // Publishing a definition refuses an unknown kind (docs/11 §5.7), so one here is a platform defect.
            var rule = registry.Find(entry.Kind) ?? throw new InvalidOperationException($"Rule '{entry.Id}' names the kind '{entry.Kind}', which is not registered.");
            flags.AddRange(rule.Evaluate(view, entry.Params, context));
        }

        return new ValidationResult(flags);
    }
}
