using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 9 (docs/03 "Publish and learn"), the outbox half. The decision that settles a document (approve, reject,
/// auto-approval) writes its <see cref="OutboxMessage"/> in its own transaction (docs/11 §13.5), so the message that
/// queues this stage is only a nudge: the stage hands the current settlement's row on by setting
/// <see cref="OutboxMessage.FannedOutAt"/> and records the publish in the timeline. For a tenant with the
/// <c>outputs</c> switch the same save adds the deliveries of its bindings (<see cref="DeliveryFanOut"/>, docs/11
/// §13.5), which are queued after the commit; without it handing on is the publish itself, as before P3. A settlement without a row (one decided before the decision wrote it) gets one
/// here, as before. Idempotent per settlement: a row already handed on means the message is dropped, two publishers
/// racing for the same row are told apart by its concurrency token, and two that both found no row by the document's
/// (<see cref="DocumentOutbox.AddMissing"/>); the loser's save fails and its message is dropped. A message from the
/// <see cref="Runner.PublishSweeperHostedService"/> says so in the timeline (<c>recoveredBy</c>).
/// </summary>
internal sealed class PublishHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DeliveryFanOut fanOut,
    IPipelineQueue queue,
    ILogger<PublishHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    public override PipelineStage Stage => PipelineStage.Publish;

    protected override bool Accepts(DocumentStatus status) => DocumentStateMachine.IsSettled(status);

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        var eventType = EventTypeOf(document.Status);
        var current = (await Db.OutboxMessages
                .Where(m => m.DocumentId == document.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(m => DocumentOutbox.IsOfSettlement(m.EventType, m.CreatedAt, document.Status, document.ApprovedAt, document.UpdatedAt))
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToList();
        if (current.Any(m => m.FannedOutAt is not null))
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Document {DocumentId}: {EventType} is already published; message dropped.", document.Id, eventType);
            }

            return StageOutcome.Skipped;
        }

        var now = Timeline.Now;
        var outbox = current.FirstOrDefault();
        if (outbox is null)
        {
            // Claim first, so the row, the deliveries and the values view they render carry the version the document
            // keeps (a rerender of such a delivery is then accepted, docs/11 §13.6); a second publisher loses here.
            if (!await DocumentOutbox.ClaimMissingAsync(Db, document, cancellationToken).ConfigureAwait(false))
            {
                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInformation("Document {DocumentId}: another publisher claimed the missing {EventType} row; message dropped.", document.Id, eventType);
                }
                return StageOutcome.Skipped;
            }

            outbox = DocumentOutbox.AddMissing(Db, document, now);
        }

        outbox.FannedOutAt = now;
        var recoveredBy = string.Equals(message.Origin, PipelineMessageOrigins.PublishSweeper, StringComparison.Ordinal) ? message.Origin : null;
        if (recoveredBy is not null)
        {
            Logger.LogWarning("Document {DocumentId}: {EventType} was settled at {SettledAt} but never published; published by the publish sweeper.", document.Id, eventType, DocumentOutbox.SettledAt(document.ApprovedAt, document.UpdatedAt));
        }

        // The fan-out (docs/11 §13.5) renders and adds the deliveries of the tenant's bindings, so they commit with the
        // hand-on; a tenant without the outputs switch has none and publishes exactly as before.
        var fan = await fanOut.FanOutAsync(document, outbox, cancellationToken).ConfigureAwait(false);
        var deliveries = fan.Entries.Count == 0 ? null : fan.Entries;
        Timeline.Record(document, DocumentEventTypes.StageCompleted, document.Status, document.Status, new { stage = PipelineStage.Publish, eventType, outboxMessageId = outbox.Id, recoveredBy, deliveries });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The nudge: lost with the process, the delivery sweeper finds the row (docs/11 §13.6).
        foreach (var delivery in fan.ToSend)
        {
            await queue.EnqueueAsync(new ProcessDelivery(delivery.TenantId, delivery.Id, delivery.DestinationId, delivery.DocumentId), TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }

        return StageOutcome.Stopped;
    }

    /// <summary>The outbox event a settled state publishes.</summary>
    internal static string EventTypeOf(DocumentStatus status) => DocumentOutbox.EventTypeOf(status);

    /// <summary>
    /// Whether the current settlement is in the outbox: a row of its event type written at or after it. A row of an
    /// earlier settlement (rejected, reprocessed, settled again) does not count. The demo resume uses this rule to
    /// re-queue settlements that have no row at all; a row that was written but not handed on is the sweeper's.
    /// </summary>
    internal static bool IsPublished(DocumentStatus status, DateTimeOffset? approvedAt, DateTimeOffset updatedAt, IEnumerable<PublishedEvent> outbox) =>
        outbox.Any(m => DocumentOutbox.IsOfSettlement(m.EventType, m.CreatedAt, status, approvedAt, updatedAt));
}

/// <summary>One outbox row of a document, as <see cref="PublishHandler.IsPublished"/> reads it.</summary>
internal sealed record PublishedEvent(string EventType, DateTimeOffset CreatedAt);
