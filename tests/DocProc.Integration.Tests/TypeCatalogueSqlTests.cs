using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Configuration;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Integration.Tests;

/// <summary>
/// The type catalogue on SQL Server behind row-level security (docs/11 §6.1, §6.10): every tenant reads the platform
/// templates and none can change them; a tenant's types, their versions and their history stay private to it; the
/// seeder, the catalogue operations and the resolver work in a tenant session; <c>LlmProviderConfigs</c> is under the
/// same platform-row predicates now.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class TypeCatalogueSqlTests(SqlServerFixture sqlServer) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!DockerFacts.IsAvailable)
        {
            return;
        }

        _connectionString = sqlServer.NewDatabaseConnectionString();
        var ct = TestContext.Current.CancellationToken;
        await using var platform = NewContext(TenantContext.Platform());
        await platform.Database.MigrateAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, slug) in new[] { (TenantA, "tenant-a"), (TenantB, "tenant-b") })
        {
            platform.Tenants.Add(new Tenant { Id = id, Name = slug, Slug = slug, LegalName = slug, DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
        }

        await platform.SaveChangesAsync(ct);

        // Tenants created after the migration get their adopted types from the seeder (docs/11 §21 step 2): 7 types for
        // each of the two tenants, plus what the build ships after the migrations froze theirs: the price-list templates
        // v2 (two platform versions) and the output template generic-article-prices-json (its row and version, P5).
        (await TypeCatalogueSeeder.EnsureAllAsync(platform, now, ct)).ShouldBe(18);
        (await TypeCatalogueSeeder.EnsureAllAsync(platform, now, ct)).ShouldBe(0, "the seeder only adds what is missing");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// After the Down to P4, the rows P5 wrote read under the rules of the build before P5 (Correction (P5) fix1): its
    /// definition reader refuses <c>flags</c> and a rule's <c>flag</c>, its supplier settings reader refuses
    /// <c>pipelineOverrides</c>, and its <c>SourceMethod</c> has no <c>Lookup</c>; the adopted type is back on the
    /// version it had before the upgrade to template v2, with its pinned document, and the platform row publishes v1.
    /// </summary>
    private static async Task AssertReadableByTheBuildBeforeP5Async(DocProcDbContext platform, Guid document, Guid supplier, Guid group, Guid definition, Guid beforeUpgrade, Guid afterUpgrade, CancellationToken ct)
    {
        async Task<int> CountAsync(FormattableString sql) => await platform.Database.SqlQuery<int>(sql).SingleAsync(ct);

        foreach (var table in new[] { "DocumentTypeVersions", "DocumentTypeVersionsHistory" })
        {
            (await platform.Database.SqlQuery<int>(System.Runtime.CompilerServices.FormattableStringFactory.Create(
                $"SELECT COUNT(*) AS [Value] FROM [{table}] WHERE JSON_QUERY(CAST([DefinitionJson] AS nvarchar(max)), N'$.flags') IS NOT NULL OR CHARINDEX(N'X_DOWNGRADE', CAST([DefinitionJson] AS nvarchar(max))) > 0")).SingleAsync(ct))
                .ShouldBe(0, $"{table}: no definition flags (the earlier reader refuses them)");
        }

        // The stored definitions still parse with the strict reader, without flags (as the earlier reader requires).
        var stored = await platform.Database.SqlQuery<string>($"SELECT CAST([DefinitionJson] AS nvarchar(max)) AS [Value] FROM [DocumentTypeVersions] WHERE [DefinitionId] = {definition}").ToListAsync(ct);
        stored.Count.ShouldBe(2);
        stored.ShouldAllBe(json => DefinitionSerializer.Parse(json).Flags.Count == 0 && DefinitionSerializer.Parse(json).Rules.All(r => r.Flag == null));
        (await CountAsync($"SELECT [PublishedVersion] AS [Value] FROM [DocumentTypeDefinitions] WHERE [Id] = {definition}")).ShouldBe(1, "back on the version it had before the upgrade");
        (await CountAsync($"SELECT [BaseVersion] AS [Value] FROM [DocumentTypeDefinitions] WHERE [Id] = {definition}")).ShouldBe(1);
        (await platform.Database.SqlQuery<string>($"SELECT [State] AS [Value] FROM [DocumentTypeVersions] WHERE [Id] = {beforeUpgrade}").SingleAsync(ct)).ShouldBe(nameof(DocumentTypeVersionState.Published));
        (await platform.Database.SqlQuery<string>($"SELECT [State] AS [Value] FROM [DocumentTypeVersions] WHERE [Id] = {afterUpgrade}").SingleAsync(ct)).ShouldBe(nameof(DocumentTypeVersionState.Deprecated));
        (await platform.Database.SqlQuery<Guid>($"SELECT [DocumentTypeVersionId] AS [Value] FROM [Documents] WHERE [Id] = {document}").SingleAsync(ct)).ShouldBe(beforeUpgrade, "the pinned document follows the restored version");
        var platformPriceList = TypeCatalogueRows.PlatformDefinitionId("std.price-list");
        (await CountAsync($"SELECT [PublishedVersion] AS [Value] FROM [DocumentTypeDefinitions] WHERE [Id] = {platformPriceList}")).ShouldBe(1, "the earlier build ships the template's first version");

        // Supplier and group settings without pipeline overrides; their other settings stay.
        foreach (var (table, id) in new[] { ("Suppliers", supplier), ("SupplierGroups", group) })
        {
            var settings = await platform.Database.SqlQuery<string>(System.Runtime.CompilerServices.FormattableStringFactory.Create($"SELECT CAST([SettingsJson] AS nvarchar(max)) AS [Value] FROM [{table}] WHERE [Id] = {{0}}", id)).SingleAsync(ct);
            settings.ShouldNotContain("pipelineOverrides", Case.Sensitive, table);
            DocProc.Application.Suppliers.SupplierSettings.Parse(settings).ShouldNotBe(DocProc.Application.Suppliers.SupplierSettings.Empty, $"{table}: the settings still read");
        }

        // The values an http-lookup wrote read as Profile values, in the document and its history; a value that is the
        // word "Lookup" stays.
        foreach (var table in new[] { "Documents", "DocumentsHistory" })
        {
            (await platform.Database.SqlQuery<int>(System.Runtime.CompilerServices.FormattableStringFactory.Create(
                $"SELECT COUNT(*) AS [Value] FROM [{table}] WHERE CHARINDEX(N'\"method\":\"Lookup\"', CAST([CanonicalJson] AS nvarchar(max))) > 0")).SingleAsync(ct)).ShouldBe(0, table);
        }

        var tree = await platform.Database.SqlQuery<string>($"SELECT CAST([CanonicalJson] AS nvarchar(max)) AS [Value] FROM [Documents] WHERE [Id] = {document}").SingleAsync(ct);
        tree.ShouldContain("\"method\":\"Profile\"");
        tree.ShouldContain("\"value\":\"Lookup\"");
        (await CountAsync($"SELECT COUNT(*) AS [Value] FROM [DocumentsHistory] WHERE [Id] = {document} AND CHARINDEX(N'\"method\":\"Profile\"', CAST([CanonicalJson] AS nvarchar(max))) > 0")).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_P1_to_P5_migrations_migrate_down_and_up_again()
    {
        // Their Down methods drop columns of the temporal Documents table, which turns system versioning off; SQL
        // Server refuses that while the schema-bound policy names the table, so the old policy comes back last.
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(platform);

            // A part joined into another document (P2's Cancelled status), with a Cancelled version in the history and
            // a timeline event that names the status.
            var (target, part) = await SeedJoinedPartAsync(platform, ct);

            // P3's outputs first, with one of everything P3 writes: the build before it has none of the output and
            // lookup tables and no supplier attributes, so they go (the supplier stays), and the policy of P2 is back.
            Guid both, feedOnly, delivery;
            await using (var seeding = NewContext(TenantContext.ForTenant(TenantA)))
            {
                var rows = await Outputs.OutputsSqlTests.SeedAsync(seeding, TenantA, ct);
                delivery = rows.DeliveryId;

                // P3 part B's rows: a redelivery of the seeded delivery for a re-approval (document.updated) in a
                // feed, a DELIVERY_FAILED flag, and API keys with the deliveries:read scope, one restricted to a feed.
                var seeded = await seeding.OutboxDeliveries.SingleAsync(d => d.Id == rows.DeliveryId, ct);
                seeding.OutboxDeliveries.Add(new OutboxDelivery
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = TenantA,
                    OutboxMessageId = seeded.OutboxMessageId,
                    DocumentId = seeded.DocumentId,
                    DocumentVersion = 2,
                    BindingId = seeded.BindingId,
                    DestinationId = seeded.DestinationId,
                    MappingVersionId = seeded.MappingVersionId,
                    IdempotencyKey = seeded.IdempotencyKey + ":r1",
                    Event = OutputEvents.Updated,
                    RedeliveryOfId = seeded.Id,
                    FeedSequence = 1,
                    Status = DeliveryStatus.Sending,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                seeding.DocumentFlags.Add(DocumentFlagRecord.FromDomain(TenantA, seeded.DocumentId, Domain.Flags.FlagCodes.Create(Domain.Flags.FlagCodes.DeliveryFailed, "Delivery to erp-ap failed: 503"), DateTimeOffset.UtcNow));
                both = Guid.CreateVersion7();
                feedOnly = Guid.CreateVersion7();
                seeding.ApiKeys.Add(new ApiKey { Id = both, TenantId = TenantA, Name = "both", Prefix = "both0000", Hash = new string('b', 64), Scopes = [ApiKeyScope.Ingest, ApiKeyScope.DeliveriesRead], CreatedAt = DateTimeOffset.UtcNow });
                seeding.ApiKeys.Add(new ApiKey { Id = feedOnly, TenantId = TenantA, Name = "feed", Prefix = "feed0000", Hash = new string('f', 64), Scopes = [ApiKeyScope.DeliveriesRead], DestinationId = seeded.DestinationId, CreatedAt = DateTimeOffset.UtcNow });
                await seeding.SaveChangesAsync(ct);
            }

            Guid issue;

            // P5 part C first, with one of everything it writes: a revision resolved as supersedes, an integration-issue
            // review task, a destination with acknowledgements and a callback secret, a Pending external reference due
            // for a poll, and a master-data source with a run. Down to P5 part B, the tables and columns are gone and
            // the policy of part B is back; up again, they are new (the task's kind back-filled Document).
            await using (var seeding = NewContext(TenantContext.ForTenant(TenantA)))
            {
                var now = DateTimeOffset.UtcNow;
                var seeded = await seeding.OutboxDeliveries.FirstAsync(d => d.Id == delivery, ct);
                var destination = await seeding.OutputDestinations.SingleAsync(d => d.Id == seeded.DestinationId, ct);
                destination.CallbackConfigJson = """{"callback":{"auth":"hmacSha256"},"statusPoll":{"urlTemplate":"https://erp.example/status/{externalId}","intervalMinutes":5,"maxPolls":10}}""";
                destination.CallbackSecretRef = $"tenant/{TenantA:D}/destination/{destination.Id:D}/callback/1";
                var newer = await seeding.Documents.SingleAsync(d => d.Id == target, ct);
                var older = await seeding.Documents.SingleAsync(d => d.Id == part, ct);
                newer.RevisionOfDocumentId = older.Id;
                newer.RevisionResolution = RevisionResolution.Supersedes;
                older.SupersededByDocumentId = newer.Id;
                issue = Guid.CreateVersion7();
                seeding.ReviewTasks.Add(new ReviewTask { Id = issue, TenantId = TenantA, DocumentId = target, Reason = "EXTERNAL_REJECTED: erp", Kind = ReviewTaskKind.IntegrationIssue, Priority = ReviewPriority.High, CreatedAt = now });
                if (!await seeding.DocumentExternalRefs.AnyAsync(r => r.DocumentId == seeded.DocumentId && r.DestinationId == seeded.DestinationId, ct))
                {
                    seeding.DocumentExternalRefs.Add(new DocumentExternalRef { Id = Guid.CreateVersion7(), TenantId = TenantA, DocumentId = seeded.DocumentId, DestinationId = seeded.DestinationId, ExternalId = "AP-1", Status = ExternalRefStatus.Pending, NextPollAt = now, Polls = 2, UpdatedAt = now });
                }

                var source = new MasterDataSource { Id = Guid.CreateVersion7(), TenantId = TenantA, Name = "ERP vendors", Transport = MasterDataTransports.Http, DestinationId = destination.Id, MatchKeys = ["attributes.erpVendorId", "vatId"], ConflictPolicyJson = """{"name":"localWins"}""", ScheduleCron = "CRON_TZ=Europe/Berlin 15 2 * * *", NextRunAt = now, CreatedAt = now, CreatedBy = "test", UpdatedAt = now };
                seeding.MasterDataSources.Add(source);
                seeding.MasterDataRuns.Add(new MasterDataRun { Id = Guid.CreateVersion7(), TenantId = TenantA, SourceId = source.Id, Trigger = MasterDataTrigger.Schedule, Created = 1, ReportJson = """{"created":1,"rows":[]}""", At = now, By = "schedule" });
                await seeding.SaveChangesAsync(ct);

                // The callback order of the fix migration: the last signed callback applied to a reference.
                foreach (var reference in await seeding.DocumentExternalRefs.ToListAsync(ct))
                {
                    reference.LastCallbackAt = now;
                    reference.LastCallbackSignature = new string('c', 64);
                }

                await seeding.SaveChangesAsync(ct);
            }

            await migrator.MigrateAsync("20261104231037_TenantIdentificationRules", ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261104231037_TenantIdentificationRules");
            (await platform.Database.SqlQuery<string>($"SELECT [Status] AS [Value] FROM [ReviewTasks] WHERE [Id] = {issue}").SingleAsync(ct))
                .ShouldBe(nameof(ReviewTaskStatus.Skipped), "an open integration issue would be a document review task of a settled document to the earlier build");
            foreach (var column in new[] { "LastCallbackAt", "LastCallbackSignature" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.DocumentExternalRefs') AND name = {column}").SingleAsync(ct)).ShouldBe(0, column);
            }

            foreach (var table in new[] { "MasterDataSources", "MasterDataRuns" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = {table}").SingleAsync(ct)).ShouldBe(0, table);
            }

            foreach (var (table, column) in new[] { ("Documents", "SupersededByDocumentId"), ("DocumentsHistory", "RevisionResolution"), ("ReviewTasks", "Kind"), ("OutputDestinations", "CallbackSecretRef"), ("DocumentExternalRefs", "NextPollAt") })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({"dbo." + table}) AND name = {column}").SingleAsync(ct)).ShouldBe(0, $"{table}.{column}");
            }

            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_policies WHERE name = N'tenant_policy' AND is_enabled = 1").SingleAsync(ct)).ShouldBe(1);
            await platform.Database.MigrateAsync(ct);
            foreach (var table in new[] { "MasterDataSources", "MasterDataRuns" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_predicates WHERE [target_object_id] = OBJECT_ID({"dbo." + table})").SingleAsync(ct)).ShouldBe(3, table);
            }

            (await platform.ReviewTasks.AsNoTracking().AllAsync(t => t.Kind == ReviewTaskKind.Document, ct)).ShouldBeTrue("up again, every task is a document task");
            (await platform.Documents.AsNoTracking().SingleAsync(d => d.Id == part, ct)).SupersededByDocumentId.ShouldBeNull("up again, the column is new");
            (await platform.MasterDataSources.AsNoTracking().CountAsync(ct)).ShouldBe(0);

            // P5 part B first, with one of everything it writes: a tenant fallback rule with an Active version and a
            // document it typed (its version pinned). Down to P5 part A, the tables and the column are gone and the policy
            // of part A is back; up again, they are new.
            await using (var seeding = NewContext(TenantContext.ForTenant(TenantA)))
            {
                var now = DateTimeOffset.UtcNow;
                var rule = new TypeIdentificationRule { Id = Guid.CreateVersion7(), TenantId = TenantA, DocumentTypeKey = "price-list", Name = "Preisliste im Titel", ActiveVersion = 1, Matched = 1, CreatedAt = now, UpdatedAt = now };
                var ruleVersion = new TypeIdentificationRuleVersion { Id = Guid.CreateVersion7(), TenantId = TenantA, Version = 1, State = TypeIdentificationRuleVersionState.Active, RuleJson = """{"all":[{"text":"Preisliste","page":1,"region":"title"}]}""", RuleHash = "0123abcd", ExcludedSupplierIdsJson = "[]", TestJson = """{"tested":1,"byType":{"price-list":1}}""", CreatedAt = now };
                rule.Versions.Add(ruleVersion);
                seeding.TypeIdentificationRules.Add(rule);
                await seeding.SaveChangesAsync(ct);
                var typed = await seeding.Documents.SingleAsync(d => d.Id == target, ct);
                typed.IdentificationRuleVersionId = ruleVersion.Id;
                await seeding.SaveChangesAsync(ct);
            }

            await migrator.MigrateAsync("20261104221250_ConfigurableStepsAndBudgets", ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261104221250_ConfigurableStepsAndBudgets");
            foreach (var table in new[] { "TypeIdentificationRules", "TypeIdentificationRuleVersions" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = {table}").SingleAsync(ct)).ShouldBe(0, table);
            }

            foreach (var table in new[] { "Documents", "DocumentsHistory" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({"dbo." + table}) AND name = N'IdentificationRuleVersionId'").SingleAsync(ct)).ShouldBe(0, table);
            }

            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_policies WHERE name = N'tenant_policy' AND is_enabled = 1").SingleAsync(ct)).ShouldBe(1);
            await platform.Database.MigrateAsync(ct);
            foreach (var table in new[] { "TypeIdentificationRules", "TypeIdentificationRuleVersions" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_predicates WHERE [target_object_id] = OBJECT_ID({"dbo." + table})").SingleAsync(ct)).ShouldBe(3, table);
            }

            (await platform.Documents.AsNoTracking().SingleAsync(d => d.Id == target, ct)).IdentificationRuleVersionId.ShouldBeNull("up again, the column is new");
            (await platform.TypeIdentificationRules.AsNoTracking().CountAsync(ct)).ShouldBe(0);

            // P5 part A first, with one of everything it writes: a pipeline snapshot, a document that ran it (its hash),
            // a lookup result and an LLM budget. Down to P4 part C, the tables and the column are gone and the policy of
            // part C is back; up again, they are new.
            await using (var seeding = NewContext(TenantContext.ForTenant(TenantA)))
            {
                var now = DateTimeOffset.UtcNow;
                var hash = new string('a', 32);
                seeding.PipelineSnapshots.Add(new PipelineSnapshot { TenantId = TenantA, Hash = hash, StepsJson = """[{"id":"llm","kind":"llm","enabled":false}]""", CreatedAt = now });
                seeding.LookupResults.Add(new LookupResult { Id = Guid.CreateVersion7(), TenantId = TenantA, DocumentId = target, StepId = "erpVendor", RequestHash = new string('b', 64), DestinationId = Guid.CreateVersion7(), Status = LookupCallStatus.Succeeded, HttpStatus = 200, AssignmentsJson = """{"ext.vendor":"V-1"}""", Attempts = 1, DurationMs = 12, CreatedAt = now });
                seeding.LlmBudgets.Add(new LlmBudgetRecord { Id = Guid.CreateVersion7(), TenantId = TenantA, Period = DocProc.Llm.Gateway.LlmBudgetPeriod.Month, Stage = DocProc.Llm.LlmStage.Extract, LimitUsd = 50m, CreatedAt = now, UpdatedAt = now });
                var ran = await seeding.Documents.SingleAsync(d => d.Id == target, ct);
                ran.PipelineHash = hash;
                await seeding.SaveChangesAsync(ct);
            }

            // What P5 writes into data the build before it reads (Correction (P5) fix1): supplier and group settings with
            // pipeline overrides, a canonical tree with values an http-lookup wrote (twice, so the history has one too),
            // and an adopted price list the start-up upgrade moved onto template v2, whose new version declares a
            // definition flag that one of its rules raises, with a document pinned to that version.
            var overriddenSupplier = Guid.CreateVersion7();
            var overriddenGroup = Guid.CreateVersion7();
            var movedDefinition = Guid.CreateVersion7();
            var (beforeUpgrade, afterUpgrade) = (Guid.CreateVersion7(), Guid.CreateVersion7());
            await using (var seeding = NewContext(TenantContext.ForTenant(TenantA)))
            {
                var now = DateTimeOffset.UtcNow;
                const string Overrides = """[{"op":"disable","step":"duplicate"}]""";
                seeding.SupplierGroups.Add(new SupplierGroup { Id = overriddenGroup, TenantId = TenantA, Name = "Overridden", SettingsJson = $$"""{"review":{"mode":"always"},"pipelineOverrides":{{Overrides}}}""", CreatedAt = now, UpdatedAt = now });
                seeding.Suppliers.Add(new Supplier { Id = overriddenSupplier, TenantId = TenantA, Name = "Overridden GmbH", NormalizedName = "overridden", GroupId = overriddenGroup, SettingsJson = $$"""{"requireSenderAuth":true,"pipelineOverrides":{{Overrides}}}""", CreatedAt = now, UpdatedAt = now });

                var template = StandardTemplates.Find("std.price-list").ShouldNotBeNull();
                template.Version.ShouldBe(2, "P5 ships the price list's second template version");
                var definition = new DocumentTypeDefinition
                {
                    Id = movedDefinition,
                    TenantId = TenantA,
                    Key = "price-list-moved",
                    Name = new Dictionary<string, string>(template.Name, StringComparer.Ordinal),
                    Origin = DefinitionOrigin.Adopted,
                    BaseDefinitionId = TypeCatalogueRows.PlatformDefinitionId(template.Key),
                    BaseVersion = 2,
                    PublishedVersion = 2,
                    CreatedAt = now,
                    CreatedBy = "test",
                    UpdatedAt = now,
                };
                var first = TypeCatalogueRows.PublishedVersion(beforeUpgrade, movedDefinition, TenantA, TypeCatalogueRows.Compile(TypeCatalogueRows.Adopted(template, definition.Key, 1)), now, "test");
                var firstJson = System.Text.Json.Nodes.JsonNode.Parse(first.DefinitionJson)!.AsObject();
                firstJson["base"]!["version"] = 1;
                first.DefinitionJson = firstJson.ToJsonString();
                first.State = DocumentTypeVersionState.Deprecated;
                var second = TypeCatalogueRows.PublishedVersion(afterUpgrade, movedDefinition, TenantA, TypeCatalogueRows.Compile(TypeCatalogueRows.Adopted(template, definition.Key, 2)), now, "test");
                var secondJson = System.Text.Json.Nodes.JsonNode.Parse(second.DefinitionJson)!.AsObject();
                secondJson["flags"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["code"] = "X_DOWNGRADE", ["severity"] = "warning" });
                secondJson["rules"]![0]!["flag"] = "X_DOWNGRADE";
                second.DefinitionJson = secondJson.ToJsonString();
                definition.Versions.Add(first);
                definition.Versions.Add(second);
                seeding.DocumentTypeDefinitions.Add(definition);
                await seeding.SaveChangesAsync(ct);

                // The flag is in the history too: the version was published with it, then edited.
                second.ChangeNote = "edited";
                var pinned = await seeding.Documents.SingleAsync(d => d.Id == target, ct);
                pinned.DocumentTypeVersionId = afterUpgrade;
                pinned.CanonicalJson = """{"vendor":{"value":"V-1","status":"Inferred","source":{"method":"Lookup"}},"note":{"value":"Lookup","status":"Extracted","source":{"method":"Llm"}}}""";
                await seeding.SaveChangesAsync(ct);
                pinned.CanonicalJson = """{"vendor":{"value":"V-2","status":"Inferred","source":{"method":"Lookup"}},"note":{"value":"Lookup","status":"Extracted","source":{"method":"Llm"}}}""";
                await seeding.SaveChangesAsync(ct);
            }

            await migrator.MigrateAsync("20261104091721_LayoutLearningAndGovernance", ct);
            await AssertReadableByTheBuildBeforeP5Async(platform, target, overriddenSupplier, overriddenGroup, movedDefinition, beforeUpgrade, afterUpgrade, ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261104091721_LayoutLearningAndGovernance");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [Tenants] WHERE JSON_VALUE(CAST([Settings] AS nvarchar(max)), N'$.Features.ConfigurableSteps') IS NOT NULL").SingleAsync(ct)).ShouldBe(0);
            foreach (var table in new[] { "PipelineSnapshots", "LookupResults", "LlmBudgets" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = {table}").SingleAsync(ct)).ShouldBe(0, table);
            }

            foreach (var table in new[] { "Documents", "DocumentsHistory" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({"dbo." + table}) AND name = N'PipelineHash'").SingleAsync(ct)).ShouldBe(0, table);
            }

            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_policies WHERE name = N'tenant_policy' AND is_enabled = 1").SingleAsync(ct)).ShouldBe(1);
            (await platform.Database.SqlQuery<int>($"SELECT CAST(temporal_type AS int) AS [Value] FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.Documents')").SingleAsync(ct)).ShouldBe(2, "system versioning is on");
            await platform.Database.MigrateAsync(ct);
            foreach (var table in new[] { "PipelineSnapshots", "LookupResults", "LlmBudgets" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_predicates WHERE [target_object_id] = OBJECT_ID({"dbo." + table})").SingleAsync(ct)).ShouldBe(3, table);
            }

            (await platform.Documents.AsNoTracking().SingleAsync(d => d.Id == target, ct)).PipelineHash.ShouldBeNull("up again, the column is new");
            (await platform.LlmBudgets.AsNoTracking().CountAsync(ct)).ShouldBe(0);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [Tenants] WHERE JSON_VALUE(CAST([Settings] AS nvarchar(max)), N'$.Features.ConfigurableSteps') = N'false'").SingleAsync(ct))
                .ShouldBe(2, "the Down removed the flag and the Up back-filled it, off, for every tenant");

            // P4 part C first, with one of everything it writes: a golden document, a sandbox run, a notification and a
            // document waiting for a Candidate layout. Down to part B, the tables and the column are gone and the policy
            // of part B is back; up again, they are new.
            await using (var seeding = NewContext(TenantContext.ForTenant(TenantA)))
            {
                var now = DateTimeOffset.UtcNow;
                seeding.GoldenDocuments.Add(new GoldenDocument { Id = Guid.CreateVersion7(), TenantId = TenantA, DocumentId = target, TypeKey = "Invoice", ExpectedValuesJson = """{"invoiceNumber":"NW-1"}""", AddedAt = now });
                seeding.RegressionRuns.Add(new RegressionRun { Id = Guid.CreateVersion7(), TenantId = TenantA, Kind = RegressionRunKind.Sandbox, Trigger = "sandbox", DocumentId = target, CandidateConfigJson = "{}", ResultJson = """{"passed":true}""", Passed = true, CreatedAt = now });
                seeding.Notifications.Add(new Notification { Id = Guid.CreateVersion7(), TenantId = TenantA, Audience = "MappingAuthor", Kind = "layout.candidate_waiting", Title = "t", Message = "m", DedupeKey = "layout-candidate:x", CreatedAt = now, UpdatedAt = now });
                var waiting = await seeding.Documents.SingleAsync(d => d.Id == target, ct);
                waiting.PendingLayoutVersionId = Guid.CreateVersion7();
                await seeding.SaveChangesAsync(ct);
            }

            await migrator.MigrateAsync("20261104080328_SupplierLayoutsAndIdentification", ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261104080328_SupplierLayoutsAndIdentification");
            foreach (var table in new[] { "GoldenDocuments", "RegressionRuns", "Notifications" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = {table}").SingleAsync(ct)).ShouldBe(0, table);
            }

            foreach (var table in new[] { "Documents", "DocumentsHistory" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({"dbo." + table}) AND name = N'PendingLayoutVersionId'").SingleAsync(ct)).ShouldBe(0, table);
            }

            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_policies WHERE name = N'tenant_policy' AND is_enabled = 1").SingleAsync(ct)).ShouldBe(1);
            (await platform.Database.SqlQuery<int>($"SELECT CAST(temporal_type AS int) AS [Value] FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.Documents')").SingleAsync(ct)).ShouldBe(2, "system versioning is on");
            await platform.Database.MigrateAsync(ct);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_predicates WHERE [target_object_id] = OBJECT_ID(N'dbo.Notifications')").SingleAsync(ct)).ShouldBe(3);
            (await platform.Documents.AsNoTracking().SingleAsync(d => d.Id == target, ct)).PendingLayoutVersionId.ShouldBeNull("up again, the column is new");

            // P4 part B next, with one of everything it writes: a layout with an Active version, a document identified by
            // it (TypeSource Identification in the history) and then held with a proposed type, and its NEW_LAYOUT.
            await using (var seeding = NewContext(TenantContext.ForTenant(TenantA)))
            {
                var now = DateTimeOffset.UtcNow;
                var nordwind = await seeding.Suppliers.SingleAsync(s => s.Name == "Nordwind", ct);
                var layout = new SupplierLayout { Id = Guid.CreateVersion7(), TenantId = TenantA, Scope = SupplierLayoutScope.Supplier, SupplierId = nordwind.Id, OwnerId = nordwind.Id, DocumentTypeKey = "Invoice", LayoutKey = "nordwind-invoice-erp", Name = "Nordwind ERP", ItemKinds = ["pdf"], ActiveVersion = 1, CreatedAt = now, UpdatedAt = now };
                var version = new SupplierLayoutVersion { Id = Guid.CreateVersion7(), TenantId = TenantA, LayoutId = layout.Id, Version = 1, Status = SupplierLayoutVersionStatus.Active, IdentificationJson = """{"all":[{"text":"Rechnung"}]}""", IdentificationHash = "abcdef01", MappingJson = """{"fields":{}}""", CoverageJson = "[]", SimilarityProfileJson = """{"tokens":["RECHNUNG"]}""", CreatedAt = now };
                seeding.SupplierLayouts.Add(layout);
                seeding.SupplierLayoutVersions.Add(version);
                await seeding.SaveChangesAsync(ct);
                var identified = await seeding.Documents.SingleAsync(d => d.Id == target, ct);
                identified.SupplierLayoutVersionId = version.Id;
                identified.TypeSource = TypeSource.Identification;
                await seeding.SaveChangesAsync(ct);
                identified.SupplierLayoutVersionId = null;
                identified.TypeSource = TypeSource.Proposed;
                seeding.DocumentFlags.Add(DocumentFlagRecord.FromDomain(TenantA, target, Domain.Flags.FlagCodes.Create(Domain.Flags.FlagCodes.NewLayout, "new layout"), now));
                await seeding.SaveChangesAsync(ct);
            }

            await migrator.MigrateAsync("20261104064837_SourceRoutingAndSupplierConfig", ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261104064837_SourceRoutingAndSupplierConfig");
            foreach (var table in new[] { "SupplierLayouts", "SupplierLayoutVersions" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = {table}").SingleAsync(ct)).ShouldBe(0, table);
            }

            foreach (var table in new[] { "Documents", "DocumentsHistory" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({"dbo." + table}) AND name = N'SupplierLayoutVersionId'").SingleAsync(ct)).ShouldBe(0, table);
            }

            // The build before part B has no Identification and no Proposed type source: both read as a rule's, in the
            // table and its history.
            (await platform.Database.SqlQuery<string>($"SELECT [TypeSource] AS [Value] FROM dbo.Documents WHERE Id = {target}").SingleAsync(ct)).ShouldBe(nameof(TypeSource.Rule));
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.Documents FOR SYSTEM_TIME ALL WHERE [TypeSource] IN (N'Identification', N'Proposed')").SingleAsync(ct)).ShouldBe(0);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_policies WHERE name = N'tenant_policy' AND is_enabled = 1").SingleAsync(ct)).ShouldBe(1);
            (await platform.Database.SqlQuery<int>($"SELECT CAST(temporal_type AS int) AS [Value] FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.Documents')").SingleAsync(ct)).ShouldBe(2, "system versioning is on again");
            await platform.Database.MigrateAsync(ct);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_predicates WHERE [target_object_id] = OBJECT_ID(N'dbo.SupplierLayoutVersions')").SingleAsync(ct)).ShouldBe(3);
            (await platform.Documents.AsNoTracking().SingleAsync(d => d.Id == target, ct)).SupplierLayoutVersionId.ShouldBeNull("up again, the column is new");

            // P4 part A next, with one of everything it writes: a supplier group, a receiving entity, the supplier's
            // settings, group and entity attributes, a source rule, a channel default and source key, the source facts
            // of an ingestion and its item's route, a routed document with a pinned type and its SUPPLIER_CONFLICT.
            await using (var seeding = NewContext(TenantContext.ForTenant(TenantA)))
            {
                var now = DateTimeOffset.UtcNow;
                var group = new SupplierGroup { Id = Guid.CreateVersion7(), TenantId = TenantA, Name = "Nordwind-Gruppe", AttributesJson = """{"erpGroup":"NW"}""", SettingsJson = """{"requireSenderAuth":true}""", CreatedAt = now, UpdatedAt = now };
                var entity = new ReceivingEntity { Id = Guid.CreateVersion7(), TenantId = TenantA, Code = "1000", Name = "Acme Gastro GmbH", VatIds = ["DE123456789"], AttributesJson = """{"companyCode":"1000"}""", CreatedAt = now, UpdatedAt = now };
                seeding.SupplierGroups.Add(group);
                seeding.ReceivingEntities.Add(entity);
                await seeding.SaveChangesAsync(ct);
                var supplier = await seeding.Suppliers.SingleAsync(s => s.Name == "Nordwind", ct);
                supplier.GroupId = group.Id;
                supplier.SettingsJson = """{"allowedTypeKeys":["Invoice"],"layouts":{"required":"true"}}""";
                seeding.SupplierEntityAttributes.Add(new SupplierEntityAttributes { TenantId = TenantA, SupplierId = supplier.Id, ReceivingEntityId = entity.Id, AttributesJson = """{"erpVendorId":"V-1000-1"}""", UpdatedAt = now });
                var channel = await seeding.IngestionChannels.FirstAsync(ct);
                channel.DefaultSupplierId = supplier.Id;
                channel.DefaultReceivingEntityId = entity.Id;
                channel.SourceKey = "imap:platform/invoices@tenant-a.local/inbox";
                var rule = new SupplierSourceRule { Id = Guid.CreateVersion7(), TenantId = TenantA, SupplierId = supplier.Id, ChannelId = channel.Id, FolderPrefix = "lieferanten/nordwind/", PinTypeKey = "Invoice", ReceivingEntityId = entity.Id, ConditionHash = new string('c', 64), CreatedBy = "test", CreatedAt = now, UpdatedAt = now };
                seeding.SupplierSourceRules.Add(rule);
                var ingestion = await seeding.Ingestions.FirstAsync(ct);
                ingestion.SourceMailbox = "invoices@tenant-a.local";
                ingestion.SourceFolderPath = "lieferanten/nordwind/";
                ingestion.SourceRecipientsJson = """["nordwind@tenant-a.local"]""";
                ingestion.SenderAuthJson = """{"dmarc":"pass"}""";
                ingestion.SourceMessageId = "imap:1:1";
                (await seeding.IngestionItems.FirstAsync(ct)).RouteJson = $$"""{"ruleId":"{{rule.Id}}"}""";
                var document = await seeding.Documents.SingleAsync(d => d.Id == target, ct);
                document.SupplierId = supplier.Id;
                document.SupplierMatchMethod = SupplierMatchMethod.SourceRule;
                document.SupplierSourceRuleId = rule.Id;
                document.ReceivingEntityId = entity.Id;
                document.TypeSource = TypeSource.Supplier;
                seeding.DocumentFlags.Add(DocumentFlagRecord.FromDomain(TenantA, target, Domain.Flags.FlagCodes.Create(Domain.Flags.FlagCodes.SupplierConflict, "conflict"), now));
                await seeding.SaveChangesAsync(ct);
            }

            await migrator.MigrateAsync("20261103221330_DeliveryFeedAndRedelivery", ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261103221330_DeliveryFeedAndRedelivery");
            foreach (var table in new[] { "SupplierGroups", "ReceivingEntities", "SupplierEntityAttributes", "SupplierSourceRules" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = {table}").SingleAsync(ct)).ShouldBe(0, table);
            }

            foreach (var (table, column) in new[] { ("Documents", "SupplierSourceRuleId"), ("Documents", "ReceivingEntityId"), ("DocumentsHistory", "ReceivingEntityId"), ("Suppliers", "GroupId"), ("Suppliers", "SettingsJson"), ("IngestionChannels", "SourceKey"), ("IngestionChannels", "DefaultSupplierId"), ("Ingestions", "SourceMailbox"), ("Ingestions", "SenderAuthJson"), ("IngestionItems", "RouteJson") })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({"dbo." + table}) AND name = {column}").SingleAsync(ct)).ShouldBe(0, $"{table}.{column}");
            }

            // The build before P4 has no Supplier type source and no source match methods: a pinned type reads as a
            // rule's, and the method of a routed supplier as unknown (the supplier stays), in the table and its history.
            (await platform.Database.SqlQuery<string>($"SELECT [TypeSource] AS [Value] FROM dbo.Documents WHERE Id = {target}").SingleAsync(ct)).ShouldBe(nameof(TypeSource.Rule));
            (await platform.Database.SqlQuery<string?>($"SELECT [SupplierMatchMethod] AS [Value] FROM dbo.Documents WHERE Id = {target}").SingleAsync(ct)).ShouldBeNull();
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.Documents FOR SYSTEM_TIME ALL WHERE [TypeSource] = N'Supplier' OR [SupplierMatchMethod] IN (N'SourceRule', N'ChannelDefault', N'Hint')").SingleAsync(ct)).ShouldBe(0);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.Documents WHERE Id = {target} AND SupplierId IS NOT NULL").SingleAsync(ct)).ShouldBe(1, "the supplier stays");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.Suppliers WHERE [Name] = N'Nordwind'").SingleAsync(ct)).ShouldBe(1, "the supplier stays");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_policies WHERE name = N'tenant_policy' AND is_enabled = 1").SingleAsync(ct)).ShouldBe(1);
            (await platform.Database.SqlQuery<int>($"SELECT CAST(temporal_type AS int) AS [Value] FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.Documents')").SingleAsync(ct)).ShouldBe(2, "system versioning is on again");
            await platform.Database.MigrateAsync(ct);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_predicates WHERE [target_object_id] = OBJECT_ID(N'dbo.SupplierSourceRules')").SingleAsync(ct)).ShouldBe(3);
            (await platform.Documents.AsNoTracking().SingleAsync(d => d.Id == target, ct)).SupplierSourceRuleId.ShouldBeNull("up again, the column is new");
            await migrator.MigrateAsync("20261103221330_DeliveryFeedAndRedelivery", ct);

            // P3 part B first: its columns go; deliveries:read is a scope the build before reads as a nameless number,
            // so it leaves every key, and the key that had nothing else is revoked; everything else stays.
            await migrator.MigrateAsync("20261103210336_OutputsAndLookups", ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261103210336_OutputsAndLookups");
            foreach (var (table, column) in new[] { ("OutboxDeliveries", "Event"), ("OutboxDeliveries", "RedeliveryOfId"), ("OutboxDeliveries", "FeedSequence"), ("OutputDestinations", "FeedSequence"), ("ApiKeys", "DestinationId") })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({"dbo." + table}) AND name = {column}").SingleAsync(ct)).ShouldBe(0, $"{table}.{column}");
            }

            (await platform.Database.SqlQuery<string>($"SELECT CAST([Scopes] AS nvarchar(max)) AS [Value] FROM dbo.ApiKeys WHERE Id = {both}").SingleAsync(ct)).ShouldBe("[0]");
            (await platform.Database.SqlQuery<DateTimeOffset?>($"SELECT [RevokedAt] AS [Value] FROM dbo.ApiKeys WHERE Id = {both}").SingleAsync(ct)).ShouldBeNull("it still ingests");
            (await platform.Database.SqlQuery<string>($"SELECT CAST([Scopes] AS nvarchar(max)) AS [Value] FROM dbo.ApiKeys WHERE Id = {feedOnly}").SingleAsync(ct)).ShouldBe("[]");
            (await platform.Database.SqlQuery<DateTimeOffset?>($"SELECT [RevokedAt] AS [Value] FROM dbo.ApiKeys WHERE Id = {feedOnly}").SingleAsync(ct)).ShouldNotBeNull("a feed client's key has nothing left to do");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.OutboxDeliveries").SingleAsync(ct)).ShouldBe(2, "the deliveries stay");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_policies WHERE name = N'tenant_policy' AND is_enabled = 1").SingleAsync(ct)).ShouldBe(1);
            await platform.Database.MigrateAsync(ct);
            (await platform.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == both, ct)).Scopes.ShouldBe([ApiKeyScope.Ingest]);
            (await platform.OutboxDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery, ct)).Event.ShouldBe(OutputEvents.Approved, "up again, the column's default");
            await migrator.MigrateAsync("20261103210336_OutputsAndLookups", ct);

            await migrator.MigrateAsync("20261103112735_TableReports", ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261103112735_TableReports");
            foreach (var table in new[] { "OutputDestinations", "OutputMappings", "OutputMappingVersions", "OutputBindings", "OutboxDeliveries", "DeliveryAttempts", "DocumentExternalRefs", "LookupTables", "LookupEntries" })
            {
                (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = {table}").SingleAsync(ct)).ShouldBe(0, table);
            }

            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Suppliers') AND name = N'AttributesJson'").SingleAsync(ct)).ShouldBe(0);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.Suppliers WHERE [Name] = N'Nordwind'").SingleAsync(ct)).ShouldBe(1, "the supplier stays");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.OutboxMessages").SingleAsync(ct)).ShouldBe(1, "the outbox row stays");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_policies WHERE name = N'tenant_policy' AND is_enabled = 1").SingleAsync(ct)).ShouldBe(1);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_predicates WHERE [target_object_id] = OBJECT_ID(N'dbo.DocumentTableReports')").SingleAsync(ct)).ShouldBe(3);

            // P2's table reports next: the table goes and the policy of P1 is back.
            await migrator.MigrateAsync("20261103102625_ReviewActions", ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261103102625_ReviewActions");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = N'DocumentTableReports'").SingleAsync(ct)).ShouldBe(0);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_policies WHERE name = N'tenant_policy' AND is_enabled = 1").SingleAsync(ct)).ShouldBe(1);

            // P2's review columns next, on their own: the documents keep their rows, and row-level security stays on.
            await migrator.MigrateAsync("20261103001957_FieldIndexAndInboxColumns", ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261103001957_FieldIndexAndInboxColumns");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Documents') AND name IN (N'SupplierMatchMethod', N'JoinedIntoDocumentId')").SingleAsync(ct)).ShouldBe(0);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.DocumentsHistory') AND name = N'SupplierMatchMethod'").SingleAsync(ct)).ShouldBe(0, "the history table follows");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_policies WHERE name = N'tenant_policy' AND is_enabled = 1").SingleAsync(ct)).ShouldBe(1);

            // The build before P2 has no Cancelled status and reads enums as strings: the part, its history and its
            // event are Rejected now, and the event still names the document it was joined into.
            (await platform.Database.SqlQuery<string>($"SELECT [Status] AS [Value] FROM dbo.Documents WHERE Id = {part}").SingleAsync(ct)).ShouldBe(nameof(DocumentStatus.Rejected));
            (await platform.Database.SqlQuery<string>($"SELECT [Status] AS [Value] FROM dbo.Documents WHERE Id = {target}").SingleAsync(ct)).ShouldBe(nameof(DocumentStatus.Split), "only Cancelled rows change");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.Documents FOR SYSTEM_TIME ALL WHERE [Status] = N'Cancelled'").SingleAsync(ct)).ShouldBe(0);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.DocumentsHistory WHERE Id = {part} AND [Status] = N'Rejected'").SingleAsync(ct)).ShouldBeGreaterThanOrEqualTo(2, "the Cancelled versions, the one the Down's own update wrote included");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.DocumentEvents WHERE [ToStatus] = N'Cancelled' OR [FromStatus] = N'Cancelled'").SingleAsync(ct)).ShouldBe(0);
            (await platform.Database.SqlQuery<string>($"SELECT [DetailsJson] AS [Value] FROM dbo.DocumentEvents WHERE DocumentId = {part} AND [ToStatus] = N'Rejected'").SingleAsync(ct)).ShouldContain(target.ToString());
            (await platform.Database.SqlQuery<int>($"SELECT CAST(temporal_type AS int) AS [Value] FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.Documents')").SingleAsync(ct)).ShouldBe(2, "system versioning is on again");

            await platform.Database.MigrateAsync(ct);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Documents') AND name IN (N'SupplierMatchMethod', N'JoinedIntoDocumentId')").SingleAsync(ct)).ShouldBe(2);
            (await platform.Documents.AsNoTracking().SingleAsync(d => d.Id == part, ct)).Status.ShouldBe(DocumentStatus.Rejected, "a downgrade past ReviewActions keeps the part rejected (docs/07)");
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_predicates WHERE [target_object_id] = OBJECT_ID(N'dbo.DocumentTableReports')").SingleAsync(ct)).ShouldBe(3);

            await migrator.MigrateAsync("20261102231925_TypeCatalogue", ct);
            await migrator.MigrateAsync("20261102210935_TenantPolicyHistoryAndMoves", ct);
            (await platform.Database.GetAppliedMigrationsAsync(ct)).Last().ShouldBe("20261102210935_TenantPolicyHistoryAndMoves");
            await platform.Database.MigrateAsync(ct);
            (await platform.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();
        }

        // Up again, the migration seeds the adopted types of the tenants that exist, and the policy filters them.
        await using var asA = NewContext(TenantContext.ForTenant(TenantA));
        (await asA.DocumentTypeDefinitions.CountAsync(d => d.TenantId == TenantA, ct)).ShouldBe(7);
        (await asA.DocumentTypeDefinitions.IgnoreQueryFilters().CountAsync(d => d.TenantId == TenantB, ct)).ShouldBe(0, "row-level security is back");
        (await asA.DocumentFieldIndex.CountAsync(ct)).ShouldBe(0);
        (await asA.DocumentTableReports.CountAsync(ct)).ShouldBe(0);
        (await asA.OutputDestinations.CountAsync(ct)).ShouldBe(0, "the down dropped them; up creates the tables empty");
        (await asA.OutputMappings.IgnoreQueryFilters().Select(m => m.Key).OrderBy(k => k).ToListAsync(ct)).ShouldBe(["generic-ap-json", "std.event"], "the platform templates are seeded again");
        (await asA.Suppliers.SingleAsync(s => s.Name == "Nordwind", ct)).AttributesJson.ShouldBeNull();
    }

    /// <summary>A document of tenant A and a part of the same attachment joined into it (Cancelled, then touched once so the history holds a Cancelled version), with its joined event.</summary>
    private static async Task<(Guid Target, Guid Part)> SeedJoinedPartAsync(DocProcDbContext platform, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var channel = new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = TenantA, Name = "api", Type = ChannelType.ApiUpload, CreatedAt = now };
        var blob = new BlobReference { Container = "raw", Key = "joined", Sha256 = new string('0', 64), SizeBytes = 1, ContentType = "application/pdf" };
        var ingestion = new Ingestion { Id = Guid.CreateVersion7(), TenantId = TenantA, ChannelId = channel.Id, ReceivedAt = now, RawBlob = blob, UpdatedAt = now };
        var item = new IngestionItem { Id = Guid.CreateVersion7(), TenantId = TenantA, IngestionId = ingestion.Id, FileName = "a.pdf", MimeType = "application/pdf", Sha256 = new string('0', 64), Blob = blob, Kind = ItemKind.Pdf, Disposition = ItemDisposition.Accepted, CreatedAt = now };
        var target = new Document { Id = Guid.CreateVersion7(), TenantId = TenantA, IngestionItemId = item.Id, Status = DocumentStatus.Split, CreatedAt = now, UpdatedAt = now };
        var part = new Document { Id = Guid.CreateVersion7(), TenantId = TenantA, IngestionItemId = item.Id, Status = DocumentStatus.Cancelled, JoinedIntoDocumentId = target.Id, CreatedAt = now, UpdatedAt = now };
        platform.IngestionChannels.Add(channel);
        platform.Ingestions.Add(ingestion);
        platform.IngestionItems.Add(item);
        platform.Documents.AddRange(target, part);
        platform.DocumentEvents.Add(new DocumentEvent { TenantId = TenantA, DocumentId = part.Id, EventType = DocumentEventTypes.Joined, FromStatus = DocumentStatus.NeedsReview, ToStatus = DocumentStatus.Cancelled, Actor = "anna", DetailsJson = $$"""{"into":"{{target.Id}}"}""", At = now });
        await platform.SaveChangesAsync(ct);

        part.UpdatedAt = now.AddSeconds(1);
        await platform.SaveChangesAsync(ct);
        (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.DocumentsHistory WHERE Id = {part.Id} AND [Status] = N'Cancelled'").SingleAsync(ct)).ShouldBe(1);
        platform.ChangeTracker.Clear();
        return (target.Id, part.Id);
    }

    [Fact]
    public async Task Every_tenant_reads_the_templates_and_only_platform_scope_changes_them()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var template = TypeCatalogueRows.PlatformDefinitionId("std.invoice");

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using var asTenant = NewContext(TenantContext.ForTenant(tenant));
            (await asTenant.DocumentTypeDefinitions.IgnoreQueryFilters().CountAsync(d => d.TenantId == null, ct)).ShouldBe(StandardTemplates.All.Count);
            // Every version a template has had: version 1 of each, and version 2 of the price-list templates (P5).
            (await asTenant.DocumentTypeVersions.IgnoreQueryFilters().CountAsync(v => v.TenantId == null, ct)).ShouldBe(StandardTemplates.All.Sum(t => t.Version));
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var row = await asA.DocumentTypeDefinitions.SingleAsync(d => d.Id == template, ct);
            row.Description = "changed by a tenant";
            await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            await Should.ThrowAsync<SqlException>(() => asA.Database.ExecuteSqlAsync($"UPDATE dbo.DocumentTypeVersions SET ChangeNote = N'x' WHERE TenantId IS NULL", ct));
            await Should.ThrowAsync<SqlException>(() => asA.Database.ExecuteSqlAsync($"DELETE FROM dbo.DocumentTypeVersions WHERE TenantId IS NULL", ct));
            asA.DocumentTypeDefinitions.Add(new DocumentTypeDefinition { Id = Guid.NewGuid(), TenantId = null, Key = "std.forged", Origin = DefinitionOrigin.Platform, CreatedBy = "a", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
        }

        await using (var platform = NewContext(TenantContext.Platform()))
        {
            var row = await platform.DocumentTypeDefinitions.SingleAsync(d => d.Id == template, ct);
            row.Description.ShouldBe(StandardTemplates.Find("std.invoice")!.Description);
            row.Description = "changed by the platform";
            await platform.SaveChangesAsync(ct);
        }
    }

    [Fact]
    public async Task A_tenants_types_and_their_history_are_its_own()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            // A change on tenant A's adopted version leaves a history row behind.
            var version = await platform.DocumentTypeVersions.SingleAsync(v => v.TenantId == TenantA && v.Definition!.Key == "Invoice", ct);
            version.ChangeNote = "history";
            await platform.SaveChangesAsync(ct);
        }

        await using var asB = NewContext(TenantContext.ForTenant(TenantB));
        (await asB.DocumentTypeDefinitions.IgnoreQueryFilters().CountAsync(d => d.TenantId == TenantA, ct)).ShouldBe(0);
        (await asB.DocumentTypeVersions.IgnoreQueryFilters().CountAsync(v => v.TenantId == TenantA, ct)).ShouldBe(0);
        (await asB.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.DocumentTypeVersions FOR SYSTEM_TIME ALL WHERE TenantId = {TenantA}").SingleAsync(ct)).ShouldBe(0);
        (await asB.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.DocumentTypeVersionsHistory WHERE TenantId = {TenantA}").SingleAsync(ct)).ShouldBe(0);

        await using var asA = NewContext(TenantContext.ForTenant(TenantA));
        (await asA.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.DocumentTypeVersionsHistory WHERE TenantId = {TenantA}").SingleAsync(ct)).ShouldBe(1);
        (await asA.DocumentTypeDefinitions.CountAsync(d => d.TenantId == TenantA, ct)).ShouldBe(7);

        // A tenant cannot move its type to another tenant.
        var own = await asA.DocumentTypeDefinitions.SingleAsync(d => d.TenantId == TenantA && d.Key == "Other", ct);
        own.TenantId = TenantB;
        await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task The_catalogue_adopts_forks_and_publishes_in_a_tenant_session_and_the_resolver_follows()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            (await platform.Tenants.SingleAsync(t => t.Id == TenantA, ct)).Settings.Features!.ConfigurableTypes = true;
            await platform.SaveChangesAsync(ct);
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var catalogue = new DocumentTypeCatalogue(asA, TimeProvider.System);
            (await catalogue.AdoptAsync(TenantA, "std.price-list", null, "admin-a", ct)).Status.ShouldBe(CatalogueStatus.Created);

            var fork = await catalogue.CopyAsync(TenantA, "std.credit-note", null, "admin-a", ct);
            fork.Status.ShouldBe(CatalogueStatus.Ok);
            var draft = fork.Value!.Versions.Single(v => v.State == DocumentTypeVersionState.Draft);
            var edited = DefinitionSerializer.Parse(draft.DefinitionJson.ShouldNotBeNull());
            edited = edited with { Fields = [.. edited.Fields, new FieldDefinition { Id = "fld_c_scr", Key = "supplierCustomerRef", Kind = FieldKinds.Identifier, Indexed = true }] };
            (await catalogue.SaveDraftAsync(TenantA, "CreditNote", draft.Version, DefinitionSerializer.Write(edited), draft.Revision, "admin-a", ct)).Status.ShouldBe(CatalogueStatus.Ok);
            var published = await catalogue.PublishAsync(TenantA, "CreditNote", draft.Version, ifMatch: null, "custom field", "admin-a", ct);
            published.Status.ShouldBe(CatalogueStatus.Ok, string.Join("; ", published.Problems ?? []));
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var resolver = new CatalogueConfigResolver(asA, new EfTenantFeatures(asA), new PublishedDefinitionCache());
            var credit = (await resolver.ResolveAsync(new EffectiveConfigRequest(TenantA, DocumentTypeKey.CreditNote), ct)).Type!;
            credit.Version.ShouldBe(2);
            credit.Origin.ShouldBe(DefinitionOrigin.Copied);
            var list = (await resolver.ResolveAsync(new EffectiveConfigRequest(TenantA, DocumentTypeKey.Parse("price-list")), ct)).Type!;
            list.TemplateKey.ShouldBe("std.price-list");
            var invoice = (await resolver.ResolveAsync(new EffectiveConfigRequest(TenantA, DocumentTypeKey.Invoice), ct)).Type!;
            invoice.Origin.ShouldBe(DefinitionOrigin.Adopted);
            invoice.VersionId.ShouldNotBeNull();
        }

        // Tenant B neither sees nor publishes tenant A's types, and its own credit note is still the adopted one.
        await using (var asB = NewContext(TenantContext.ForTenant(TenantB)))
        {
            var catalogue = new DocumentTypeCatalogue(asB, TimeProvider.System);
            (await catalogue.FindAsync(TenantA, "price-list", ct)).ShouldBeNull();
            (await catalogue.PublishAsync(TenantA, "CreditNote", 2, null, null, "admin-b", ct)).Status.ShouldBe(CatalogueStatus.NotFound);
            var resolver = new CatalogueConfigResolver(asB, new EfTenantFeatures(asB), new PublishedDefinitionCache());
            (await resolver.ResolveAsync(new EffectiveConfigRequest(TenantB, DocumentTypeKey.CreditNote), ct)).Type!.Origin.ShouldBe(DefinitionOrigin.Adopted);
            (await resolver.ResolveAsync(new EffectiveConfigRequest(TenantA, DocumentTypeKey.CreditNote), ct)).Type!.VersionId.ShouldBeNull("B's session cannot read A's catalogue: the embedded fallback answers");
        }
    }

    [Fact]
    public async Task Platform_llm_provider_configurations_are_read_only_to_tenants()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var platformConfig = new LlmProviderConfigRecord { Id = Guid.NewGuid(), Scope = Llm.Providers.ConfigScope.Platform, TenantId = null, Kind = Llm.LlmProviderKind.Anthropic, UpdatedAt = DateTimeOffset.UtcNow };
        var tenantConfig = new LlmProviderConfigRecord { Id = Guid.NewGuid(), Scope = Llm.Providers.ConfigScope.Tenant, TenantId = TenantA, Kind = Llm.LlmProviderKind.OpenAi, UpdatedAt = DateTimeOffset.UtcNow };
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            platform.LlmProviderConfigs.AddRange(platformConfig, tenantConfig);
            await platform.SaveChangesAsync(ct);
        }

        await using (var asB = NewContext(TenantContext.ForTenant(TenantB)))
        {
            (await asB.LlmProviderConfigs.IgnoreQueryFilters().Select(c => c.Id).ToListAsync(ct)).ShouldBe([platformConfig.Id]);
            await Should.ThrowAsync<SqlException>(() => asB.Database.ExecuteSqlAsync($"UPDATE dbo.LlmProviderConfigs SET MaxConcurrency = 1 WHERE Id = {platformConfig.Id}", ct));
        }

        await using var asA = NewContext(TenantContext.ForTenant(TenantA));
        (await asA.LlmProviderConfigs.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(2);
        var own = await asA.LlmProviderConfigs.SingleAsync(c => c.Id == tenantConfig.Id, ct);
        own.MaxConcurrency = 2;
        await asA.SaveChangesAsync(ct);
    }

    private DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), _connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }
}
