using DocProc.Domain.Documents;

namespace DocProc.Domain.Tests;

public class DocumentStateMachineTests
{
    [Fact]
    public void Happy_path_runs_stage_by_stage()
    {
        DocumentStatus[] path =
        [
            DocumentStatus.Received, DocumentStatus.Normalized, DocumentStatus.Split, DocumentStatus.Classified,
            DocumentStatus.Matched, DocumentStatus.Extracted, DocumentStatus.Validated, DocumentStatus.AutoApproved,
        ];

        for (var i = 1; i < path.Length; i++)
        {
            DocumentStateMachine.Transition(path[i - 1], path[i]).ShouldBe(path[i]);
        }
    }

    [Fact]
    public void Review_outcome_can_be_approved_or_rejected()
    {
        DocumentStateMachine.CanTransition(DocumentStatus.Validated, DocumentStatus.NeedsReview).ShouldBeTrue();
        DocumentStateMachine.CanTransition(DocumentStatus.NeedsReview, DocumentStatus.Approved).ShouldBeTrue();
        DocumentStateMachine.CanTransition(DocumentStatus.NeedsReview, DocumentStatus.Rejected).ShouldBeTrue();
    }

    [Fact]
    public void Skipping_a_stage_is_rejected()
    {
        var ex = Should.Throw<InvalidDocumentTransitionException>(() =>
            DocumentStateMachine.Transition(DocumentStatus.Received, DocumentStatus.Extracted));
        ex.From.ShouldBe(DocumentStatus.Received);
        ex.To.ShouldBe(DocumentStatus.Extracted);
    }

    [Theory]
    [InlineData(DocumentStatus.Received)]
    [InlineData(DocumentStatus.Classified)]
    [InlineData(DocumentStatus.Validated)]
    public void Any_in_flight_stage_can_fail(DocumentStatus from) =>
        DocumentStateMachine.CanTransition(from, DocumentStatus.Failed).ShouldBeTrue();

    [Theory]
    [InlineData(DocumentStatus.AutoApproved)]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Rejected)]
    public void Settled_states_cannot_fail_but_can_be_reprocessed(DocumentStatus from)
    {
        DocumentStateMachine.IsSettled(from).ShouldBeTrue();
        DocumentStateMachine.CanTransition(from, DocumentStatus.Failed).ShouldBeFalse();
        DocumentStateMachine.CanTransition(from, DocumentStatus.Received).ShouldBeTrue();
    }

    [Fact]
    public void Failed_documents_retry_from_any_pipeline_stage_but_not_into_a_decision()
    {
        DocumentStateMachine.CanTransition(DocumentStatus.Failed, DocumentStatus.Extracted).ShouldBeTrue();
        DocumentStateMachine.CanTransition(DocumentStatus.Failed, DocumentStatus.Approved).ShouldBeFalse();
        DocumentStateMachine.CanTransition(DocumentStatus.Failed, DocumentStatus.AutoApproved).ShouldBeFalse();
    }

    [Fact]
    public void Approved_covers_both_human_and_automatic_approval()
    {
        DocumentStateMachine.IsApproved(DocumentStatus.Approved).ShouldBeTrue();
        DocumentStateMachine.IsApproved(DocumentStatus.AutoApproved).ShouldBeTrue();
        DocumentStateMachine.IsApproved(DocumentStatus.Rejected).ShouldBeFalse();
    }
}
