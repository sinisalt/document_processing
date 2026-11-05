using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Outputs.Delivery;

/// <summary>What one <see cref="ProcessDelivery"/> came to; <see cref="Retry"/> is the next attempt to queue, with its delay.</summary>
public sealed record DeliveryRun(string Result, ProcessDelivery? Retry = null, TimeSpan RetryDelay = default)
{
    public static DeliveryRun Dropped(string why) => new("dropped: " + why);
}

/// <summary>
/// One attempt at a delivery (docs/11 §13.6), the work of <c>ProcessDelivery</c>:
/// <list type="number">
/// <item><description>The message must be for the attempt the row expects (its <c>Attempts</c> plus one) and the row
/// waiting (<c>Pending</c>, <c>Failed</c>, or <c>Sending</c> whose lease ran out: a process died mid-send);
/// anything else is a copy (the nudge and a sweep, a retry and a sweep) and is dropped.</description></item>
/// <item><description>Ordering: a fan-out delivery of an older revision is <c>Skipped</c> when a newer one exists
/// for the same binding and destination (the runner already serialises <c>destination:document</c>).</description></item>
/// <item><description>The row is claimed (<c>Sending</c>, the attempt counted, a lease of the timeout plus
/// <see cref="DeliveryOptions.SendLease"/>) under its concurrency token, so of two hosts only one sends.</description></item>
/// <item><description>The transport sends the payload snapshot; a <c>pull</c> destination publishes it to its feed
/// instead, where it waits (<c>Sending</c>) for the client's acknowledgement.</description></item>
/// <item><description>The attempt is written (<see cref="DeliveryAttempt"/>), with the timeline event; a success
/// stores the external id and resolves <c>DELIVERY_FAILED</c>; a retryable failure waits for the backoff
/// (<see cref="DeliveryBackoff"/>, or the receiver's <c>Retry-After</c>); a permanent one, or the 15th attempt, or 72 h,
/// dead-letters the delivery and raises <c>DELIVERY_FAILED</c>.</description></item>
/// </list>
/// </summary>
public sealed class DeliveryProcessor(
    DocProcDbContext db,
    IEnumerable<IDeliveryTransport> transports,
    IContainerBlobStore blobs,
    TimeProvider clock,
    IOptions<DeliveryOptions> options,
    ILogger<DeliveryProcessor> logger)
{
    private readonly DeliveryOptions _options = options.Value;

    public async Task<DeliveryRun> ProcessAsync(ProcessDelivery message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var delivery = await db.OutboxDeliveries
            .Include(d => d.Binding)
            .Include(d => d.Destination)
            .Include(d => d.MappingVersion)
            .SingleOrDefaultAsync(d => d.Id == message.DeliveryId, cancellationToken).ConfigureAwait(false);
        if (delivery is null)
        {
            return DeliveryRun.Dropped("no such delivery in the tenant's scope");
        }

        var now = clock.GetUtcNow();
        if (delivery.Attempts != message.Attempt - 1)
        {
            return DeliveryRun.Dropped($"the message is for attempt {message.Attempt.ToString(CultureInfo.InvariantCulture)}, the row has made {delivery.Attempts.ToString(CultureInfo.InvariantCulture)}");
        }

        var waiting = delivery.Status is DeliveryStatus.Pending or DeliveryStatus.Failed
            || (delivery.Status == DeliveryStatus.Sending && delivery.NextAttemptAt is { } lease && lease <= now);
        if (!waiting)
        {
            return DeliveryRun.Dropped($"the delivery is {delivery.Status}");
        }

        var destination = delivery.Destination!;
        var bindingName = delivery.Binding?.Name ?? delivery.BindingId.ToString("D");
        if (delivery.RedeliveryOfId is null)
        {
            var newer = await db.OutboxDeliveries.AnyAsync(
                d => d.DocumentId == delivery.DocumentId && d.BindingId == delivery.BindingId && d.DestinationId == delivery.DestinationId
                     && d.DocumentVersion > delivery.DocumentVersion && d.RedeliveryOfId == null
                     && d.Status != DeliveryStatus.Skipped && d.Status != DeliveryStatus.Cancelled,
                cancellationToken).ConfigureAwait(false);
            if (newer)
            {
                delivery.Status = DeliveryStatus.Skipped;
                delivery.NextAttemptAt = null;
                delivery.LastError = "Superseded by a newer revision before it was delivered.";
                DeliveryJournal.Record(db, delivery, DocumentEventTypes.DeliverySkipped, new { deliveryId = delivery.Id, binding = bindingName, destination = destination.Name, revision = delivery.DocumentVersion, reason = delivery.LastError }, now);
                return await SaveAsync("skipped: superseded", cancellationToken).ConfigureAwait(false);
            }
        }

        if (destination.Status != OutputDestinationStatus.Active)
        {
            delivery.Status = DeliveryStatus.Cancelled;
            delivery.NextAttemptAt = null;
            delivery.LastError = "The destination is disabled; redeliver once it is active again.";
            DeliveryJournal.Record(db, delivery, DocumentEventTypes.DeliverySkipped, new { deliveryId = delivery.Id, binding = bindingName, destination = destination.Name, revision = delivery.DocumentVersion, reason = delivery.LastError }, now);
            return await SaveAsync("cancelled: destination disabled", cancellationToken).ConfigureAwait(false);
        }

        var config = JsonNode.Parse(destination.ConfigJson) as JsonObject ?? [];
        var timeout = TimeSpan.FromSeconds(config["timeoutSeconds"] is JsonValue t && t.TryGetValue<int>(out var seconds) ? seconds : HttpDeliveryTransport.DefaultTimeoutSeconds);

        // Claim: of two hosts that got a copy of this attempt, the one whose save lands first sends.
        delivery.Status = DeliveryStatus.Sending;
        delivery.Attempts++;
        delivery.NextAttemptAt = now + timeout + _options.SendLease;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DeliveryRun.Dropped("another attempt claimed the delivery first");
        }

        DeliveryOutcome outcome;
        var payload = await PayloadAsync(delivery, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            outcome = DeliveryOutcome.Permanent("snapshot", "The payload snapshot is missing from the exports store; redeliver with mode rerender.");
        }
        else if (destination.Transport == OutputTransports.Pull)
        {
            return await PublishToFeedAsync(delivery, destination, bindingName, cancellationToken).ConfigureAwait(false);
        }
        else if (transports.FirstOrDefault(x => x.Transport == destination.Transport) is not { } transport)
        {
            outcome = DeliveryOutcome.Permanent("transport", $"This build cannot send over '{destination.Transport}'.");
        }
        else
        {
            var typeKey = await db.Documents.Where(d => d.Id == delivery.DocumentId).Select(d => d.Type).SingleAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? output = null;
            if (delivery.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != false)
            {
                try
                {
                    output = JsonNode.Parse(payload);
                }
                catch (JsonException)
                {
                    output = null;
                }
            }

            var values = new DeliveryTemplateValues(delivery.DocumentId, delivery.DocumentVersion, delivery.Event, typeKey.Value, delivery.Id, output);
            var context = new DeliveryContext(delivery, destination, config, payload, delivery.ContentType ?? "application/json", values, delivery.MappingVersion?.FileNameTemplate, ct => OriginalAsync(delivery.DocumentId, ct));
            try
            {
                outcome = await transport.SendAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Delivery {DeliveryId}: the {Transport} transport failed unexpectedly.", delivery.Id, destination.Transport);
                outcome = DeliveryOutcome.Retryable("transport", $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        return await RecordAsync(delivery, destination, bindingName, outcome, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the attempt and moves the delivery on; the caller queues <see cref="DeliveryRun.Retry"/>.</summary>
    private async Task<DeliveryRun> RecordAsync(OutboxDelivery delivery, OutputDestination destination, string bindingName, DeliveryOutcome outcome, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            TenantId = delivery.TenantId,
            DeliveryId = delivery.Id,
            At = now,
            DurationMs = outcome.DurationMs,
            StatusCode = outcome.StatusCode,
            ErrorClass = outcome.ErrorClass,
            ResponseExcerpt = Excerpt(outcome),
        });
        delivery.LastStatusCode = outcome.StatusCode;
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["deliveryId"] = delivery.Id,
            ["binding"] = bindingName,
            ["destination"] = destination.Name,
            ["transport"] = destination.Transport,
            ["revision"] = delivery.DocumentVersion,
            ["event"] = delivery.Event,
            ["attempt"] = delivery.Attempts,
            ["statusCode"] = outcome.StatusCode,
            ["errorClass"] = outcome.ErrorClass,
        };

        if (outcome.Kind == DeliveryOutcomeKind.Delivered)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.DeliveredAt = now;
            delivery.NextAttemptAt = null;
            delivery.LastError = null;
            // A destination that reports back (docs/11 §13.8, callback or status poll) keeps the reference Pending until
            // it does; one without a mapped id is known by the document id until its first answer names its own.
            var acknowledgements = Acknowledgements.AcknowledgementConfig.Of(destination.CallbackConfigJson);
            if (outcome.ExternalId is { } externalId)
            {
                await StoreExternalIdAsync(delivery, externalId, acknowledgements, now, cancellationToken).ConfigureAwait(false);
                details["externalId"] = externalId;
            }
            else if (acknowledgements.Any)
            {
                await StoreExternalIdAsync(delivery, delivery.DocumentId.ToString(), acknowledgements, now, cancellationToken).ConfigureAwait(false);
            }

            if (outcome.Note is { } note)
            {
                details["note"] = note;
            }

            DeliveryJournal.Record(db, delivery, DocumentEventTypes.DeliverySucceeded, details, now);
            await DeliveryJournal.ResolveFailedAsync(db, delivery, now, DeliveryJournal.Actor, cancellationToken).ConfigureAwait(false);
            return await SaveAsync("delivered", cancellationToken).ConfigureAwait(false);
        }

        delivery.LastError = Truncate(outcome.Error ?? outcome.ErrorClass ?? "failed", 2000);
        details["error"] = delivery.LastError;
        var exhausted = delivery.Attempts >= _options.MaxAttempts || now - delivery.CreatedAt >= _options.MaxAge;
        if (outcome.Kind == DeliveryOutcomeKind.Retryable && !exhausted)
        {
            var delay = DeliveryBackoff.Delay(delivery.Attempts, outcome.RetryAfter, _options, Random.Shared);
            delivery.Status = DeliveryStatus.Failed;
            delivery.NextAttemptAt = now + delay;
            details["nextAttemptAt"] = delivery.NextAttemptAt;
            DeliveryJournal.Record(db, delivery, DocumentEventTypes.DeliveryFailed, details, now);
            var saved = await SaveAsync("retrying", cancellationToken).ConfigureAwait(false);
            return saved.Result != "retrying"
                ? saved
                : saved with { Retry = new ProcessDelivery(delivery.TenantId, delivery.Id, delivery.DestinationId, delivery.DocumentId, delivery.Attempts + 1), RetryDelay = delay };
        }

        delivery.Status = DeliveryStatus.DeadLettered;
        delivery.NextAttemptAt = null;
        details["deadLettered"] = true;
        if (outcome.Kind == DeliveryOutcomeKind.Retryable)
        {
            details["exhausted"] = delivery.Attempts >= _options.MaxAttempts ? "attempts" : "age";
        }

        DeliveryJournal.Record(db, delivery, DocumentEventTypes.DeliveryFailed, details, now);
        await DeliveryJournal.RaiseFailedAsync(db, delivery, destination.Name, delivery.LastError, now, cancellationToken).ConfigureAwait(false);
        return await SaveAsync("dead-lettered", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pull transport: the delivery takes the destination's next feed sequence number in one transaction with the
    /// counter's increment, which holds the destination row until it commits, so the feed's order is its commit order.
    /// It then waits in the feed (<c>Sending</c>, no lease) until the client acknowledges it.
    /// </summary>
    private async Task<DeliveryRun> PublishToFeedAsync(OutboxDelivery delivery, OutputDestination destination, string bindingName, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var sequence = await strategy.ExecuteAsync(
            async ct =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await db.OutputDestinations.Where(d => d.Id == destination.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.FeedSequence, d => d.FeedSequence + 1), ct).ConfigureAwait(false);
                var next = await db.OutputDestinations.Where(d => d.Id == destination.Id).Select(d => d.FeedSequence).SingleAsync(ct).ConfigureAwait(false);
                var now = clock.GetUtcNow();
                delivery.FeedSequence = next;
                delivery.NextAttemptAt = null;
                delivery.LastError = null;
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    TenantId = delivery.TenantId,
                    DeliveryId = delivery.Id,
                    At = now,
                    ErrorClass = "published",
                    ResponseExcerpt = $"In the feed as entry {next.ToString(CultureInfo.InvariantCulture)}; delivered when the client acknowledges it.",
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return next;
            },
            cancellationToken).ConfigureAwait(false);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Delivery {DeliveryId} ({Binding}) is entry {Sequence} of feed {Destination}.", delivery.Id, bindingName, sequence, destination.Id);
        }

        return new DeliveryRun("published to feed");
    }

    private async Task<DeliveryRun> SaveAsync(string result, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new DeliveryRun(result);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A redelivery or an acknowledgement changed the row meanwhile; its state stands.
            return DeliveryRun.Dropped("the delivery changed while the attempt ran");
        }
    }

    private async Task StoreExternalIdAsync(OutboxDelivery delivery, string externalId, Acknowledgements.AcknowledgementConfig acknowledgements, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Without acknowledgements the 2xx is the receiver's acceptance, as before; with them the receiver says later.
        var status = acknowledgements.Any ? ExternalRefStatus.Pending : ExternalRefStatus.Accepted;
        var nextPoll = Acknowledgements.ExternalAcknowledgements.FirstPollAt(acknowledgements, now);
        var reference = await db.DocumentExternalRefs.SingleOrDefaultAsync(r => r.DocumentId == delivery.DocumentId && r.DestinationId == delivery.DestinationId, cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            db.DocumentExternalRefs.Add(new DocumentExternalRef
            {
                Id = Guid.CreateVersion7(),
                TenantId = delivery.TenantId,
                DocumentId = delivery.DocumentId,
                DestinationId = delivery.DestinationId,
                ExternalId = externalId,
                Status = status,
                UpdatedAt = now,
                NextPollAt = nextPoll,
            });
            return;
        }

        reference.ExternalId = externalId;
        reference.Status = status;
        reference.Message = null;
        reference.UpdatedAt = now;
        reference.NextPollAt = nextPoll;
        reference.Polls = 0;
    }

    private async Task<byte[]?> PayloadAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.PayloadBlobKey is not { } key)
        {
            return null;
        }

        var reference = await blobs.FindAsync(BlobContainers.Exports, key, cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            return null;
        }

        await using var stream = await blobs.OpenReadAsync(reference, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async Task<OriginalFile?> OriginalAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var item = await db.Documents.Where(d => d.Id == documentId)
            .Select(d => new { d.IngestionItem!.FileName, d.IngestionItem.MimeType, d.IngestionItem.Blob })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var reference = new BlobRef(item.Blob.Container, item.Blob.Key, item.Blob.Sha256, item.Blob.SizeBytes, item.Blob.ContentType);
        try
        {
            await using var stream = await blobs.OpenReadAsync(reference, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return new OriginalFile(buffer.ToArray(), item.FileName, item.MimeType);
        }
        catch (BlobNotFoundException)
        {
            return null;
        }
    }

    private static string? Excerpt(DeliveryOutcome outcome)
    {
        var text = outcome.Note is { } note ? (outcome.ResponseExcerpt is { } excerpt ? note + "\n" + excerpt : note) : outcome.ResponseExcerpt ?? outcome.Error;
        return text is null ? null : Truncate(text, DeliveryAttempt.MaxExcerptLength);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}

/// <summary>
/// The waits of docs/11 §13.6: entry n of <see cref="DeliveryOptions.EffectiveRetrySchedule"/> after attempt n (the
/// last entry repeats) with up to 10 % jitter either way, or the receiver's <c>Retry-After</c> (at least 1 s, at most
/// <see cref="DeliveryOptions.MaxRetryAfter"/>) when it sent one.
/// </summary>
public static class DeliveryBackoff
{
    public static TimeSpan Delay(int attemptsMade, TimeSpan? retryAfter, DeliveryOptions options, Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(random);
        if (retryAfter is { } asked)
        {
            return asked < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : asked > options.MaxRetryAfter ? options.MaxRetryAfter : asked;
        }

        var schedule = options.EffectiveRetrySchedule;
        var step = schedule[Math.Clamp(attemptsMade - 1, 0, schedule.Count - 1)];
        var jitter = 1 + ((random.NextDouble() * 0.2) - 0.1);
        return TimeSpan.FromTicks((long)(step.Ticks * jitter));
    }
}
