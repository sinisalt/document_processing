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
}
