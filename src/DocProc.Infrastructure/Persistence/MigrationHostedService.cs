using DocProc.Infrastructure.Catalogue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// Applies pending migrations when the host starts. Intended for local development and the Azure dev environment;
/// production deployments run the idempotent script from the release pipeline instead (README "Database").
/// </summary>
public sealed class MigrationHostedService(IServiceProvider services, ILogger<MigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DocProcDbContext>();
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date.");
        }
        else
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            }

            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        // Template versions a later build ships, and tenants created by an older one (docs/11 §21 step 2).
        await using var platform = scope.ServiceProvider.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        var added = await TypeCatalogueSeeder.EnsureAllAsync(platform, TimeProvider.System.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (added > 0 && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Type catalogue: {Added} platform template or adopted type row(s) added.", added);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
