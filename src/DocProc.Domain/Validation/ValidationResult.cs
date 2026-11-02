using DocProc.Domain.Flags;

namespace DocProc.Domain.Validation;

/// <summary>Outcome of stage 7: the flags raised and whether the document may skip human review.</summary>
public sealed record ValidationResult(IReadOnlyList<DocumentFlag> Flags, IReadOnlyList<string>? NotRun = null)
{
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
