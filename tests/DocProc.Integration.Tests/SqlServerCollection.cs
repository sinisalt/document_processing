using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace DocProc.Integration.Tests;

/// <summary>
/// Every test class that starts a SQL Server 2025 container belongs to this collection, so they run one after
/// another rather than side by side. A SQL Server container wants about 2 GB and several seconds of CPU to start.
/// On a 4-core runner, a handful started at once, next to the API and pipeline suites, crashed SQL Server on
/// start-up ("This program has encountered a fatal error") and failed unrelated tests.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection
{
    public const string Name = "SqlServer";
}

/// <summary>
/// One SQL Server 2025 container shared by a test class. Each test asks for its own database through
/// <see cref="NewDatabaseConnectionString"/>, so tests stay isolated without each paying for a container.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public async ValueTask InitializeAsync()
    {
        if (!DockerFacts.IsAvailable)
        {
            return;
        }

        _container = await SqlServerContainers.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// A connection string for a database that no other test uses. The database does not exist yet: EF's
    /// <c>MigrateAsync</c> creates it.
    /// </summary>
    public string NewDatabaseConnectionString() =>
        new SqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            InitialCatalog = $"test_{Guid.NewGuid():N}",
        }.ConnectionString;
}

/// <summary>Starts the SQL Server 2025 containers of the integration tests.</summary>
public static class SqlServerContainers
{
    private const int Attempts = 3;

    /// <summary>
    /// Builds and starts a SQL Server 2025 container, trying again with a new container when one exits during
    /// start-up. SQL Server on Linux occasionally dies before it accepts connections ("This program has encountered a
    /// fatal error", Reason 0x00000002, with a stack in sqlpal.dll and lsasrv.dll), before any test code has run. The
    /// next container starts normally. A container that does start but then fails a test is not retried.
    /// </summary>
    public static async Task<MsSqlContainer> StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest").Build();
            try
            {
                await container.StartAsync(cancellationToken);
                return container;
            }
            catch (ContainerNotRunningException) when (attempt < Attempts && !cancellationToken.IsCancellationRequested)
            {
                await container.DisposeAsync();
            }
        }
    }
}
