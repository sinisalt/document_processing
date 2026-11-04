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
/// database. A migration that adds a tenant-scoped table declares a new frozen list and a new frozen
/// <see cref="PolicyDefinition"/> that uses it (a temporal table's history table included), points
/// <see cref="CurrentPolicy"/> at it, and re-creates the policy with <see cref="DropSql"/> followed by
/// <see cref="CreateSql(PolicyDefinition)"/>.
/// </para>
/// <para>
/// <c>TypeCatalogue</c> (<see cref="PolicyV4"/>) adds the type catalogue and <c>LlmProviderConfigs</c> to the platform-row
/// tables. Since <c>TenantPolicyHistoryAndMoves</c> (<see cref="PolicyV3"/>) the policy also covers the history tables of the
/// temporal tables, which SQL Server does not protect with the current table's predicates (a tenant session would
/// read every tenant's history through <c>FOR SYSTEM_TIME</c> or the history table itself), and it blocks updates
/// that move a strict-table row to another tenant. History tables get a filter predicate only: the system writes
/// them, and no session can. The policy is schema-bound: a plain column ADD or DROP on a temporal table passes (SQL
/// Server carries it to the history table), but turning system versioning off is refused while the policy names the
/// table, so a migration that needs that drops the policy first and re-creates it after (as before the history
/// predicates; <c>TenantIsolationTests</c> holds both).
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

    /// <summary>
    /// The history tables of strict temporal tables (<c>TenantPolicyHistoryAndMoves</c>), filtered with the strict
    /// predicate. Frozen.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> StrictHistoryTablesV1 =
    [
        ("DocumentsHistory", "TenantId"),
    ];

    /// <summary>
    /// The history tables of temporal tables with platform rows (<c>TenantPolicyHistoryAndMoves</c>), filtered with the
    /// platform-row read predicate. Frozen.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> PlatformRowHistoryTablesV1 =
    [
        ("ExtractorProfilesHistory", "TenantId"),
    ];

    /// <summary>
    /// The nullable-tenant tables of the <c>TypeCatalogue</c> migration (docs/11 §6.10): <see cref="PlatformRowTablesV1"/>,
    /// the type catalogue (platform templates have TenantId NULL) and <c>LlmProviderConfigs</c> (platform provider
    /// configurations), which until then only its EF filter protected. Frozen.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> PlatformRowTablesV2 =
    [
        .. PlatformRowTablesV1,
        ("DocumentTypeDefinitions", "TenantId"),
        ("DocumentTypeVersions", "TenantId"),
        ("LlmProviderConfigs", "TenantId"),
    ];

    /// <summary>The platform-row history tables of the <c>TypeCatalogue</c> migration: <c>DocumentTypeVersions</c> is temporal. Frozen.</summary>
    public static readonly IReadOnlyList<(string Table, string Column)> PlatformRowHistoryTablesV2 =
    [
        .. PlatformRowHistoryTablesV1,
        ("DocumentTypeVersionsHistory", "TenantId"),
    ];

    /// <summary>The policy of the <c>P0PersistenceSeams</c> migration. Frozen.</summary>
    public static readonly PolicyDefinition PolicyV2 = new(PipelineProtectedTables, PlatformRowTablesV1, [], [], BlockStrictUpdates: false);

    /// <summary>
    /// The policy of the <c>TenantPolicyHistoryAndMoves</c> migration: <see cref="PolicyV2"/>, plus the history tables
    /// and an after-update block on every strict table. Frozen.
    /// </summary>
    public static readonly PolicyDefinition PolicyV3 = new(PipelineProtectedTables, PlatformRowTablesV1, StrictHistoryTablesV1, PlatformRowHistoryTablesV1, BlockStrictUpdates: true);

    /// <summary>
    /// The policy of the <c>TypeCatalogue</c> migration: <see cref="PolicyV3"/> plus the type catalogue tables and
    /// <c>LlmProviderConfigs</c> under the platform-row predicates, and the catalogue's history table. Frozen.
    /// </summary>
    public static readonly PolicyDefinition PolicyV4 = new(PipelineProtectedTables, PlatformRowTablesV2, StrictHistoryTablesV1, PlatformRowHistoryTablesV2, BlockStrictUpdates: true);

    /// <summary>The strict tables of the <c>FieldIndexAndInboxColumns</c> migration: <see cref="PipelineProtectedTables"/> plus <c>DocumentFieldIndex</c>. Frozen.</summary>
    public static readonly IReadOnlyList<(string Table, string Column)> FieldIndexProtectedTables =
    [
        .. PipelineProtectedTables,
        ("DocumentFieldIndex", "TenantId"),
    ];

    /// <summary>
    /// The policy of the <c>FieldIndexAndInboxColumns</c> migration: <see cref="PolicyV4"/> with <c>DocumentFieldIndex</c>
    /// (docs/11 §6.2) among the strict tables. Frozen.
    /// </summary>
    public static readonly PolicyDefinition PolicyV5 = new(FieldIndexProtectedTables, PlatformRowTablesV2, StrictHistoryTablesV1, PlatformRowHistoryTablesV2, BlockStrictUpdates: true);

    /// <summary>The strict tables of the <c>TableReports</c> migration: <see cref="FieldIndexProtectedTables"/> plus <c>DocumentTableReports</c>. Frozen.</summary>
    public static readonly IReadOnlyList<(string Table, string Column)> TableReportProtectedTables =
    [
        .. FieldIndexProtectedTables,
        ("DocumentTableReports", "TenantId"),
    ];

    /// <summary>
    /// The policy of the <c>TableReports</c> migration: <see cref="PolicyV5"/> with <c>DocumentTableReports</c> (docs/11
    /// §6.2, the table tool's report) among the strict tables. Frozen.
    /// </summary>
    public static readonly PolicyDefinition PolicyV6 = new(TableReportProtectedTables, PlatformRowTablesV2, StrictHistoryTablesV1, PlatformRowHistoryTablesV2, BlockStrictUpdates: true);

    /// <summary>
    /// The strict tables of the <c>OutputsAndLookups</c> migration: <see cref="TableReportProtectedTables"/> plus the
    /// output tables of docs/11 §6.7 with a tenant of their own (destinations, bindings, deliveries, their attempts and
    /// the external references). Frozen.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> OutputsProtectedTables =
    [
        .. TableReportProtectedTables,
        ("OutputDestinations", "TenantId"),
        ("OutputBindings", "TenantId"),
        ("OutboxDeliveries", "TenantId"),
        ("DeliveryAttempts", "TenantId"),
        ("DocumentExternalRefs", "TenantId"),
    ];

    /// <summary>
    /// The nullable-tenant tables of the <c>OutputsAndLookups</c> migration (docs/11 §6.10): <see cref="PlatformRowTablesV2"/>
    /// plus the output mappings and their versions (platform integration templates) and the lookup tables and their
    /// entries (platform code lists). Frozen.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> PlatformRowTablesV3 =
    [
        .. PlatformRowTablesV2,
        ("OutputMappings", "TenantId"),
        ("OutputMappingVersions", "TenantId"),
        ("LookupTables", "TenantId"),
        ("LookupEntries", "TenantId"),
    ];

    /// <summary>
    /// The policy of the <c>OutputsAndLookups</c> migration (P3): <see cref="PolicyV6"/> with the output tables among the
    /// strict tables and the mappings and lookup tables among the platform-row tables. None of them is temporal. Frozen.
    /// </summary>
    public static readonly PolicyDefinition PolicyV7 = new(OutputsProtectedTables, PlatformRowTablesV3, StrictHistoryTablesV1, PlatformRowHistoryTablesV2, BlockStrictUpdates: true);

    /// <summary>
    /// The strict tables of the <c>SourceRoutingAndSupplierConfig</c> migration (P4): <see cref="OutputsProtectedTables"/>
    /// plus the supplier groups, receiving entities, per-entity supplier attributes and supplier source rules of docs/11
    /// §6.4 and §6.5. Frozen.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> RoutingProtectedTables =
    [
        .. OutputsProtectedTables,
        ("SupplierGroups", "TenantId"),
        ("ReceivingEntities", "TenantId"),
        ("SupplierEntityAttributes", "TenantId"),
        ("SupplierSourceRules", "TenantId"),
    ];

    /// <summary>
    /// The policy of the <c>SourceRoutingAndSupplierConfig</c> migration (P4): <see cref="PolicyV7"/> with the routing and
    /// supplier configuration tables among the strict tables. None of them is temporal. Frozen.
    /// </summary>
    public static readonly PolicyDefinition PolicyV8 = new(RoutingProtectedTables, PlatformRowTablesV3, StrictHistoryTablesV1, PlatformRowHistoryTablesV2, BlockStrictUpdates: true);

    /// <summary>
    /// The strict tables of the <c>SupplierLayoutsAndIdentification</c> migration (P4 part B): <see cref="RoutingProtectedTables"/>
    /// plus the supplier layouts and their versions of docs/11 §6.6. Frozen.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> LayoutProtectedTables =
    [
        .. RoutingProtectedTables,
        ("SupplierLayouts", "TenantId"),
        ("SupplierLayoutVersions", "TenantId"),
    ];

    /// <summary>
    /// The policy of the <c>SupplierLayoutsAndIdentification</c> migration (P4 part B): <see cref="PolicyV8"/> with the layout
    /// tables among the strict tables. Neither is temporal. Frozen.
    /// </summary>
    public static readonly PolicyDefinition PolicyV9 = new(LayoutProtectedTables, PlatformRowTablesV3, StrictHistoryTablesV1, PlatformRowHistoryTablesV2, BlockStrictUpdates: true);

    /// <summary>The policy the latest migration creates.</summary>
    public static PolicyDefinition CurrentPolicy => PolicyV9;

    /// <summary>Every tenant-scoped table with a non-null tenant column: the strict set the current policy covers.</summary>
    public static IReadOnlyList<(string Table, string Column)> ProtectedTables => CurrentPolicy.StrictTables;

    /// <summary>Every table with platform rows (nullable TenantId) the current policy covers.</summary>
    public static IReadOnlyList<(string Table, string Column)> PlatformRowTables => CurrentPolicy.PlatformRowTables;

    /// <summary>The policy exactly as the <c>InitialCreate</c> migration creates it (<see cref="InitialProtectedTables"/>).</summary>
    public static string CreateSql() => CreateSql(InitialProtectedTables);

    /// <summary>A policy over strict tables only, as the migrations before the platform-row predicate create it.</summary>
    public static string CreateSql(IReadOnlyList<(string Table, string Column)> tables) => CreateSql(tables, []);

    /// <summary>
    /// The policy over <paramref name="tables"/> (strict filter and insert block) and <paramref name="platformRowTables"/>
    /// (platform rows readable by everyone, writable only in platform scope), as the <c>P0PersistenceSeams</c> migration
    /// creates it.
    /// </summary>
    public static string CreateSql(IReadOnlyList<(string Table, string Column)> tables, IReadOnlyList<(string Table, string Column)> platformRowTables) =>
        CreateSql(new PolicyDefinition(tables, platformRowTables, [], [], BlockStrictUpdates: false));

    /// <summary>
    /// The policy <paramref name="policy"/> describes: strict tables get the strict filter and insert block (and an
    /// update block when <see cref="PolicyDefinition.BlockStrictUpdates"/>), platform-row tables the read filter and
    /// the strict blocks, history tables a filter only. CREATE FUNCTION and CREATE SECURITY POLICY are issued through
    /// EXEC so they run as their own batch: EF's idempotent script wraps every operation in an IF ... BEGIN/END block,
    /// where a bare CREATE FUNCTION is invalid. The read function is created only when a platform-row table is listed,
    /// so the SQL of the earlier migrations is unchanged.
    /// </summary>
    public static string CreateSql(PolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var tables = policy.StrictTables;
        var platformRowTables = policy.PlatformRowTables;
        var strictFormat = policy.BlockStrictUpdates
            ? "    ADD FILTER PREDICATE [{0}].[{1}]([{3}]) ON [dbo].[{2}],\n    ADD BLOCK PREDICATE [{0}].[{1}]([{3}]) ON [dbo].[{2}] AFTER INSERT,\n    ADD BLOCK PREDICATE [{0}].[{1}]([{3}]) ON [dbo].[{2}] AFTER UPDATE"
            : "    ADD FILTER PREDICATE [{0}].[{1}]([{3}]) ON [dbo].[{2}],\n    ADD BLOCK PREDICATE [{0}].[{1}]([{3}]) ON [dbo].[{2}] AFTER INSERT";
        var strict = tables.Select(t => string.Format(CultureInfo.InvariantCulture, strictFormat, Schema, PredicateFunction, t.Table, t.Column));
        var platform = platformRowTables.Select(t =>
            string.Format(
                CultureInfo.InvariantCulture,
                "    ADD FILTER PREDICATE [{0}].[{1}]([{4}]) ON [dbo].[{3}],\n"
                + "    ADD BLOCK PREDICATE [{0}].[{2}]([{4}]) ON [dbo].[{3}] AFTER INSERT,\n"
                + "    ADD BLOCK PREDICATE [{0}].[{2}]([{4}]) ON [dbo].[{3}] AFTER UPDATE,\n"
                + "    ADD BLOCK PREDICATE [{0}].[{2}]([{4}]) ON [dbo].[{3}] BEFORE UPDATE,\n"
                + "    ADD BLOCK PREDICATE [{0}].[{2}]([{4}]) ON [dbo].[{3}] BEFORE DELETE",
                Schema, PlatformReadFunction, PredicateFunction, t.Table, t.Column));
        var history = policy.StrictHistoryTables.Select(t => (Function: PredicateFunction, t.Table, t.Column))
            .Concat(policy.PlatformRowHistoryTables.Select(t => (Function: PlatformReadFunction, t.Table, t.Column)))
            .Select(t => string.Format(CultureInfo.InvariantCulture, "    ADD FILTER PREDICATE [{0}].[{1}]([{3}]) ON [dbo].[{2}]", Schema, t.Function, t.Table, t.Column));
        var predicates = string.Join(",\n", strict.Concat(platform).Concat(history));

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

        var create = $"""
            CREATE SECURITY POLICY [{Schema}].[{PolicyName}]
            {predicates}
            WITH (STATE = ON, SCHEMABINDING = ON)
            """;

        var createRead = platformRowTables.Count == 0 && policy.PlatformRowHistoryTables.Count == 0 ? string.Empty : $"EXEC(N'{Escape(readFunction)}');\n";
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{Schema}')
                EXEC(N'CREATE SCHEMA [{Schema}]');
            EXEC(N'{Escape(function)}');
            {createRead}EXEC(N'{Escape(create)}');
            """;
    }

    /// <summary>
    /// Runs a data statement of a migration with the predicates open (platform scope), then closes them again. Neither
    /// migration path (the release script run with sqlcmd, <c>MigrationHostedService</c> with an anonymous tenant
    /// context) sets a tenant, so a backfill over a protected table would otherwise see no rows.
    /// <para>
    /// The statement runs through EXEC, so SQL Server compiles it only when it runs. EF's idempotent script puts a whole
    /// migration into one batch, and SQL Server compiles a batch before running any of it: a statement that names a
    /// column an earlier ALTER TABLE of the same migration adds fails with error 207 (invalid column name), and so does
    /// one whose columns a migration that already ran has renamed or dropped, although its IF skips it.
    /// </para>
    /// </summary>
    public static string InPlatformScope(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return $"""
            EXEC sp_set_session_context @key = N'{TenantSessionContextInterceptor.ScopeKey}', @value = N'{TenantSessionContextInterceptor.PlatformScopeValue}', @read_only = 0;
            EXEC(N'{Escape(statement)}');
            EXEC sp_set_session_context @key = N'{TenantSessionContextInterceptor.ScopeKey}', @value = NULL, @read_only = 0;
            """;
    }

    private static string Escape(string sql) => sql.Replace("'", "''", StringComparison.Ordinal);

    public static string DropSql() => $"""
        DROP SECURITY POLICY IF EXISTS [{Schema}].[{PolicyName}];
        DROP FUNCTION IF EXISTS [{Schema}].[{PredicateFunction}];
        DROP FUNCTION IF EXISTS [{Schema}].[{PlatformReadFunction}];
        DROP SCHEMA IF EXISTS [{Schema}];
        """;
}

/// <summary>
/// What one version of the tenant policy covers (<see cref="RowLevelSecurity.CreateSql(PolicyDefinition)"/>). Every
/// migration that re-creates the policy names a frozen definition of its own.
/// </summary>
/// <param name="StrictTables">Tables with a non-null tenant column: strict filter, insert block.</param>
/// <param name="PlatformRowTables">Tables with platform rows (nullable tenant): read filter, strict blocks on every write.</param>
/// <param name="StrictHistoryTables">History tables of strict temporal tables: strict filter.</param>
/// <param name="PlatformRowHistoryTables">History tables of platform-row temporal tables: read filter.</param>
/// <param name="BlockStrictUpdates">Whether strict tables also get an after-update block, so a row cannot be moved to another tenant.</param>
public sealed record PolicyDefinition(
    IReadOnlyList<(string Table, string Column)> StrictTables,
    IReadOnlyList<(string Table, string Column)> PlatformRowTables,
    IReadOnlyList<(string Table, string Column)> StrictHistoryTables,
    IReadOnlyList<(string Table, string Column)> PlatformRowHistoryTables,
    bool BlockStrictUpdates);
