using System.Globalization;

namespace DocProc.Infrastructure.Persistence.Tenancy;

/// <summary>
/// T-SQL for the second tenancy layer (docs/01 "Multi-tenancy"): a security policy that filters and blocks rows
/// whose TenantId is not the one in SESSION_CONTEXT, unless the session is in platform scope. A connection with no
/// session context sees nothing, so a missed EF filter cannot leak data. Used by the migrations.
/// </summary>
public static class RowLevelSecurity
{
    public const string Schema = "rls";
    public const string PredicateFunction = "fn_tenant_predicate";
    public const string PolicyName = "tenant_policy";

    /// <summary>Tenant-scoped tables and the column the predicate is evaluated on, in migration order.</summary>
    public static readonly IReadOnlyList<(string Table, string Column)> ProtectedTables =
    [
        ("Tenants", "Id"),
        ("TenantMembers", "TenantId"),
        ("ApiKeys", "TenantId"),
        ("IngestionChannels", "TenantId"),
        ("Suppliers", "TenantId"),
        ("Ingestions", "TenantId"),
        ("IngestionItems", "TenantId"),
        ("Documents", "TenantId"),
        ("DocumentFlags", "TenantId"),
        ("DocumentEvents", "TenantId"),
        ("ReviewTasks", "TenantId"),
        ("Corrections", "TenantId"),
        ("TenantLlmPolicies", "TenantId"),
        ("LlmUsage", "TenantId"),
    ];

    /// <summary>
    /// CREATE FUNCTION and CREATE SECURITY POLICY are issued through EXEC so they run as their own batch: EF's
    /// idempotent script wraps every operation in an IF ... BEGIN/END block, where a bare CREATE FUNCTION is invalid.
    /// </summary>
    public static string CreateSql()
    {
        var predicates = string.Join(",\n", ProtectedTables.Select(t =>
            string.Format(
                CultureInfo.InvariantCulture,
                "    ADD FILTER PREDICATE [{0}].[{1}]([{3}]) ON [dbo].[{2}],\n    ADD BLOCK PREDICATE [{0}].[{1}]([{3}]) ON [dbo].[{2}] AFTER INSERT",
                Schema, PredicateFunction, t.Table, t.Column)));

        var function = $"""
            CREATE FUNCTION [{Schema}].[{PredicateFunction}](@TenantId uniqueidentifier)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN SELECT 1 AS allowed
            WHERE @TenantId = CAST(SESSION_CONTEXT(N'{TenantSessionContextInterceptor.TenantIdKey}') AS uniqueidentifier)
               OR CAST(SESSION_CONTEXT(N'{TenantSessionContextInterceptor.ScopeKey}') AS nvarchar(16)) = N'{TenantSessionContextInterceptor.PlatformScopeValue}'
            """;

        var policy = $"""
            CREATE SECURITY POLICY [{Schema}].[{PolicyName}]
            {predicates}
            WITH (STATE = ON, SCHEMABINDING = ON)
            """;

        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{Schema}')
                EXEC(N'CREATE SCHEMA [{Schema}]');
            EXEC(N'{Escape(function)}');
            EXEC(N'{Escape(policy)}');
            """;
    }

    private static string Escape(string sql) => sql.Replace("'", "''", StringComparison.Ordinal);

    public static string DropSql() => $"""
        DROP SECURITY POLICY IF EXISTS [{Schema}].[{PolicyName}];
        DROP FUNCTION IF EXISTS [{Schema}].[{PredicateFunction}];
        DROP SCHEMA IF EXISTS [{Schema}];
        """;
}
