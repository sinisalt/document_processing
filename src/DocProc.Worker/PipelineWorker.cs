namespace DocProc.Worker;

/// <summary>
/// Placeholder host loop. Phase 1 replaces this with Wolverine handlers per pipeline stage
/// (docs/03) consuming from Azure Service Bus; the host, configuration and health wiring stay.
/// </summary>
public sealed class PipelineWorker(ILogger<PipelineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DocProc worker started; no stage handlers are registered yet.");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }
}
