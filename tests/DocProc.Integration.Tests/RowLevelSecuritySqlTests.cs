using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Migrations;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace DocProc.Integration.Tests;

/// <summary>
/// The SQL of the platform-row predicate (docs/11 §6.10), checked without a database; <see cref="TenantIsolationTests"/>
/// proves its behaviour on SQL Server.
/// </summary>
public sealed class RowLevelSecuritySqlTests
{
    /// <summary>
    /// Nullable-tenant tables that are knowingly not under the platform-row predicate yet. docs/11 §6.10 names
    /// ExtractorProfiles as the gap it closes; LlmProviderConfigs has the same shape (platform configurations with a
    /// NULL TenantId) and is still protected by its EF query filter alone.
    /// </summary>
    private static readonly string[] KnownUnprotected = [];

    [Fact]
    public void Platform_row_tables_get_the_read_filter_and_the_strict_block_predicates()
    {
        var sql = RowLevelSecurity.CreateSql(RowLevelSecurity.PipelineProtectedTables, RowLevelSecurity.PlatformRowTablesV1);

        sql.ShouldContain($"CREATE FUNCTION [rls].[{RowLevelSecurity.PlatformReadFunction}]");
        sql.ShouldContain("WHERE @TenantId IS NULL");
        sql.ShouldContain($"ADD FILTER PREDICATE [rls].[{RowLevelSecurity.PlatformReadFunction}]([TenantId]) ON [dbo].[ExtractorProfiles]");
        foreach (var operation in new[] { "AFTER INSERT", "AFTER UPDATE", "BEFORE UPDATE", "BEFORE DELETE" })
        {
            sql.ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[ExtractorProfiles] {operation}");
        }

        // The strict tables are filtered and blocked exactly as before.
        sql.ShouldContain($"ADD FILTER PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[Documents]");
        sql.ShouldNotContain($"[{RowLevelSecurity.PlatformReadFunction}]([TenantId]) ON [dbo].[Documents]");
    }

    [Fact]
    public void The_strict_only_policy_of_the_earlier_migrations_is_unchanged()
    {
        var sql = RowLevelSecurity.CreateSql(RowLevelSecurity.PipelineProtectedTables);

        sql.ShouldNotContain(RowLevelSecurity.PlatformReadFunction);
        sql.ShouldNotContain("ExtractorProfiles");
        sql.ShouldNotContain("BEFORE DELETE");
        RowLevelSecurity.PipelineProtectedTables.Count.ShouldBe(16);
        RowLevelSecurity.PlatformRowTablesV1.ShouldBe([("ExtractorProfiles", "TenantId")]);
    }

    [Fact]
    public void Drop_removes_both_functions_after_the_policy()
    {
        var sql = RowLevelSecurity.DropSql();

        var policy = sql.IndexOf("DROP SECURITY POLICY", StringComparison.Ordinal);
        sql.IndexOf($"DROP FUNCTION IF EXISTS [rls].[{RowLevelSecurity.PredicateFunction}]", StringComparison.Ordinal).ShouldBeGreaterThan(policy);
        sql.IndexOf($"DROP FUNCTION IF EXISTS [rls].[{RowLevelSecurity.PlatformReadFunction}]", StringComparison.Ordinal).ShouldBeGreaterThan(policy);
    }

    [Fact]
    public void Every_nullable_tenant_table_is_under_the_platform_row_predicate_or_a_known_gap()
    {
        using var context = NewContext();
        var nullableTenantTables = context.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.FindProperty("TenantId") is { IsNullable: true })
            .Select(e => e.GetTableName()!)
            .ToHashSet(StringComparer.Ordinal);

        var covered = RowLevelSecurity.PlatformRowTables.Select(t => t.Table).Concat(KnownUnprotected).ToHashSet(StringComparer.Ordinal);

