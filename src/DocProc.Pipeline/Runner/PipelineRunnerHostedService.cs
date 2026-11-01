using DocProc.Application.Pipeline;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Pipeline.Queue;
using DocProc.Pipeline.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Runner;

/// <summary>
/// Consumes the pipeline queue with <see cref="PipelineOptions.MaxParallelism"/> workers. Every message gets a DI
/// scope of its own with the scoped <see cref="TenantContext"/> set to the message's tenant, so the DbContext filters
/// and the stage code never see another tenant's rows. Messages with the same <see cref="PipelineWorkItem.Key"/>
/// (one document, one ingestion) run one after the other through <see cref="KeyedWorkGate"/>, so a message delivered
/// twice cannot race its twin past the status check. A document stage that succeeds enqueues the next stage; a
/// failure goes through <see cref="FailurePolicy"/> and is recorded in a fresh scope by <see cref="FailureRecorder"/>.
/// </summary>
public sealed class PipelineRunnerHostedService(
    IPipelineQueueReader reader,
    IPipelineQueue queue,
    IServiceScopeFactory scopes,
    IOptions<PipelineOptions> options,
    ILogger<PipelineRunnerHostedService> logger) : BackgroundService
{
    private readonly PipelineOptions _options = options.Value;
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _running = [];
    private readonly KeyedWorkGate _keys = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parallelism = Math.Max(1, _options.MaxParallelism);
        using var slots = new SemaphoreSlim(parallelism, parallelism);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Pipeline runner started with parallelism {Parallelism}.", parallelism);
        }

        try
        {
            await foreach (var item in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
                var work = Task.Run(
                    async () =>
                    {
                        try
                        {
                            // The slot is held while waiting for a twin with the same key: the twin never waits for
                            // a slot itself, so the wait always ends, and a queue full of one document's messages
                            // only costs throughput, never a deadlock.
                            using (await _keys.EnterAsync(item.Key, stoppingToken).ConfigureAwait(false))
                            {
                                await ProcessAsync(item, stoppingToken).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            logger.LogWarning("Pipeline message for tenant {TenantId} was still waiting for its turn at shutdown and is lost (in-process queue).", item.TenantId);
                        }
                        finally
                        {
                            slots.Release();
                            reader.Acknowledge(item);
                        }
                    },
                    CancellationToken.None);
                Remember(work);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }

        await DrainAsync().ConfigureAwait(false);
        logger.LogInformation("Pipeline runner stopped.");
    }

    private async Task ProcessAsync(PipelineWorkItem item, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(item.TenantId);
            switch (item)
            {
                case IngestionWorkItem ingestion:
                    await scope.ServiceProvider.GetRequiredService<IIngestionHandler>().HandleAsync(ingestion.Message, cancellationToken).ConfigureAwait(false);
                    break;

                case DocumentWorkItem document:
                    await ProcessDocumentAsync(scope.ServiceProvider, document.Message, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    logger.LogError("Unknown pipeline work item {ItemType}; dropped.", item.GetType().Name);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Pipeline message for tenant {TenantId} was interrupted by shutdown and is lost (in-process queue).", item.TenantId);
        }
        catch (Exception exception)
        {
            await HandleFailureAsync(item, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessDocumentAsync(IServiceProvider services, ProcessDocument message, CancellationToken cancellationToken)
    {
        var handler = services.GetServices<IStageHandler>().FirstOrDefault(h => h.Stage == message.Stage);
        if (handler is null)
        {
            logger.LogError("No handler is registered for stage {Stage}; message for document {DocumentId} dropped.", message.Stage, message.DocumentId);
            return;
        }

        var outcome = await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
        if (outcome == StageOutcome.Advanced && message.Next() is { } next)
        {
            await queue.EnqueueAsync(next, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFailureAsync(PipelineWorkItem item, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(item.TenantId);
            var recorder = scope.ServiceProvider.GetRequiredService<FailureRecorder>();

            switch (item)
            {
                case DocumentWorkItem { Message: var message }:
                    await ApplyAsync(recorder, message, FailurePolicy.Decide(exception, message.Attempt, _options), exception, cancellationToken).ConfigureAwait(false);
                    break;

                case IngestionWorkItem { Message: var message }:
                    await ApplyAsync(recorder, message, exception, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Failure of a pipeline message for tenant {TenantId} could not be recorded: shutdown.", item.TenantId);
        }
        catch (Exception recordingFailure)
        {
            logger.LogError(recordingFailure, "Failure of a pipeline message for tenant {TenantId} could not be recorded; original error: {Error}", item.TenantId, exception.Message);
        }
    }

    private async Task ApplyAsync(FailureRecorder recorder, ProcessDocument message, FailureDecision decision, Exception exception, CancellationToken cancellationToken)
    {
        switch (decision)
        {
            case FailureDecision.Retry retry:
                logger.LogWarning(exception, "Stage {Stage} of document {DocumentId} failed on attempt {Attempt}; retrying in {Delay}.", message.Stage, message.DocumentId, message.Attempt, retry.Delay);
                await recorder.RecordRetryAsync(message, retry.Reason, retry.Delay, cancellationToken).ConfigureAwait(false);
                await queue.EnqueueAsync(message.Retry(), retry.Delay, cancellationToken).ConfigureAwait(false);
                break;

            case FailureDecision.Fail fail:
                logger.LogError(exception, "Stage {Stage} of document {DocumentId} failed on attempt {Attempt}; document marked Failed.", message.Stage, message.DocumentId, message.Attempt);
                await recorder.RecordFailureAsync(message, fail.Reason, fail.FlagCode, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyAsync(FailureRecorder recorder, ProcessIngestion message, Exception exception, CancellationToken cancellationToken)
    {
        var final = message.Attempt >= _options.MaxAttempts || exception is Application.Abstractions.DocumentProcessingException or Support.StageFailedException;
        var reason = $"{exception.GetType().Name}: {exception.Message}";
        await recorder.RecordIngestionFailureAsync(message, reason, final, cancellationToken).ConfigureAwait(false);
        if (final)
        {
            logger.LogError(exception, "Ingestion {IngestionId} failed on attempt {Attempt}; marked Failed.", message.IngestionId, message.Attempt);
            return;
        }

        var delay = _options.BackoffAfter(message.Attempt);
        logger.LogWarning(exception, "Ingestion {IngestionId} failed on attempt {Attempt}; retrying in {Delay}.", message.IngestionId, message.Attempt, delay);
        var retry = message with { Attempt = message.Attempt + 1 };
        if (queue is InProcessPipelineQueue inProcess)
        {
            await inProcess.EnqueueAsync(retry, delay, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Another transport: IPipelineQueue has no delayed ingestion enqueue, so wait here and enqueue afterwards.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    await queue.EnqueueAsync(retry, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // shutdown
                }
            },
            CancellationToken.None);
    }

    private void Remember(Task work)
    {
        lock (_gate)
        {
            _running.Add(work);
        }

        _ = work.ContinueWith(
            t =>
            {
                lock (_gate)
                {
                    _running.Remove(t);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _running];
        }

        if (pending.Length == 0)
        {
            return;
        }

        using var grace = new CancellationTokenSource(_options.ShutdownGracePeriod);
        try
        {
            await Task.WhenAll(pending).WaitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("{Count} pipeline message(s) were still running after the shutdown grace period.", pending.Length);
        }
    }
}
