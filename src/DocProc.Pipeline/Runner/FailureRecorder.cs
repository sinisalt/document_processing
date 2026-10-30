using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Runner;

/// <summary>
/// Writes the outcome of a failed attempt in a scope of its own, after the handler's scope (and its half-applied
/// changes) is gone: a <c>document.stage_failed</c> event for a scheduled retry, or <c>Failed(reason)</c> plus the
/// event and an optional flag for the final failure (docs/03 "Document state machine").
/// </summary>
internal sealed class FailureRecorder(DocProcDbContext db, DocumentTimeline timeline, DocumentFlagWriter flags, TimeProvider clock)
{
    public const int MaxReasonLength = 2000;

    public async Task RecordRetryAsync(ProcessDocument message, string error, TimeSpan delay, CancellationToken cancellationToken)
    {
        var document = await LoadAsync(message.DocumentId, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return;
        }

        timeline.Record(document, DocumentEventTypes.StageFailed, document.Status, document.Status, new
        {
            stage = message.Stage,
            attempt = message.Attempt,
            error = Truncate(error),
            retry = true,
            retryDelay = delay,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordFailureAsync(ProcessDocument message, string reason, string? flagCode, CancellationToken cancellationToken)
    {
        var document = await LoadAsync(message.DocumentId, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return;
        }

        var details = new { stage = message.Stage, attempt = message.Attempt, error = Truncate(reason), retry = false };
        if (DocumentStateMachine.CanTransition(document.Status, DocumentStatus.Failed))
        {
            timeline.Transition(document, DocumentStatus.Failed, details, DocumentEventTypes.StageFailed);
            document.FailureReason = Truncate(reason);
        }
        else
        {
            // Already failed, or settled by a human in the meantime: keep the audit trail, leave the status alone.
            timeline.Record(document, DocumentEventTypes.StageFailed, document.Status, document.Status, details);
        }

        if (flagCode is not null)
        {
            flags.Replace(document, [flagCode], [FlagCodes.Create(flagCode, Truncate(reason, 1000))]);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordIngestionFailureAsync(ProcessIngestion message, string reason, bool final, CancellationToken cancellationToken)
    {
        var ingestion = await db.Ingestions.SingleOrDefaultAsync(i => i.Id == message.IngestionId, cancellationToken).ConfigureAwait(false);
        if (ingestion is null)
        {
            return;
        }

        ingestion.Attempts = message.Attempt;
        ingestion.LastError = Truncate(reason);
        ingestion.UpdatedAt = clock.GetUtcNow();
        if (final && ingestion.Status == IngestionStatus.Received)
        {
            ingestion.Status = IngestionStatus.Failed;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<Document?> LoadAsync(Guid documentId, CancellationToken cancellationToken) =>
        db.Documents.Include(d => d.Flags).SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

    private static string Truncate(string value, int max = MaxReasonLength) =>
        value.Length <= max ? value : value[..max];
}
