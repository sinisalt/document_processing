using System.Text.Json;
using DocProc.Domain.Flags;

namespace DocProc.Domain.Validation;

/// <summary>
/// A rule kind (docs/11 §11, layer 2): what a document type's rule list names by <see cref="Kind"/>, evaluated over
/// the document's <see cref="DocumentView"/> with the parameters the list gives it. Rules are pure: same view, same
/// parameters and context, same flags. The generic kinds arrive with P1; P0 registers the coded invoice rules
/// through <see cref="InvoiceRuleAdapter"/>.
/// </summary>
public interface IDocumentRule
{
    /// <summary>The kind a rule list names, for example <c>invoice.totals</c>.</summary>
    string Kind { get; }

    IEnumerable<DocumentFlag> Evaluate(DocumentView view, RuleParams parameters, ValidationContext context);
}

/// <summary>The parameters a rule list entry passes to its kind (<c>params</c> of the definition), as raw JSON values.</summary>
public sealed record RuleParams(IReadOnlyDictionary<string, JsonElement> Values)
{
    public static readonly RuleParams None = new(new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    public bool TryGet(string name, out JsonElement value) => Values.TryGetValue(name, out value);
}

/// <summary>
/// One entry of a type's rule list: a stable id, the kind it runs and the parameters it passes. <see cref="Severity"/>
/// is the definition's <c>severity</c> (null keeps the kind's own) and <see cref="Message"/> its <c>message</c> by
/// locale; the validator applies both to the flags the rule raises. <see cref="FlagCode"/> is the definition flag the
/// rule raises instead of the kind's own codes (<c>flag</c>, a definition-scoped <c>X_*</c> code the definition declares
/// in its <c>flags</c> block, docs/11 §11); the declared severity and message apply where the rule sets none.
/// </summary>
public sealed record RuleReference(string Id, string Kind, RuleParams Params, FlagSeverity? Severity = null, IReadOnlyDictionary<string, string>? Message = null, string? FlagCode = null)
{
    public static RuleReference Of(string kind) => new(kind, kind, RuleParams.None);
}
