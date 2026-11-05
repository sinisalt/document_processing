using DocProc.Infrastructure.Outputs.Acknowledgements;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Runner;

/// <summary>
/// Runs <see cref="ExternalStatusPoller"/> every <c>Delivery:StatusPollInterval</c> (docs/11 §13.8: "run by the delivery
/// worker while the external ref is Pending"). Off when the interval is zero or the host has no database.
/// </summary>
public sealed class StatusPollerHostedService(
    IServiceProvider services,
    TimeProvider clock,
    IOptions<DeliveryOptions> options,
    ILogger<StatusPollerHostedService> logger) : BackgroundService
{
    /// <summary>The poller, or null in a host without persistence (the OpenAPI export).</summary>
    public ExternalStatusPoller? Poller => services.GetService<ISystemDbContextFactory>() is null ? null : services.GetService<ExternalStatusPoller>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.StatusPollInterval;
        if (interval <= TimeSpan.Zero || Poller is not { } poller)
        {
            logger.LogInformation("Status poller is off (Delivery:StatusPollInterval is zero, or the host has no database).");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, clock, stoppingToken).ConfigureAwait(false);
                try
                {
                    await poller.PollDueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Status poll failed; retrying in {Interval}.", interval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }
}
