using DocProc.Application.Pipeline;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Runner;

/// <summary>
/// The <c>DeliverySweeper</c> of docs/11 §13.6: queues deliveries whose message was lost. The fan-out commits its
/// deliveries with the publish and only then queues them, and a retry waits in the in-process queue; a process that
/// ends in between leaves the row due. Every <see cref="DeliveryOptions.SweepInterval"/> this service looks, across
/// every tenant, by <c>(Status, NextAttemptAt)</c>, for deliveries <c>Pending</c> or <c>Failed</c> and due at least
/// <see cref="DeliveryOptions.SweepGrace"/> ago, and <c>Sending</c> ones whose lease ran out (a process died
/// mid-send), and queues the attempt each expects. Each row is first claimed for the sweep: its <c>NextAttemptAt</c>
/// moves on by the grace under its <c>Version</c> token (an optimistic check, on SQL Server and SQLite alike), so the
/// sweepers of the API and the worker, which both run one in compose, queue it once, and a queued copy that is not
/// needed any more is dropped by the processor's attempt check.
/// </summary>
public sealed class DeliverySweeperHostedService(
    IServiceProvider services,
    IPipelineQueue queue,
    TimeProvider clock,
    IOptions<DeliveryOptions> options,
    ILogger<DeliverySweeperHostedService> logger) : BackgroundService
{
    private readonly DeliveryOptions _options = options.Value;

    // Looked up, not injected: a host started without a database (the OpenAPI export) still starts, without sweeping.
    private ISystemDbContextFactory? Contexts => services.GetService<ISystemDbContextFactory>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.SweepInterval;
        if (interval <= TimeSpan.Zero)
        {
            logger.LogInformation("Delivery sweeper is off (Delivery:SweepInterval is zero).");
            return;
        }

        if (Contexts is null)
        {
            logger.LogWarning("Delivery sweeper is off: this host has no database.");
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
                    logger.LogError(exception, "Delivery sweep failed; retrying in {Interval}.", interval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    /// <summary>One sweep: queues every due delivery it claims (up to the batch size) and returns how many.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var contexts = Contexts ?? throw new InvalidOperationException("The delivery sweeper needs persistence (ISystemDbContextFactory), and this host has none.");
        var now = clock.GetUtcNow();
        var dueBefore = now - _options.SweepGrace;
        var queued = 0;
        await using var db = contexts.CreatePlatformScoped();
        var due = await db.OutboxDeliveries.AsNoTracking()
            .Where(d => ((d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed) && d.NextAttemptAt != null && d.NextAttemptAt <= dueBefore)
                        || (d.Status == DeliveryStatus.Sending && d.NextAttemptAt != null && d.NextAttemptAt <= now))
            .OrderBy(d => d.NextAttemptAt)
            .Take(_options.SweepBatchSize)
            .Select(d => new { d.Id, d.TenantId, d.DestinationId, d.DocumentId, d.Attempts, d.Version, d.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var until = now + _options.SweepGrace;
        foreach (var row in due)
        {
            // A Sending row whose lease ran out was cut off mid-attempt: it becomes Failed, which the attempt it now
            // expects (Attempts + 1) picks up like any retry.
            var rows = db.OutboxDeliveries.Where(d => d.Id == row.Id && d.Version == row.Version);
            var claimed = row.Status == DeliveryStatus.Sending
                ? await rows.ExecuteUpdateAsync(
                    u => u.SetProperty(d => d.NextAttemptAt, until)
                        .SetProperty(d => d.Status, DeliveryStatus.Failed)
                        .SetProperty(d => d.LastError, "The attempt did not finish (its process ended or it ran past its lease); it is retried.")
                        .SetProperty(d => d.Version, d => d.Version + 1),
                    cancellationToken).ConfigureAwait(false)
                : await rows.ExecuteUpdateAsync(u => u.SetProperty(d => d.NextAttemptAt, until).SetProperty(d => d.Version, d => d.Version + 1), cancellationToken).ConfigureAwait(false);
            if (claimed == 0)
            {
                continue;
            }

            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning("Delivery {DeliveryId} of tenant {TenantId} was {Status} and due, with no message; queued for attempt {Attempt}.", row.Id, row.TenantId, row.Status, row.Attempts + 1);
            }

            await queue.EnqueueAsync(new ProcessDelivery(row.TenantId, row.Id, row.DestinationId, row.DocumentId, row.Attempts + 1), TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            queued++;
        }

        return queued;
    }
}
