namespace DocProc.Mapping.Identification;

/// <summary>Whose a layout is (docs/11 §6.6 <c>Scope</c>).</summary>
public enum LayoutScope
{
    Supplier,
    Group,
}

/// <summary>
/// The state of a layout version as the resolver sees it (docs/11 §6.6): <see cref="Active"/> and <see cref="Degraded"/>
/// identify and apply (a Degraded version's field mapping is not used, the LLM fills everything, §8.2), a
/// <see cref="Shadow"/> version is evaluated for its stats only, a <see cref="Candidate"/> only feeds
/// <c>pendingLayoutVersionId</c> (§8.4 step 5).
/// </summary>
public enum LayoutCandidateState
{
    Active,
    Degraded,
    Shadow,
    Candidate,
}

/// <summary>One layout version the resolver evaluates: its layout, its type, where it applies, and its identification rule.</summary>
public sealed record LayoutCandidate(
    Guid LayoutId,
    string LayoutKey,
    Guid VersionId,
    int Version,
    string DocumentTypeKey,
    LayoutScope Scope,
    LayoutCandidateState State,
    IdentificationRule Rule,
    int Priority = 100,
    IReadOnlyList<string>? ItemKinds = null,
    Guid? ReceivingEntityId = null);

/// <summary>What the resolver knows of the document and its supplier.</summary>
/// <param name="Context">The document's words, its supplier's master data and its source facts.</param>
/// <param name="Candidates">The layout versions of the supplier and of its group.</param>
/// <param name="ItemKind">The item kind (<c>pdf</c>, <c>image</c>, <c>einvoice-xml</c>, ...).</param>
/// <param name="FixedType">The type when it is already fixed (TypeSource Supplier, Human, Hint or Structured): only that type's layouts are searched.</param>
/// <param name="LayoutsRequired">Whether the supplier's <c>layouts.required</c> resolves to true.</param>
/// <param name="ReceivingEntityId">The document's receiving entity, which a layout restricted to an entity needs.</param>
public sealed record LayoutResolverInput(
    IdentificationContext Context,
    IReadOnlyList<LayoutCandidate> Candidates,
    string? ItemKind = null,
    string? FixedType = null,
    bool LayoutsRequired = false,
    Guid? ReceivingEntityId = null);

/// <summary>How the resolver decided (docs/11 §8.4).</summary>
public enum LayoutResolution
{
    /// <summary>Exactly one Active layout holds (or one wins on priority, then specificity): it applies.</summary>
    Identified,

    /// <summary>Several Active layouts hold and tie on priority and specificity: nothing is applied (<c>LAYOUT_AMBIGUOUS</c>).</summary>
    Ambiguous,

    /// <summary>The supplier is layout-managed and no Active layout holds (<c>NEW_LAYOUT</c>).</summary>
    NewLayout,

    /// <summary>The supplier is not layout-managed: today's classification and extraction (§8.4 step 6).</summary>
    NotManaged,

    /// <summary>An e-invoice with no layout holding: its XML carries the type and the fields; no <c>NEW_LAYOUT</c> (§8.4).</summary>
    Exempt,
}

/// <summary>One candidate's evaluation, for the test report and the stats.</summary>
public sealed record LayoutEvaluation(LayoutCandidate Candidate, IdentificationResult Result);

/// <summary>The resolver's answer.</summary>
public sealed record LayoutResolverResult(
    LayoutResolution Resolution,
    LayoutEvaluation? Selected,
    IReadOnlyList<LayoutEvaluation> Tied,
    LayoutEvaluation? Pending,
    IReadOnlyList<LayoutEvaluation> Evaluated,
    bool LayoutManaged)
{
    /// <summary>The Shadow versions whose rule held (stats only, never applied).</summary>
    public IEnumerable<LayoutEvaluation> ShadowHits => Evaluated.Where(e => e.Candidate.State == LayoutCandidateState.Shadow && e.Result.Holds);
}

/// <summary>Selects a document's layout (docs/11 §8.4): a pure function of the document's words, its source facts and its supplier's layouts.</summary>
public interface ILayoutResolver
{
    LayoutResolverResult Resolve(LayoutResolverInput input);
}

