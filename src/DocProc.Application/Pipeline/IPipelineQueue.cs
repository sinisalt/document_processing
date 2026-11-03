namespace DocProc.Application.Pipeline;

/// <summary>
/// Durable work queue between the API and the worker and between worker stages (docs/03). Local development and
/// tests use an in-process channel; Azure uses Service Bus (one queue per stage, see the AppHost). Enqueue must be
/// idempotent-safe for consumers: every handler re-checks the document state before doing work.
/// </summary>
public interface IPipelineQueue
{
    Task EnqueueAsync(ProcessIngestion message, CancellationToken cancellationToken);

    Task EnqueueAsync(ProcessDocument message, CancellationToken cancellationToken);

    /// <summary>Schedules a document step after a delay (retry with backoff, LLM_PROVIDER_UNAVAILABLE wait).</summary>
    Task EnqueueAsync(ProcessDocument message, TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>
    /// Schedules one attempt at a delivery (docs/11 §13.6), at once (<paramref name="delay"/> zero, the nudge after the
    /// fan-out or a redelivery) or after a retry's backoff. Lossy like every message of the in-process queue: the
    /// delivery row in the database is the truth, and the delivery sweeper queues what was lost.
    /// </summary>
    Task EnqueueAsync(ProcessDelivery message, TimeSpan delay, CancellationToken cancellationToken);
}
