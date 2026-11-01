using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DocProc.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Sets SESSION_CONTEXT on every opened connection so the row-level security policy (see <see cref="RowLevelSecurity"/>)
/// sees the same tenant the DbContext filters by. SQL Server clears SESSION_CONTEXT when a pooled connection is reset,
/// so a connection never carries a previous request's tenant.
/// </summary>
public sealed class TenantSessionContextInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public const string TenantIdKey = "TenantId";
    public const string ScopeKey = "TenantScope";
    public const string PlatformScopeValue = "platform";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Build(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = Build(connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private DbCommand Build(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            "EXEC sp_set_session_context @key = N'TenantId', @value = @tenantId, @read_only = 0;" +
            "EXEC sp_set_session_context @key = N'TenantScope', @value = @scope, @read_only = 0;";

        var tenantId = command.CreateParameter();
        tenantId.ParameterName = "@tenantId";
        tenantId.DbType = System.Data.DbType.Guid;
        tenantId.Value = tenant.TenantId.HasValue ? tenant.TenantId.Value : DBNull.Value;
        command.Parameters.Add(tenantId);

        var scope = command.CreateParameter();
        scope.ParameterName = "@scope";
        scope.DbType = System.Data.DbType.String;
        scope.Value = tenant.IsPlatformScope ? PlatformScopeValue : DBNull.Value;
        command.Parameters.Add(scope);
        return command;
    }
}
