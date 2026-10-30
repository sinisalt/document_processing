using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocProc.Infrastructure.Persistence;

/// <summary>Runs <see cref="DevSeed"/> at start-up; registered after the schema or migration service so it starts after the schema exists.</summary>
public sealed class DevSeedHostedService(ISystemDbContextFactory contexts, TimeProvider clock, ILogger<DevSeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = contexts.CreatePlatformScoped();
        var inserted = await DevSeed.ApplyAsync(db, clock, cancellationToken).ConfigureAwait(false);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Development seed applied: {Inserted} row(s) inserted; the rest already existed.", inserted);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
