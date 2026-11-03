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

        // Tenants created after the migration get their adopted types from the seeder (docs/11 §21 step 2).
        (await TypeCatalogueSeeder.EnsureAllAsync(platform, now, ct)).ShouldBe(14);
        (await TypeCatalogueSeeder.EnsureAllAsync(platform, now, ct)).ShouldBe(0, "the seeder only adds what is missing");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_P1_P2_and_P3_migrations_migrate_down_and_up_again()
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
            (await asTenant.DocumentTypeVersions.IgnoreQueryFilters().CountAsync(v => v.TenantId == null, ct)).ShouldBe(StandardTemplates.All.Count);
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
