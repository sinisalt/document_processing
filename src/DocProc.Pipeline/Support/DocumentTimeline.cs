using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Pipeline.Support;

/// <summary>
/// Writes the append-only timeline of docs/03 "Document state machine" and moves documents through it. Every
/// transition goes through <see cref="DocumentStateMachine"/>, so a handler that runs out of order fails loudly
/// instead of corrupting the state. Nothing is saved here; the handler's <c>SaveChangesAsync</c> commits the stage.
/// </summary>
internal sealed class DocumentTimeline(DocProcDbContext db, TimeProvider clock)
{
    public const string Actor = "pipeline";

    public DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>Records an event without changing the status.</summary>
    public DocumentEvent Record(Document document, string eventType, DocumentStatus? from, DocumentStatus? to, object? details = null)
    {
        var entry = new DocumentEvent
        {
            TenantId = document.TenantId,
            DocumentId = document.Id,
            EventType = eventType,
            FromStatus = from,
            ToStatus = to,
            Actor = Actor,
            DetailsJson = details is null ? null : PipelineJson.Serialize(details),
            At = Now,
        };
        db.DocumentEvents.Add(entry);
        return entry;
    }

    /// <summary>Moves the document to <paramref name="to"/> (validated against the state machine) and records the transition.</summary>
    public void Transition(Document document, DocumentStatus to, object? details = null, string eventType = DocumentEventTypes.StageCompleted)
    {
        var from = document.Status;
        document.Status = DocumentStateMachine.Transition(from, to);
        document.UpdatedAt = Now;
        if (to != DocumentStatus.Failed)
        {
            document.FailureReason = null;
        }

        Record(document, eventType, from, to, details);
    }
}
