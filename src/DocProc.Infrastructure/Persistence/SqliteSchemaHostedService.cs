using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// Creates the SQLite schema from the model when the host starts. The migrations are SQL Server-only (row-level
/// security, temporal tables), so the development mode uses <c>EnsureCreated</c> instead; after a model change the
/// file is deleted and recreated (docs/09 "Running without Docker").
/// </summary>
public sealed class SqliteSchemaHostedService(ISystemDbContextFactory contexts, SqliteDatabaseLocation location, ILogger<SqliteSchemaHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = contexts.CreatePlatformScoped();
        var created = await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("SQLite database {Path} {State}.", location.Path, created ? "created from the model" : "already exists");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The absolute path of the SQLite file; registered so logs and diagnostics can name it.</summary>
public sealed record SqliteDatabaseLocation(string Path)
{
    public string ConnectionString => $"Data Source={Path}";
}
