using DocProc.Infrastructure.MasterData;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Runner;

/// <summary>
/// The scheduled master-data sync (docs/11 §14 <c>MasterDataSyncHostedService</c>): every <c>Pipeline:MasterDataSyncInterval</c>
/// it runs the enabled sources whose schedule is due, one at a time, each claimed first by moving its <c>NextRunAt</c>
/// on (so two hosts never run one twice). A source that fails is recorded as a failed run and tried at its next time.
/// </summary>
public sealed class MasterDataSyncHostedService(
    IServiceProvider services,
    TimeProvider clock,
    IOptions<PipelineOptions> options,
    ILogger<MasterDataSyncHostedService> logger) : BackgroundService
{
    public const string Actor = "schedule";

    private ISystemDbContextFactory? Contexts => services.GetService<ISystemDbContextFactory>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.MasterDataSyncInterval;
        if (interval <= TimeSpan.Zero || Contexts is null)
        {
            logger.LogInformation("Master-data sync is off (Pipeline:MasterDataSyncInterval is zero, or the host has no database).");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, clock, stoppingToken).ConfigureAwait(false);
                try
                {
                    await RunDueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Master-data sync failed; retrying in {Interval}.", interval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    /// <summary>Runs the due sources; returns how many ran.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var contexts = Contexts ?? throw new InvalidOperationException("The master-data sync needs persistence.");
        var sync = services.GetRequiredService<MasterDataSync>();
        var now = clock.GetUtcNow();
        List<(Guid Id, int Version)> due;
        await using (var db = contexts.CreatePlatformScoped())
        {
            due = (await db.MasterDataSources.AsNoTracking()
                    .Where(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= now)
                    .OrderBy(s => s.NextRunAt)
                    .Take(20)
                    .Select(s => new { s.Id, s.Version })
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(s => (s.Id, s.Version)).ToList();
        }

        var ran = 0;
        foreach (var (id, version) in due)
        {
            await using var db = contexts.CreatePlatformScoped();
            var source = await db.MasterDataSources.SingleOrDefaultAsync(s => s.Id == id && s.Version == version, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                continue; // changed or claimed by another host
            }

            // Claim: the next run time moves on in the save of this run; a second host's save fails on the version.
            source.NextRunAt = MasterDataSync.NextRun(source, now);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                continue;
            }

            var run = await sync.RunAsync(db, source, MasterDataTrigger.Schedule, dryRun: false, uploaded: null, Actor, cancellationToken).ConfigureAwait(false);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Master-data source {Source} of tenant {TenantId} ran: {Status}, {Created} created, {Updated} updated, {Conflicts} conflicts.", source.Name, source.TenantId, run.Status, run.Created, run.Updated, run.Conflicts);
            }

            ran++;
        }

        return ran;
    }
}
