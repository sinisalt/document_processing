using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 9 (docs/03 "Publish and learn"), the outbox half: one <see cref="OutboxMessage"/> per settled document, with
/// the <see cref="DocumentEventMessage"/> as payload, so a delivery job can send webhooks later (docs/05 "Events
/// out"). Idempotent per approval: a second message for the same settlement finds the row and does nothing. Webhook
/// delivery, exports and the learning loop are later blocks.
/// </summary>
internal sealed class PublishHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    ILogger<PublishHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    public override PipelineStage Stage => PipelineStage.Publish;

    protected override bool Accepts(DocumentStatus status) => DocumentStateMachine.IsSettled(status);

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        var eventType = EventTypeOf(document.Status);
        var existing = await Db.OutboxMessages.AsNoTracking()
            .Where(m => m.DocumentId == document.Id)
            .Select(m => new PublishedEvent(m.EventType, m.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (IsPublished(document.Status, document.ApprovedAt, document.UpdatedAt, existing))
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Document {DocumentId}: {EventType} is already in the outbox; message dropped.", document.Id, eventType);
            }

            return StageOutcome.Skipped;
        }

        var now = Timeline.Now;
        var payload = new DocumentEventMessage(document.TenantId, document.Id, eventType, now);
        var outbox = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            TenantId = document.TenantId,
            DocumentId = document.Id,
            EventType = eventType,
            PayloadJson = PipelineJson.Serialize(payload),
            CreatedAt = now,
        };
        Db.OutboxMessages.Add(outbox);
        Timeline.Record(document, DocumentEventTypes.StageCompleted, document.Status, document.Status, new { stage = PipelineStage.Publish, eventType, outboxMessageId = outbox.Id });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Stopped;
    }

    /// <summary>The outbox event a settled state publishes.</summary>
    internal static string EventTypeOf(DocumentStatus status) =>
        status == DocumentStatus.Rejected ? DocumentEventTypes.Rejected : DocumentEventTypes.Approved;

    /// <summary>
    /// Whether the current settlement is already in the outbox: a row of its event type written at or after it. A row
    /// of an earlier settlement (rejected, reprocessed, settled again) does not count. The demo resume uses the same
    /// rule, so it re-queues exactly the documents this stage would publish.
    /// </summary>
    internal static bool IsPublished(DocumentStatus status, DateTimeOffset? approvedAt, DateTimeOffset updatedAt, IEnumerable<PublishedEvent> outbox)
    {
        var eventType = EventTypeOf(status);
        var settledAt = approvedAt ?? updatedAt;
        return outbox.Any(m => string.Equals(m.EventType, eventType, StringComparison.Ordinal) && m.CreatedAt >= settledAt);
    }
}

/// <summary>One outbox row of a document, as <see cref="PublishHandler.IsPublished"/> reads it.</summary>
internal sealed record PublishedEvent(string EventType, DateTimeOffset CreatedAt);
