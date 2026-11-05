using System.Text.Json;
using DocProc.Domain.Validation;

namespace DocProc.Mapping.Identification;

/// <summary>Where a rule is used: a supplier or group layout (may read the supplier's master data), or a tenant fallback rule (may not, docs/11 §8.5).</summary>
public enum IdentificationScope
{
    Layout,
    Tenant,
}

/// <summary>
/// The checks at save of docs/11 §8.5 that do not need other documents: the meta-schema, the format, patterns that
/// compile and stay within the bounds of <see cref="PatternSize"/>, at least one positive identifier, at most
/// <see cref="IdentificationRule.MaxIdentifiers"/> identifiers, <c>allowOcrErrors</c> never on the only positive
/// identifier, no <c>supplierValue</c> in a tenant rule, and no structured identifier before the structured layers
/// exist (P6). The identification test against the supplier's documents (§8.3) runs where the documents are.
/// </summary>
public static class IdentificationRuleChecks
{
    /// <summary>The rule, or null with every problem found.</summary>
    public static IdentificationRule? Check(string json, IdentificationScope scope, out IReadOnlyList<IdentificationProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(json);
        var found = new List<IdentificationProblem>();
        problems = found;
        try
        {
            using var document = JsonDocument.Parse(json);
            found.AddRange(IdentificationRuleSchema.Validate(document.RootElement));
        }
        catch (JsonException ex)
        {
            found.Add(new IdentificationProblem("(root)", $"is not JSON: {ex.Message}"));
            return null;
        }

        IdentificationRule rule;
        try
        {
            rule = IdentificationRule.Parse(json);
        }
        catch (IdentificationRuleException ex)
        {
            found.Add(new IdentificationProblem(ex.Path.Length == 0 ? "(root)" : ex.Path, ex.Message));
            return null;
        }

        found.AddRange(Semantics(rule, scope));
        return found.Count == 0 ? rule : null;
    }

    /// <summary>The checks of a rule that reads, beyond its meta-schema.</summary>
    public static IReadOnlyList<IdentificationProblem> Semantics(IdentificationRule rule, IdentificationScope scope)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var problems = new List<IdentificationProblem>();
        if (rule.Identifiers.Count > IdentificationRule.MaxIdentifiers)
        {
            problems.Add(new IdentificationProblem("(root)", $"A rule holds at most {IdentificationRule.MaxIdentifiers} identifiers; this one has {rule.Identifiers.Count}."));
        }

        var positives = rule.Identifiers.Where(i => i.Positive).ToList();
        if (positives.Count == 0)
        {
            problems.Add(new IdentificationProblem("(root)", "A rule needs at least one positive identifier (in all or any); none alone identifies nothing."));
        }
        else if (positives.All(p => p.Identifier.AllowOcrErrors))
        {
            foreach (var (path, _, _) in positives)
            {
                problems.Add(new IdentificationProblem(path, "allowOcrErrors is not allowed on the only positive identifier: the rule also needs an exact one."));
            }
        }

        foreach (var (path, identifier, _) in rule.Identifiers)
        {
            if (identifier.AllowOcrErrors && identifier.Kind != IdentifierKind.Text)
            {
                problems.Add(new IdentificationProblem(path, "allowOcrErrors applies to text identifiers only."));
            }

            switch (identifier.Kind)
            {
                case IdentifierKind.Regex:
                    CheckPattern(identifier.Value, path, problems);
                    break;
                case IdentifierKind.Source:
                    if (!SourceFacts.Keys.Contains(identifier.Value, StringComparer.Ordinal))
                    {
                        problems.Add(new IdentificationProblem(path, $"'{identifier.Value}' is not a source fact; use one of {string.Join(", ", SourceFacts.Keys)}."));
                    }

                    if (identifier.Pattern is { } pattern)
                    {
                        CheckPattern(pattern, path, problems);
                    }

                    break;
                case IdentifierKind.SupplierValue:
                    if (scope == IdentificationScope.Tenant)
                    {
                        problems.Add(new IdentificationProblem(path, "A tenant fallback rule runs when the supplier may be unknown, so it cannot name a supplier value; use a text or regex identifier."));
                    }
                    else if (!SupplierValueKeys.IsValid(identifier.Value))
                    {
                        problems.Add(new IdentificationProblem(path, $"'{identifier.Value}' is not a supplier value; use vatId, taxId, iban or attributes.<key>."));
                    }

                    break;
                case IdentifierKind.XmlPath or IdentifierKind.Cell:
                    problems.Add(new IdentificationProblem(path, $"{IdentificationRuleEvaluator.KindName(identifier.Kind)} identifiers read structured sources, which arrive with the tree and tabular layers (P6)."));
                    break;
            }
        }

        return problems;
    }

    private static void CheckPattern(string pattern, string path, List<IdentificationProblem> problems)
    {
        if (PatternSize.Problem(pattern) is { } problem)
        {
            problems.Add(new IdentificationProblem(path, $"The pattern repeats too much: {problem}"));
            return;
        }

        if (IdentificationRuleEvaluator.Compile(pattern) is null)
        {
            problems.Add(new IdentificationProblem(path, "The pattern does not compile (NonBacktracking: no backreferences or lookarounds)."));
        }
    }
}
