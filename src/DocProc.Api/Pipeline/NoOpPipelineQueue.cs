using DocProc.Application.Pipeline;

namespace DocProc.Api.Pipeline;

/// <summary>
/// The queue the API falls back to when no pipeline runner is registered (an API host started on its own, the
/// OpenAPI export, smoke tests): messages are logged and dropped, so uploads and review actions still succeed and
/// the documents simply wait until a worker picks them up from the database. Registered with
/// <c>TryAddSingleton</c> after everything else, so a real queue always wins.
/// </summary>
public sealed class NoOpPipelineQueue(ILogger<NoOpPipelineQueue> logger) : IPipelineQueue
{
    public Task EnqueueAsync(ProcessIngestion message, CancellationToken cancellationToken)
    {
        logger.LogWarning("No pipeline queue is registered; ingestion {IngestionId} of tenant {TenantId} is not scheduled.", message.IngestionId, message.TenantId);
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(ProcessDocument message, CancellationToken cancellationToken)
    {
        logger.LogWarning("No pipeline queue is registered; stage {Stage} of document {DocumentId} is not scheduled.", message.Stage, message.DocumentId);
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(ProcessDocument message, TimeSpan delay, CancellationToken cancellationToken) => EnqueueAsync(message, cancellationToken);
}