        covered.ShouldBe(nullableTenantTables, ignoreOrder: true);
        RowLevelSecurity.PlatformRowTables.Select(t => t.Table).ShouldNotContain(t => RowLevelSecurity.ProtectedTables.Any(p => p.Table == t));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_seams_migration_re_creates_the_policy_and_back_fills_in_platform_scope(bool up)
    {
        var migration = new P0PersistenceSeams();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();

        statements.ShouldContain(RowLevelSecurity.DropSql());
        if (up)
        {
            statements.ShouldContain(RowLevelSecurity.CreateSql(RowLevelSecurity.PipelineProtectedTables, RowLevelSecurity.PlatformRowTablesV1));
            var backfill = statements.Single(s => s.Contains("[SubmittedBy]", StringComparison.Ordinal));
            var open = backfill.IndexOf($"@value = N'{TenantSessionContextInterceptor.PlatformScopeValue}'", StringComparison.Ordinal);
            open.ShouldBeGreaterThanOrEqualTo(0);
            backfill.IndexOf("UPDATE", StringComparison.Ordinal).ShouldBeGreaterThan(open);
            // Through EXEC, so the escaped literals.
            backfill.ShouldContain("N''ApiUpload'', N''PortalUpload''");
        }
        else
        {
            statements.ShouldContain(RowLevelSecurity.CreateSql(RowLevelSecurity.PipelineProtectedTables));
        }
    }

    [Fact]
    public void The_seams_migration_gives_every_tenant_the_flags_object_in_platform_scope()
    {
        var statements = new P0PersistenceSeams().UpOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();

        var backfill = statements.Single(s => s.Contains("$.Features", StringComparison.Ordinal));
        backfill.ShouldContain("UPDATE [Tenants]");
        backfill.IndexOf($"@value = N'{TenantSessionContextInterceptor.PlatformScopeValue}'", StringComparison.Ordinal).ShouldBeLessThan(backfill.IndexOf("UPDATE", StringComparison.Ordinal));

        // The frozen literal names real flags, all off.
        var flags = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(P0PersistenceSeams.AllFlagsOff)!;
        flags.Values.ShouldAllBe(on => !on);
        flags.Keys.ShouldBe(typeof(TenantFeatureFlags).GetProperties().Select(p => p.Name), ignoreOrder: true);
    }

    [Fact]
    public void A_platform_scope_statement_runs_through_exec_between_opening_and_closing_the_scope()
    {
        var sql = RowLevelSecurity.InPlatformScope("UPDATE [T] SET [C] = N'x' WHERE [D] IS NULL;");

        var open = sql.IndexOf($"@value = N'{TenantSessionContextInterceptor.PlatformScopeValue}'", StringComparison.Ordinal);
        var exec = sql.IndexOf("EXEC(N'UPDATE [T] SET [C] = N''x'' WHERE [D] IS NULL;');", StringComparison.Ordinal);
        var close = sql.IndexOf("@value = NULL", StringComparison.Ordinal);
        open.ShouldBeGreaterThanOrEqualTo(0);
        exec.ShouldBeGreaterThan(open, "compiled only when it runs, after the ALTER TABLE before it");
        close.ShouldBeGreaterThan(exec);
    }

