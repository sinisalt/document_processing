using DocProc.Application.Pipeline;
using DocProc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Runner;

/// <summary>
/// Publishes settlements whose publish message was lost (docs/11 §13.5). Approve, reject and auto-approval commit the
/// outbox row with the decision and only then queue the publish stage; when the process dies in between (or the
/// in-process queue is dropped at shutdown) the row stays un-handed-on. Every
/// <see cref="PipelineOptions.PublishSweepInterval"/> this service looks, across every tenant, for settled documents
/// whose current settlement has no handed-on row (<see cref="DocumentOutbox.Unpublished"/>), settled at least
/// <see cref="PipelineOptions.PublishSweepGrace"/> ago, and queues the publish stage for each with
/// <see cref="PipelineMessageOrigins.PublishSweeper"/> as the origin, which the stage records in the timeline. The stage
/// is idempotent per settlement, so a sweep that races the normal nudge publishes once. The first sweep runs one
/// interval after start, so a restarted process publishes what its predecessor lost.
/// </summary>
public sealed class PublishSweeperHostedService(
    ISystemDbContextFactory contexts,
    IPipelineQueue queue,
    TimeProvider clock,
    IOptions<PipelineOptions> options,
    ILogger<PublishSweeperHostedService> logger) : BackgroundService
{
    private readonly PipelineOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.PublishSweepInterval;
        if (interval <= TimeSpan.Zero)
        {
            logger.LogInformation("Publish sweeper is off (Pipeline:PublishSweepInterval is zero).");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, clock, stoppingToken).ConfigureAwait(false);
                try
                {
                    await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A database outage must not end the loop: the next sweep finds the same settlements.
                    logger.LogError(exception, "Publish sweep failed; retrying in {Interval}.", interval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    /// <summary>One sweep: queues the publish stage for every due settlement (up to the batch size) and returns how many.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        List<UnpublishedSettlement> due;
        await using (var db = contexts.CreatePlatformScoped())
        {
            due = await DocumentOutbox.Unpublished(db, now - _options.PublishSweepGrace, now - _options.PublishSweepLookback)
                .Take(_options.PublishSweepBatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var settlement in due)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "Document {DocumentId} of tenant {TenantId} was {Status} at {SettledAt} but is not published; queued for stage {Stage}.",
                    settlement.DocumentId,
                    settlement.TenantId,
                    settlement.Status,
                    settlement.SettledAt,
                    PipelineStage.Publish);
            }

            await queue.EnqueueAsync(new ProcessDocument(settlement.TenantId, settlement.DocumentId, PipelineStage.Publish, Origin: PipelineMessageOrigins.PublishSweeper), cancellationToken).ConfigureAwait(false);
        }

        return due.Count;
    }
}
