namespace DocProc.Mapping.Identification;

/// <summary>One Active tenant fallback rule version (docs/11 §6.6 <c>TypeIdentificationRuleVersion</c>) as the set evaluates it.</summary>
public sealed record TenantRuleCandidate(
    Guid RuleId,
    Guid VersionId,
    int Version,
    string Name,
    string DocumentTypeKey,
    int Priority,
    IdentificationRule Rule,
    IReadOnlyCollection<Guid> ExcludedSupplierIds);

/// <summary>A rule's evaluation, for the decision, the evidence and the save-time test.</summary>
public sealed record TenantRuleEvaluation(TenantRuleCandidate Candidate, IdentificationResult Result);

/// <summary>How the tenant rules decided (docs/11 §8.5 "Scopes and precedence").</summary>
public enum TenantRuleResolution
{
    /// <summary>No rule holds: classification goes on with the keyword rules and the LLM.</summary>
    None,

    /// <summary>Rules of exactly one type hold; the one with the highest priority is the evidence. The type is decided.</summary>
    Identified,

    /// <summary>Rules of two or more types hold: neither decides, and classification goes on with the keyword rules and the LLM.</summary>
    Conflicting,
}

public sealed record TenantRuleResult(TenantRuleResolution Resolution, TenantRuleEvaluation? Selected, IReadOnlyList<TenantRuleEvaluation> Holding, IReadOnlyList<TenantRuleEvaluation> Evaluated);

/// <summary>
/// The tenant fallback identification rules (docs/11 §8.5, §9.4, <c>TypeIdentificationRuleSet</c>): every Active rule
/// is evaluated (strict, no early "good enough") without supplier master data (a tenant rule has no
/// <c>supplierValue</c>), skipping the rules that exclude the document's supplier. When the rules that hold are all of
/// one type, that type is decided and the holding rule with the highest <c>Priority</c> (then the lowest name) is
/// recorded as the evidence; when rules of two types hold, neither decides (<c>Priority</c> never breaks that tie).
/// </summary>
public static class TypeIdentificationRuleSet
{
    public static TenantRuleResult Resolve(IReadOnlyList<TenantRuleCandidate> rules, DocumentWords words, SourceFacts? source, Guid? supplierId)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(words);
        var context = new IdentificationContext(words, SupplierValues.None, source);
        var evaluated = rules
            .Where(r => supplierId is not { } id || !r.ExcludedSupplierIds.Contains(id))
            .Select(r => new TenantRuleEvaluation(r, IdentificationRuleEvaluator.Evaluate(r.Rule, context)))
            .ToList();
        var holding = evaluated.Where(e => e.Result.Holds).ToList();
        if (holding.Count == 0)
        {
            return new TenantRuleResult(TenantRuleResolution.None, null, holding, evaluated);
        }

        if (holding.Select(h => h.Candidate.DocumentTypeKey).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return new TenantRuleResult(TenantRuleResolution.Conflicting, null, holding, evaluated);
        }

        var selected = holding
            .OrderByDescending(h => h.Candidate.Priority)
            .ThenBy(h => h.Candidate.Name, StringComparer.Ordinal)
            .ThenBy(h => h.Candidate.RuleId)
            .First();
        return new TenantRuleResult(TenantRuleResolution.Identified, selected, holding, evaluated);
    }
}
