using DocProc.Infrastructure.Persistence.Migrations;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace DocProc.Integration.Tests;

/// <summary>
/// The SQL text of the <c>AddItemOwnershipIndex</c> back-fill, checked without a database: it lives apart from
/// <see cref="ItemOwnershipBackfillTests"/>, whose SQL Server container it does not need, so it runs (and passes)
/// on a machine without Docker and costs CI no container start-up.
/// </summary>
public sealed class ItemOwnershipBackfillSqlTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_data_statements_open_platform_scope_and_close_it_again(bool up)
    {
        var migration = new AddItemOwnershipIndex();
        var operations = up ? migration.UpOperations : migration.DownOperations;

        var statements = operations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
        statements.Count.ShouldBe(1);
        var sql = statements[0];

        var open = sql.IndexOf($"@key = N'{TenantSessionContextInterceptor.ScopeKey}', @value = N'{TenantSessionContextInterceptor.PlatformScopeValue}'", StringComparison.Ordinal);
        var update = sql.IndexOf("UPDATE", StringComparison.Ordinal);
        var close = sql.IndexOf($"@key = N'{TenantSessionContextInterceptor.ScopeKey}', @value = NULL", StringComparison.Ordinal);
        open.ShouldBeGreaterThanOrEqualTo(0, "platform scope is opened");
        update.ShouldBeGreaterThan(open, "the UPDATE runs inside platform scope");
        close.ShouldBeGreaterThan(update, "platform scope is closed after the UPDATE");
    }
}
