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
/// settled document may be reprocessed from the start, and a reviewed document may be reclassified.
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
        [DocumentStatus.Validated] = [DocumentStatus.AutoApproved, DocumentStatus.NeedsReview, DocumentStatus.Failed],
        [DocumentStatus.NeedsReview] = [DocumentStatus.Approved, DocumentStatus.Rejected, DocumentStatus.Classified, DocumentStatus.Received],
        [DocumentStatus.AutoApproved] = [DocumentStatus.Received],
        [DocumentStatus.Approved] = [DocumentStatus.Received],
        [DocumentStatus.Rejected] = [DocumentStatus.Received],
        [DocumentStatus.Failed] = [DocumentStatus.Received, DocumentStatus.Normalized, DocumentStatus.Split, DocumentStatus.Classified, DocumentStatus.Matched, DocumentStatus.Extracted, DocumentStatus.Validated],
    }.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToFrozenSet());

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
}
