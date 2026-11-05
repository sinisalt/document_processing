using DocProc.Domain.Flags;

namespace DocProc.Domain.Validation;

/// <summary>Outcome of stage 7: the flags raised and whether the document may skip human review.</summary>
public sealed record ValidationResult(IReadOnlyList<DocumentFlag> Flags, IReadOnlyList<string>? NotRun = null)
{
    /// <summary>
    /// What each rule of the list that ran raised, by rule id (docs/11 §11 layer 3: <c>review.requirePassedRules</c> and
    /// <c>review.blockOnWarnings</c> read it). A rule that ran and raised nothing has an empty list; a rule that did not
    /// run (<see cref="RulesNotRun"/>) has no entry.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DocumentFlag>> ByRule { get; init; } = new Dictionary<string, IReadOnlyList<DocumentFlag>>(StringComparer.Ordinal);

    /// <summary>Whether the rule ran and raised no warning and no blocking flag (an info note does not fail it).</summary>
    public bool Passed(string ruleId) =>
        ByRule.TryGetValue(ruleId, out var raised) && raised.All(f => f.Severity == FlagSeverity.Info);

    /// <summary>Whether the rule ran and raised a warning.</summary>
    public bool Warned(string ruleId) =>
        ByRule.TryGetValue(ruleId, out var raised) && raised.Any(f => f.Severity == FlagSeverity.Warning);

    /// <summary>
    /// The ids of rule list entries whose kind has no evaluator in this deployment yet (a declared kind, docs/11 §11),
    /// so they did not run. A rule that did not run vouches for nothing: such a document is never auto-approved.
    /// </summary>
    public IReadOnlyList<string> RulesNotRun => NotRun ?? [];

    public bool HasBlocking => Flags.Any(f => f.Severity == FlagSeverity.Blocking);

    public bool HasWarnings => Flags.Any(f => f.Severity == FlagSeverity.Warning);

    /// <summary>
    /// Auto-approval needs no Blocking flag and no field below the confidence threshold (docs/03-pipeline.md stage 7).
    /// Other warnings are shown but do not force review by themselves; tenant review policy may tighten this. Every
    /// rule of the list must have run (<see cref="RulesNotRun"/>).
    /// </summary>
    public bool IsEligibleForAutoApproval =>
        !HasBlocking && Flags.All(f => f.Code != FlagCodes.LowConfidence) && RulesNotRun.Count == 0;

    public IEnumerable<DocumentFlag> OfCode(string code) => Flags.Where(f => f.Code == code);
}
