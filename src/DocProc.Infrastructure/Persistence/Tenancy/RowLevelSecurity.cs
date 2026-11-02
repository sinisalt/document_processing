using System.Globalization;

namespace DocProc.Infrastructure.Persistence.Tenancy;

/// <summary>
/// T-SQL for the second tenancy layer (docs/01 "Multi-tenancy"): a security policy that filters and blocks rows
/// whose TenantId is not the one in SESSION_CONTEXT, unless the session is in platform scope. A connection with no
/// session context sees nothing, so a missed EF filter cannot leak data. Used by the migrations.
/// <para>
/// Tables with a nullable TenantId hold platform rows (TenantId NULL) next to tenant rows (docs/11 §6.10). They get a
/// second, read-only predicate as their filter, <see cref="PlatformReadFunction"/>, which also lets every session read
/// the platform rows, and the strict predicate as their block predicates on insert, update and delete, so a tenant
/// session can never create, change or delete a platform row.
/// </para>
/// <para>
/// Every migration builds its policy from a frozen table list of its own (<see cref="InitialProtectedTables"/>,
/// <see cref="PipelineProtectedTables"/>, <see cref="PlatformRowTablesV1"/>): the SQL is generated when the migration
/// runs, so a list that later grew would make an old migration reference a table that does not exist yet on a fresh
/// database. A migration that adds a tenant-scoped table declares a new frozen list, points
/// <see cref="ProtectedTables"/> or <see cref="PlatformRowTables"/> at it, and re-creates the policy with
/// <see cref="DropSql"/> followed by <see cref="CreateSql(IReadOnlyList{ValueTuple{string, string}}, IReadOnlyList{ValueTuple{string, string}})"/>.
/// </para>
/// </summary>
public static class RowLevelSecurity
{
    public const string Schema = "rls";
    public const string PredicateFunction = "fn_tenant_predicate";
    public const string PlatformReadFunction = "fn_tenant_or_platform_read";
    public const string PolicyName = "tenant_policy";

    /// <summary>
    /// The tables the <c>InitialCreate</c> migration protected, in migration order. Frozen: that migration builds its
    /// SQL from this list when it runs, so a table added here would be referenced before it exists on a fresh database.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> InitialProtectedTables =
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

    /// <summary>The strict tables of the <c>AddPipelineAndApiTables</c> migration. Frozen, like <see cref="InitialProtectedTables"/>.</summary>
    public static readonly IReadOnlyList<(string Table, string Column)> PipelineProtectedTables =
    [
        .. InitialProtectedTables,
        ("OutboxMessages", "TenantId"),
        ("IdempotencyKeys", "TenantId"),
    ];

    /// <summary>
    /// The nullable-tenant tables of the <c>P0PersistenceSeams</c> migration (docs/11 §6.10). Frozen. The type
    /// catalogue, lookup and output mapping tables of docs/11 join a later list with the migrations that create them.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> PlatformRowTablesV1 =
    [
        ("ExtractorProfiles", "TenantId"),
    ];

    /// <summary>Every tenant-scoped table with a non-null tenant column: the strict set the current policy covers.</summary>
    public static IReadOnlyList<(string Table, string Column)> ProtectedTables => PipelineProtectedTables;

    /// <summary>Every table with platform rows (nullable TenantId) the current policy covers.</summary>
    public static IReadOnlyList<(string Table, string Column)> PlatformRowTables => PlatformRowTablesV1;

    /// <summary>The policy exactly as the <c>InitialCreate</c> migration creates it (<see cref="InitialProtectedTables"/>).</summary>
    public static string CreateSql() => CreateSql(InitialProtectedTables);

    /// <summary>A policy over strict tables only, as the migrations before the platform-row predicate create it.</summary>
    public static string CreateSql(IReadOnlyList<(string Table, string Column)> tables) => CreateSql(tables, []);

