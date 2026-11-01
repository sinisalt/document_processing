using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace DocProc.Integration.Tests;

/// <summary>Smoke test for the database the rest of the integration suite will build on: SQL Server 2025 in a container.</summary>
public sealed class SqlServerContainerTests : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public async ValueTask InitializeAsync()
    {
        if (!DockerFacts.IsAvailable)
        {
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest").Build();
        await _container.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Sql_server_2025_container_answers_and_supports_the_json_type()
    {
        DockerFacts.SkipUnlessAvailable();

        await using var connection = new SqlConnection(_container!.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var version = new SqlCommand("SELECT SERVERPROPERTY('ProductMajorVersion')", connection);
        var major = Convert.ToInt32(await version.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        major.ShouldBeGreaterThanOrEqualTo(17);

        // The canonical document and profile rules are stored in native json columns (docs/01 "Data").
        await using var json = new SqlCommand("DECLARE @d json = N'{\"invoiceNumber\":\"RE-1\"}'; SELECT JSON_VALUE(@d, '$.invoiceNumber')", connection);
        (await json.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("RE-1");
    }
}