/// <summary>
/// The layout resolver of docs/11 §8.4, steps 1 to 6:
/// <list type="number">
/// <item><description>Candidates: the supplier's own versions, or its group's when it has no own Active layout, filtered by
/// item kind, receiving entity and a fixed type.</description></item>
/// <item><description>Every candidate's rule is evaluated strictly (<see cref="IdentificationRuleEvaluator"/>).</description></item>
/// <item><description>Exactly one Active (or Degraded) layout holds: it applies.</description></item>
/// <item><description>Several hold: the highest priority, then the most positive identifiers; a tie is ambiguous.</description></item>
/// <item><description>None holds and the supplier is layout-managed (an Active layout of its own or its group's, of any type, or
/// <c>layouts.required</c>): a new layout; a Candidate whose rule holds is named as pending.</description></item>
/// <item><description>Otherwise the supplier is not layout-managed.</description></item>
/// </list>
/// An e-invoice is never a new layout (§8.4: its XML is not a design).
/// </summary>
public sealed class LayoutResolver : ILayoutResolver
{
    public const string EInvoiceItemKind = "einvoice-xml";

    public static LayoutResolver Instance { get; } = new();

    public LayoutResolverResult Resolve(LayoutResolverInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        static bool Applies(LayoutCandidate c) => c.State is LayoutCandidateState.Active or LayoutCandidateState.Degraded;

        // A supplier's own layouts always win: its group's are only candidates while it has no Active layout of its own.
        var own = input.Candidates.Where(c => c.Scope == LayoutScope.Supplier).ToList();
        var pool = own.Any(Applies) ? own : input.Candidates.Where(c => c.Scope == LayoutScope.Group).ToList();
        if (!own.Any(Applies) && own.Count > 0)
        {
            // Own Candidates still name a pending layout although the group's layouts are the ones that may apply.
            pool = [.. pool, .. own];
        }

        var layoutManaged = input.LayoutsRequired || pool.Any(Applies);
        var filtered = pool
            .Where(c => c.ItemKinds is not { Count: > 0 } kinds || (input.ItemKind is { } kind && kinds.Contains(kind, StringComparer.Ordinal)))
            .Where(c => c.ReceivingEntityId is null || c.ReceivingEntityId == input.ReceivingEntityId)
            .Where(c => input.FixedType is null || string.Equals(c.DocumentTypeKey, input.FixedType, StringComparison.Ordinal))
            .ToList();

        var evaluated = filtered.Select(c => new LayoutEvaluation(c, IdentificationRuleEvaluator.Evaluate(c.Rule, input.Context))).ToList();
        var held = evaluated.Where(e => Applies(e.Candidate) && e.Result.Holds)
            .OrderByDescending(e => e.Candidate.Priority)
            .ThenByDescending(e => e.Candidate.Rule.PositiveCount)
            .ToList();

        if (held.Count > 0)
        {
            var best = held[0];
            var tied = held.Where(e => e.Candidate.Priority == best.Candidate.Priority && e.Candidate.Rule.PositiveCount == best.Candidate.Rule.PositiveCount).ToList();
            return tied.Count > 1
                ? new LayoutResolverResult(LayoutResolution.Ambiguous, null, tied, null, evaluated, layoutManaged)
                : new LayoutResolverResult(LayoutResolution.Identified, best, [], null, evaluated, layoutManaged);
        }

        var pending = evaluated.Where(e => e.Candidate.State == LayoutCandidateState.Candidate && e.Result.Holds)
            .OrderByDescending(e => e.Candidate.Priority)
            .ThenByDescending(e => e.Candidate.Rule.PositiveCount)
            .FirstOrDefault();
        if (string.Equals(input.ItemKind, EInvoiceItemKind, StringComparison.Ordinal))
        {
            return new LayoutResolverResult(LayoutResolution.Exempt, null, [], null, evaluated, layoutManaged);
        }

        return layoutManaged
            ? new LayoutResolverResult(LayoutResolution.NewLayout, null, [], pending, evaluated, true)
            : new LayoutResolverResult(LayoutResolution.NotManaged, null, [], pending, evaluated, false);
    }
}