    /// <summary>
    /// The policy over <paramref name="tables"/> (strict filter and insert block) and <paramref name="platformRowTables"/>
    /// (platform rows readable by everyone, writable only in platform scope). CREATE FUNCTION and CREATE SECURITY
    /// POLICY are issued through EXEC so they run as their own batch: EF's idempotent script wraps every operation in
    /// an IF ... BEGIN/END block, where a bare CREATE FUNCTION is invalid. The read function is created only when a
    /// platform-row table is listed, so the SQL of the earlier migrations is unchanged.
    /// </summary>
    public static string CreateSql(IReadOnlyList<(string Table, string Column)> tables, IReadOnlyList<(string Table, string Column)> platformRowTables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(platformRowTables);
        var strict = tables.Select(t =>
            string.Format(
                CultureInfo.InvariantCulture,
                "    ADD FILTER PREDICATE [{0}].[{1}]([{3}]) ON [dbo].[{2}],\n    ADD BLOCK PREDICATE [{0}].[{1}]([{3}]) ON [dbo].[{2}] AFTER INSERT",
                Schema, PredicateFunction, t.Table, t.Column));
        var platform = platformRowTables.Select(t =>
            string.Format(
                CultureInfo.InvariantCulture,
                "    ADD FILTER PREDICATE [{0}].[{1}]([{4}]) ON [dbo].[{3}],\n"
                + "    ADD BLOCK PREDICATE [{0}].[{2}]([{4}]) ON [dbo].[{3}] AFTER INSERT,\n"
                + "    ADD BLOCK PREDICATE [{0}].[{2}]([{4}]) ON [dbo].[{3}] AFTER UPDATE,\n"
                + "    ADD BLOCK PREDICATE [{0}].[{2}]([{4}]) ON [dbo].[{3}] BEFORE UPDATE,\n"
                + "    ADD BLOCK PREDICATE [{0}].[{2}]([{4}]) ON [dbo].[{3}] BEFORE DELETE",
                Schema, PlatformReadFunction, PredicateFunction, t.Table, t.Column));
        var predicates = string.Join(",\n", strict.Concat(platform));

        var function = $"""
            CREATE FUNCTION [{Schema}].[{PredicateFunction}](@TenantId uniqueidentifier)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN SELECT 1 AS allowed
            WHERE @TenantId = CAST(SESSION_CONTEXT(N'{TenantSessionContextInterceptor.TenantIdKey}') AS uniqueidentifier)
               OR CAST(SESSION_CONTEXT(N'{TenantSessionContextInterceptor.ScopeKey}') AS nvarchar(16)) = N'{TenantSessionContextInterceptor.PlatformScopeValue}'
            """;

        var readFunction = $"""
            CREATE FUNCTION [{Schema}].[{PlatformReadFunction}](@TenantId uniqueidentifier)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN SELECT 1 AS allowed
            WHERE @TenantId IS NULL
               OR @TenantId = CAST(SESSION_CONTEXT(N'{TenantSessionContextInterceptor.TenantIdKey}') AS uniqueidentifier)
               OR CAST(SESSION_CONTEXT(N'{TenantSessionContextInterceptor.ScopeKey}') AS nvarchar(16)) = N'{TenantSessionContextInterceptor.PlatformScopeValue}'
            """;

        var policy = $"""
            CREATE SECURITY POLICY [{Schema}].[{PolicyName}]
            {predicates}
            WITH (STATE = ON, SCHEMABINDING = ON)
            """;

        var createRead = platformRowTables.Count == 0 ? string.Empty : $"EXEC(N'{Escape(readFunction)}');\n";
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{Schema}')
                EXEC(N'CREATE SCHEMA [{Schema}]');
            EXEC(N'{Escape(function)}');
            {createRead}EXEC(N'{Escape(policy)}');
            """;
    }

    /// <summary>
    /// Runs a data statement of a migration with the predicates open (platform scope), then closes them again. Neither
    /// migration path (the release script run with sqlcmd, <c>MigrationHostedService</c> with an anonymous tenant
    /// context) sets a tenant, so a backfill over a protected table would otherwise see no rows.
    /// </summary>
    public static string InPlatformScope(string statement) =>
        $"""
        EXEC sp_set_session_context @key = N'{TenantSessionContextInterceptor.ScopeKey}', @value = N'{TenantSessionContextInterceptor.PlatformScopeValue}', @read_only = 0;
        {statement}
        EXEC sp_set_session_context @key = N'{TenantSessionContextInterceptor.ScopeKey}', @value = NULL, @read_only = 0;
        """;

    private static string Escape(string sql) => sql.Replace("'", "''", StringComparison.Ordinal);

    public static string DropSql() => $"""
        DROP SECURITY POLICY IF EXISTS [{Schema}].[{PolicyName}];
        DROP FUNCTION IF EXISTS [{Schema}].[{PredicateFunction}];
        DROP FUNCTION IF EXISTS [{Schema}].[{PlatformReadFunction}];
        DROP SCHEMA IF EXISTS [{Schema}];
        """;
}