    [Fact]
    public void Every_data_statement_of_a_migration_compiles_only_when_it_runs()
    {
        // EF's idempotent script puts a whole migration into one batch, which SQL Server compiles before running any of
        // it: a bare UPDATE over a column the same migration adds fails with error 207. Data statements go through EXEC.
        var migrations = typeof(DocProcDbContext).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Microsoft.EntityFrameworkCore.Migrations.Migration)) && !t.IsAbstract)
            .Select(t => (Microsoft.EntityFrameworkCore.Migrations.Migration)Activator.CreateInstance(t)!)
            .ToList();
        migrations.Count.ShouldBeGreaterThan(5);
        foreach (var migration in migrations)
        {
            foreach (var sql in migration.UpOperations.Concat(migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql))
            {
                var bare = sql.Split('\n').Select(l => l.TrimStart()).Where(l => l.StartsWith("UPDATE ", StringComparison.OrdinalIgnoreCase) || l.StartsWith("INSERT ", StringComparison.OrdinalIgnoreCase) || l.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase));
                bare.ShouldBeEmpty($"{migration.GetType().Name}: a data statement outside EXEC");
            }
        }
    }

    [Fact]
    public void The_history_policy_filters_the_history_tables_and_blocks_tenant_moves()
    {
        var sql = RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV3);

        sql.ShouldContain($"ADD FILTER PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[DocumentsHistory]");
        sql.ShouldContain($"ADD FILTER PREDICATE [rls].[{RowLevelSecurity.PlatformReadFunction}]([TenantId]) ON [dbo].[ExtractorProfilesHistory]");
        sql.ShouldNotContain("ON [dbo].[DocumentsHistory] AFTER", customMessage: "the system writes history; no block predicate");
        sql.ShouldNotContain("ON [dbo].[ExtractorProfilesHistory] AFTER");
        foreach (var (table, column) in RowLevelSecurity.PipelineProtectedTables)
        {
            sql.ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([{column}]) ON [dbo].[{table}] AFTER INSERT");
            sql.ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([{column}]) ON [dbo].[{table}] AFTER UPDATE");
        }

        // The platform-row tables keep exactly the P0 predicates.
        foreach (var operation in new[] { "AFTER INSERT", "AFTER UPDATE", "BEFORE UPDATE", "BEFORE DELETE" })
        {
            sql.ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[ExtractorProfiles] {operation}");
        }

        // The P0 policy is unchanged by the new definition.
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV2).ShouldBe(RowLevelSecurity.CreateSql(RowLevelSecurity.PipelineProtectedTables, RowLevelSecurity.PlatformRowTablesV1));
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV2).ShouldNotContain("History");
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV2).ShouldNotContain("[dbo].[Documents] AFTER UPDATE");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_history_migration_re_creates_the_policy(bool up)
    {
        var migration = new TenantPolicyHistoryAndMoves();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();

        statements.ShouldBe([RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(up ? RowLevelSecurity.PolicyV3 : RowLevelSecurity.PolicyV2)]);
    }

    [Fact]
    public void Every_temporal_tables_history_is_under_the_policy()
    {
        using var context = NewContext();
        // Temporal configuration lives in the design-time model only.
        var temporal = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetEntityTypes().Where(e => !e.IsOwned() && e.IsTemporal()).ToList();
        temporal.ShouldNotBeEmpty();

        var strict = RowLevelSecurity.CurrentPolicy.StrictHistoryTables.Select(t => t.Table).ToList();
        var platform = RowLevelSecurity.CurrentPolicy.PlatformRowHistoryTables.Select(t => t.Table).ToList();
        foreach (var entity in temporal)
        {
            var history = entity.GetHistoryTableName()!;
            var nullableTenant = entity.FindProperty("TenantId") is { IsNullable: true };
            (nullableTenant ? platform : strict).ShouldContain(history, $"{entity.GetTableName()} is temporal; its history table needs a filter predicate");
        }

        strict.Concat(platform).Count().ShouldBe(temporal.Count);
    }

    [Fact]
    public void Ingestion_channel_types_named_in_the_back_fill_are_the_upload_channels()
    {
        // The back-fill matches the stored enum names; renaming the members would silently skip rows.
        nameof(ChannelType.ApiUpload).ShouldBe("ApiUpload");
        nameof(ChannelType.PortalUpload).ShouldBe("PortalUpload");
    }

    private static DocProcDbContext NewContext()
    {
        var options = PersistenceOptions.ConfigureSqlServer(
            new DbContextOptionsBuilder<DocProcDbContext>(),
            "Server=unused;Database=unused;Integrated Security=false;TrustServerCertificate=true",
            migrationsOnly: true);
        return new DocProcDbContext(options.Options, TenantContext.Platform());
    }
}
