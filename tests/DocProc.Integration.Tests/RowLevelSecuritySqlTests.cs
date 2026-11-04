using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Outputs;
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
        // A flag added after P0 is back-filled by its own migration (ConfigurableSteps by ConfigurableStepsAndBudgets).
        flags.Keys.ShouldBe(typeof(TenantFeatureFlags).GetProperties().Select(p => p.Name).Except([nameof(TenantFeatureFlags.ConfigurableSteps)]), ignoreOrder: true);
        new ConfigurableStepsAndBudgets().UpOperations.OfType<SqlOperation>().ShouldContain(o => o.Sql == RowLevelSecurity.InPlatformScope(ConfigurableStepsAndBudgets.BackfillFlagSql));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_type_catalogue_migration_seeds_inside_platform_scope_and_re_creates_the_policy(bool up)
    {
        var migration = new TypeCatalogue();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();

        if (up)
        {
            statements[0].ShouldBe(RowLevelSecurity.DropSql());
            statements[^1].ShouldBe(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV4));
            statements[1..^1].ShouldBe([.. TypeCatalogueSeedSql.Statements().Select(RowLevelSecurity.InPlatformScope)]);
        }
        else
        {
            statements.ShouldBe([RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV3)]);
        }

        var sql = RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV4);
        foreach (var table in new[] { "DocumentTypeDefinitions", "DocumentTypeVersions", "LlmProviderConfigs", "ExtractorProfiles" })
        {
            sql.ShouldContain($"ADD FILTER PREDICATE [rls].[{RowLevelSecurity.PlatformReadFunction}]([TenantId]) ON [dbo].[{table}]");
            sql.ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[{table}] BEFORE DELETE");
        }

        sql.ShouldContain($"ADD FILTER PREDICATE [rls].[{RowLevelSecurity.PlatformReadFunction}]([TenantId]) ON [dbo].[DocumentTypeVersionsHistory]");
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV3).ShouldNotContain("DocumentType");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_field_index_migration_puts_the_index_under_the_strict_policy(bool up)
    {
        // docs/11 §6.2: DocumentFieldIndex is a tenant table; FieldIndexAndInboxColumns re-creates the policy with it.
        var migration = new FieldIndexAndInboxColumns();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();
        statements.ShouldBe([RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(up ? RowLevelSecurity.PolicyV5 : RowLevelSecurity.PolicyV4)]);

        var sql = RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV5);
        sql.ShouldContain($"ADD FILTER PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[DocumentFieldIndex]");
        sql.ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[DocumentFieldIndex] AFTER UPDATE");
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV4).ShouldNotContain("DocumentFieldIndex");
        RowLevelSecurity.PolicyV5.PlatformRowTables.ShouldBe(RowLevelSecurity.PolicyV4.PlatformRowTables);
        RowLevelSecurity.PolicyV5.StrictTables.Except(RowLevelSecurity.PolicyV4.StrictTables).ShouldBe([("DocumentFieldIndex", "TenantId")]);

        // The drop comes before the table goes, so the schema-bound policy never names a missing table.
        if (!up)
        {
            var operations = migration.DownOperations.ToList();
            operations.FindIndex(o => o is SqlOperation).ShouldBeLessThan(operations.FindIndex(o => o is DropTableOperation { Name: "DocumentFieldIndex" }));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_table_reports_migration_puts_the_reports_under_the_strict_policy(bool up)
    {
        // docs/11 §6.2: DocumentTableReports is a tenant table; TableReports re-creates the policy with it.
        var migration = new TableReports();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();
        statements.ShouldBe([RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(up ? RowLevelSecurity.PolicyV6 : RowLevelSecurity.PolicyV5)]);

        var sql = RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV6);
        sql.ShouldContain($"ADD FILTER PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[DocumentTableReports]");
        sql.ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[DocumentTableReports] AFTER UPDATE");
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV5).ShouldNotContain("DocumentTableReports");
        RowLevelSecurity.PolicyV6.PlatformRowTables.ShouldBe(RowLevelSecurity.PolicyV5.PlatformRowTables);
        RowLevelSecurity.PolicyV6.StrictTables.Except(RowLevelSecurity.PolicyV5.StrictTables).ShouldBe([("DocumentTableReports", "TenantId")]);

        var operations = (up ? migration.UpOperations : migration.DownOperations).ToList();
        if (up)
        {
            operations.FindLastIndex(o => o is SqlOperation).ShouldBeGreaterThan(operations.FindIndex(o => o is CreateTableOperation { Name: "DocumentTableReports" }), "the policy names the table once it exists");
        }
        else
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeLessThan(operations.FindIndex(o => o is DropTableOperation { Name: "DocumentTableReports" }));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_outputs_migration_puts_the_output_tables_under_the_policy_and_seeds_the_templates_in_platform_scope(bool up)
    {
        // docs/11 §6.7, §6.10: the tables with a tenant of their own are strict, mappings and lookup tables hold
        // platform rows; OutputsAndLookups re-creates the policy with them and seeds the platform output templates.
        var migration = new OutputsAndLookups();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();
        if (up)
        {
            statements.First().ShouldBe(RowLevelSecurity.DropSql());
            statements.Last().ShouldBe(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV7));
            statements.Skip(1).SkipLast(1).ShouldBe([.. OutputTemplates.SeedStatements().Select(RowLevelSecurity.InPlatformScope)]);
            statements.Count.ShouldBe(2 + (2 * OutputTemplates.Frozen().Count));
        }
        else
        {
            statements.ShouldBe([RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV6)]);
        }

        RowLevelSecurity.PolicyV7.StrictTables.Except(RowLevelSecurity.PolicyV6.StrictTables).Select(t => t.Table)
            .ShouldBe(["OutputDestinations", "OutputBindings", "OutboxDeliveries", "DeliveryAttempts", "DocumentExternalRefs"], ignoreOrder: true);
        RowLevelSecurity.PolicyV7.PlatformRowTables.Except(RowLevelSecurity.PolicyV6.PlatformRowTables).Select(t => t.Table)
            .ShouldBe(["OutputMappings", "OutputMappingVersions", "LookupTables", "LookupEntries"], ignoreOrder: true);
        var sql = RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV7);
        sql.ShouldContain($"ADD FILTER PREDICATE [rls].[{RowLevelSecurity.PlatformReadFunction}]([TenantId]) ON [dbo].[OutputMappings]");
        sql.ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[LookupEntries] BEFORE DELETE");
        sql.ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[OutboxDeliveries] AFTER UPDATE");

        var operations = (up ? migration.UpOperations : migration.DownOperations).ToList();
        if (up)
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeGreaterThan(operations.FindLastIndex(o => o is CreateTableOperation), "the seed and the policy come once the tables exist");
        }
        else
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeLessThan(operations.FindIndex(o => o is DropTableOperation));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_routing_migration_puts_the_supplier_configuration_tables_under_the_strict_policy(bool up)
    {
        // docs/11 §6.4, §6.5, §6.10: supplier groups, receiving entities, per-entity attributes and source rules belong
        // to one tenant; SourceRoutingAndSupplierConfig re-creates the policy with them (PolicyV8), and its Down goes
        // back to PolicyV7 after dropping the policy first (it is schema-bound to the tables Down drops).
        var migration = new SourceRoutingAndSupplierConfig();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();
        if (up)
        {
            statements.ShouldBe([RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV8)]);
        }
        else
        {
            statements.First().ShouldBe(RowLevelSecurity.DropSql());
            statements.Last().ShouldBe(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV7));
        }

        RowLevelSecurity.PolicyV8.StrictTables.Except(RowLevelSecurity.PolicyV7.StrictTables).Select(t => t.Table)
            .ShouldBe(["SupplierGroups", "ReceivingEntities", "SupplierEntityAttributes", "SupplierSourceRules"], ignoreOrder: true);
        RowLevelSecurity.PolicyV8.PlatformRowTables.ShouldBe(RowLevelSecurity.PolicyV7.PlatformRowTables);
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV8).ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[SupplierSourceRules] AFTER UPDATE");

        var operations = (up ? migration.UpOperations : migration.DownOperations).ToList();
        if (up)
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeGreaterThan(operations.FindLastIndex(o => o is CreateTableOperation));
        }
        else
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeLessThan(operations.FindIndex(o => o is DropTableOperation));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_tenant_identification_rules_migration_puts_the_P5_part_B_tables_under_the_strict_policy(bool up)
    {
        // docs/11 §6.6, §8.5: tenant fallback rules and their versions belong to one tenant; TenantIdentificationRules
        // re-creates the policy with them (PolicyV12), its Down goes back to PolicyV11.
        var migration = new TenantIdentificationRules();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();
        statements.ShouldBe(up
            ? [RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV12)]
            : [RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV11)]);

        RowLevelSecurity.CurrentPolicy.ShouldBe(RowLevelSecurity.PolicyV12);
        RowLevelSecurity.PolicyV12.StrictTables.Except(RowLevelSecurity.PolicyV11.StrictTables).Select(t => t.Table)
            .ShouldBe(["TypeIdentificationRules", "TypeIdentificationRuleVersions"], ignoreOrder: true);
        RowLevelSecurity.PolicyV12.PlatformRowTables.ShouldBe(RowLevelSecurity.PolicyV11.PlatformRowTables);
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV12).ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[TypeIdentificationRuleVersions] AFTER UPDATE");

        var operations = (up ? migration.UpOperations : migration.DownOperations).ToList();
        if (up)
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeGreaterThan(operations.FindLastIndex(o => o is CreateTableOperation));
        }
        else
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeLessThan(operations.FindIndex(o => o is DropTableOperation));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_configurable_steps_migration_puts_the_P5_part_A_tables_under_the_strict_policy(bool up)
    {
        // docs/11 §10, §16: pipeline snapshots, lookup results and LLM budgets belong to one tenant;
        // ConfigurableStepsAndBudgets re-creates the policy with them (PolicyV11), its Down goes back to PolicyV10.
        var migration = new ConfigurableStepsAndBudgets();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();
        statements.ShouldBe(up
            ? [RowLevelSecurity.InPlatformScope(ConfigurableStepsAndBudgets.BackfillFlagSql), RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV11)]
            : [RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV10), RowLevelSecurity.InPlatformScope(ConfigurableStepsAndBudgets.RemoveFlagSql)]);

        RowLevelSecurity.PolicyV11.StrictTables.Except(RowLevelSecurity.PolicyV10.StrictTables).Select(t => t.Table)
            .ShouldBe(["PipelineSnapshots", "LookupResults", "LlmBudgets"], ignoreOrder: true);
        RowLevelSecurity.PolicyV11.PlatformRowTables.ShouldBe(RowLevelSecurity.PolicyV10.PlatformRowTables);
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV11).ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[LlmBudgets] AFTER UPDATE");

        var operations = (up ? migration.UpOperations : migration.DownOperations).ToList();
        if (up)
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeGreaterThan(operations.FindLastIndex(o => o is CreateTableOperation));
        }
        else
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeLessThan(operations.FindIndex(o => o is DropTableOperation));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_governance_migration_puts_the_part_C_tables_under_the_strict_policy(bool up)
    {
        // docs/11 §6.9, §6.10: golden documents, sandbox and regression runs and notifications belong to one tenant;
        // LayoutLearningAndGovernance re-creates the policy with them (PolicyV10), its Down goes back to PolicyV9.
        var migration = new LayoutLearningAndGovernance();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();
        if (up)
        {
            statements.ShouldBe([RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV10)]);
        }
        else
        {
            statements.ShouldBe([RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV9)]);
        }

        RowLevelSecurity.PolicyV10.StrictTables.Except(RowLevelSecurity.PolicyV9.StrictTables).Select(t => t.Table)
            .ShouldBe(["GoldenDocuments", "RegressionRuns", "Notifications"], ignoreOrder: true);
        RowLevelSecurity.PolicyV10.PlatformRowTables.ShouldBe(RowLevelSecurity.PolicyV9.PlatformRowTables);
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV10).ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[Notifications] AFTER UPDATE");

        var operations = (up ? migration.UpOperations : migration.DownOperations).ToList();
        if (up)
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeGreaterThan(operations.FindLastIndex(o => o is CreateTableOperation));
        }
        else
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeLessThan(operations.FindIndex(o => o is DropTableOperation));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_layouts_migration_puts_the_layout_tables_under_the_strict_policy(bool up)
    {
        // docs/11 §6.6, §6.10: supplier layouts and their versions belong to one tenant; SupplierLayoutsAndIdentification
        // re-creates the policy with them (PolicyV9), and its Down goes back to PolicyV8 after dropping the policy first.
        var migration = new SupplierLayoutsAndIdentification();
        var statements = (up ? migration.UpOperations : migration.DownOperations).OfType<SqlOperation>().Select(o => o.Sql).ToList();
        if (up)
        {
            statements.ShouldBe([RowLevelSecurity.DropSql(), RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV9)]);
        }
        else
        {
            statements.First().ShouldBe(RowLevelSecurity.DropSql());
            statements.Last().ShouldBe(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV8));
        }

        RowLevelSecurity.PolicyV9.StrictTables.Except(RowLevelSecurity.PolicyV8.StrictTables).Select(t => t.Table)
            .ShouldBe(["SupplierLayouts", "SupplierLayoutVersions"], ignoreOrder: true);
        RowLevelSecurity.PolicyV9.PlatformRowTables.ShouldBe(RowLevelSecurity.PolicyV8.PlatformRowTables);
        RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV9).ShouldContain($"ADD BLOCK PREDICATE [rls].[{RowLevelSecurity.PredicateFunction}]([TenantId]) ON [dbo].[SupplierLayoutVersions] AFTER UPDATE");

        var operations = (up ? migration.UpOperations : migration.DownOperations).ToList();
        if (up)
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeGreaterThan(operations.FindLastIndex(o => o is CreateTableOperation));
        }
        else
        {
            operations.FindIndex(o => o is SqlOperation).ShouldBeLessThan(operations.FindIndex(o => o is DropTableOperation));
        }
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
