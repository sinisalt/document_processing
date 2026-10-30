using System.Collections.Frozen;

namespace DocProc.Domain.Documents;

/// <summary>Document lifecycle states (docs/03-pipeline.md "Document state machine").</summary>
public enum DocumentStatus
{
    Received,
    Normalized,
    Split,
    Classified,
    Matched,
    Extracted,
    Validated,
    AutoApproved,
    NeedsReview,
    Approved,
    Rejected,
    Failed,
}

public sealed class InvalidDocumentTransitionException(DocumentStatus from, DocumentStatus to)
    : InvalidOperationException($"A document cannot move from {from} to {to}.")
{
    public DocumentStatus From { get; } = from;
    public DocumentStatus To { get; } = to;
}

/// <summary>
/// Allowed transitions. The happy path runs stage by stage; any in-flight state may fail; a failed or
/// settled document may be reprocessed from the start, and a reviewed document may be reclassified. A human may
/// also decide a document the pipeline has validated but not yet routed (Validated → Approved / Rejected), so the
/// review API never has to wait for the review-routing step. Reprocessing from a later stage is a separate
/// transition set (<see cref="CanReprocess"/>).
/// </summary>
public static class DocumentStateMachine
{
    private static readonly FrozenDictionary<DocumentStatus, FrozenSet<DocumentStatus>> Allowed = new Dictionary<DocumentStatus, DocumentStatus[]>
    {
        [DocumentStatus.Received] = [DocumentStatus.Normalized, DocumentStatus.Failed],
        [DocumentStatus.Normalized] = [DocumentStatus.Split, DocumentStatus.Failed],
        [DocumentStatus.Split] = [DocumentStatus.Classified, DocumentStatus.Failed],
        [DocumentStatus.Classified] = [DocumentStatus.Matched, DocumentStatus.NeedsReview, DocumentStatus.Failed],
        [DocumentStatus.Matched] = [DocumentStatus.Extracted, DocumentStatus.Failed],
        [DocumentStatus.Extracted] = [DocumentStatus.Validated, DocumentStatus.Failed],
        [DocumentStatus.Validated] = [DocumentStatus.AutoApproved, DocumentStatus.NeedsReview, DocumentStatus.Approved, DocumentStatus.Rejected, DocumentStatus.Failed],
        [DocumentStatus.NeedsReview] = [DocumentStatus.Approved, DocumentStatus.Rejected, DocumentStatus.Classified, DocumentStatus.Received],
        [DocumentStatus.AutoApproved] = [DocumentStatus.Received],
        [DocumentStatus.Approved] = [DocumentStatus.Received],
        [DocumentStatus.Rejected] = [DocumentStatus.Received],
        [DocumentStatus.Failed] = [DocumentStatus.Received, DocumentStatus.Normalized, DocumentStatus.Split, DocumentStatus.Classified, DocumentStatus.Matched, DocumentStatus.Extracted, DocumentStatus.Validated],
    }.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToFrozenSet());

    /// <summary>
    /// States a human may send back into the pipeline with <c>:reprocess</c> (docs/05): a failed document, one
    /// waiting for review, a rejected one, or one the pipeline validated but nobody decided on yet. Approved
    /// documents are deliberately absent: an approval has been published and must be reverted explicitly.
    /// </summary>
    private static readonly FrozenSet<DocumentStatus> Reprocessable =
        new[] { DocumentStatus.Failed, DocumentStatus.NeedsReview, DocumentStatus.Rejected, DocumentStatus.Validated }.ToFrozenSet();

    /// <summary>The pipeline states a reprocess may reset to: the status left behind by the stage before the one that reruns.</summary>
    private static readonly FrozenSet<DocumentStatus> ReprocessTargets =
        new[] { DocumentStatus.Received, DocumentStatus.Normalized, DocumentStatus.Split, DocumentStatus.Classified, DocumentStatus.Matched, DocumentStatus.Extracted }.ToFrozenSet();

    /// <summary>States in which a human or the pipeline has given a final answer for this processing round.</summary>
    public static bool IsSettled(DocumentStatus status) =>
        status is DocumentStatus.AutoApproved or DocumentStatus.Approved or DocumentStatus.Rejected;

    /// <summary>States that count as approved for publishing and the gold set.</summary>
    public static bool IsApproved(DocumentStatus status) =>
        status is DocumentStatus.AutoApproved or DocumentStatus.Approved;

    public static bool CanTransition(DocumentStatus from, DocumentStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static DocumentStatus Transition(DocumentStatus from, DocumentStatus to) =>
        CanTransition(from, to) ? to : throw new InvalidDocumentTransitionException(from, to);

    public static IReadOnlySet<DocumentStatus> NextStates(DocumentStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : FrozenSet<DocumentStatus>.Empty;

    /// <summary>
    /// Whether a human may reset the document from <paramref name="from"/> to the pipeline state <paramref name="to"/>
    /// so the stages after it run again. Distinct from <see cref="CanTransition"/>, which describes what the pipeline
    /// itself does next; a reprocess jumps backwards on purpose.
    /// </summary>
    public static bool CanReprocess(DocumentStatus from, DocumentStatus to) =>
        Reprocessable.Contains(from) && ReprocessTargets.Contains(to);

    public static DocumentStatus Reprocess(DocumentStatus from, DocumentStatus to) =>
        CanReprocess(from, to) ? to : throw new InvalidDocumentTransitionException(from, to);
}
