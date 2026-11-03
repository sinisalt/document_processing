using DocProc.Infrastructure.Outputs;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Integration.Tests.Outputs;

/// <summary>
/// The output and lookup tables on SQL Server behind row-level security (docs/11 §6.7, §6.10, policy
/// <c>PolicyV7</c>): destinations, bindings, deliveries, their attempts and external references are a tenant's own;
/// the platform output templates and code lists are read by every tenant and changed by none; a tenant's mappings and
/// lookup entries stay private to it. Also what the <c>OutputsAndLookups</c> migration seeds.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OutputsSqlTests(SqlServerFixture sqlServer) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    internal static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

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
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_migration_seeds_the_platform_output_templates_as_the_build_ships_them()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using var platform = NewContext(TenantContext.Platform());

        var rows = await platform.OutputMappings.Include(m => m.Versions).Where(m => m.TenantId == null).OrderBy(m => m.Key).ToListAsync(ct);

        rows.Select(m => m.Key).ShouldBe(["generic-ap-json", "std.event"]);
        foreach (var row in rows)
        {
            var template = OutputTemplates.Find(row.Key)!;
            row.Id.ShouldBe(OutputTemplates.PlatformMappingId(row.Key));
            row.PublishedVersion.ShouldBe(template.Version);
            var version = row.Versions.ShouldHaveSingleItem();
            version.Expression.ShouldBe(template.Expression);
            version.State.ShouldBe(OutputMappingVersionState.Published);
            version.TargetSchemaJson.ShouldNotBeNull();
        }

        (await OutputTemplates.AddMissingAsync(platform, DateTimeOffset.UtcNow, ct)).ShouldBe(0, "the seeder finds nothing missing after the migration");
    }

    [Fact]
    public async Task Every_tenant_reads_the_platform_templates_and_none_changes_them()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using var asTenant = NewContext(TenantContext.ForTenant(tenant));
            (await asTenant.OutputMappings.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(2);
            (await asTenant.OutputMappingVersions.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(2);
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            await Should.ThrowAsync<SqlException>(() => asA.Database.ExecuteSqlAsync($"UPDATE dbo.OutputMappingVersions SET Expression = N'1' WHERE TenantId IS NULL", ct));
            await Should.ThrowAsync<SqlException>(() => asA.Database.ExecuteSqlAsync($"DELETE FROM dbo.OutputMappings WHERE TenantId IS NULL", ct));
            asA.LookupTables.Add(new LookupTable { Id = Guid.NewGuid(), TenantId = null, Key = "forged", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
        }

        await using var platform = NewContext(TenantContext.Platform());
        (await platform.OutputMappingVersions.CountAsync(v => v.TenantId == null && v.Expression != "1", ct)).ShouldBe(2, "nothing changed");
    }

    [Fact]
    public async Task A_tenants_outputs_deliveries_and_lookups_are_its_own()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        P3Rows rows;
        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            rows = await SeedAsync(asA, TenantA, ct);
            (await asA.OutputDestinations.CountAsync(ct)).ShouldBe(1);
            (await asA.OutboxDeliveries.CountAsync(ct)).ShouldBe(1);
            (await asA.DeliveryAttempts.CountAsync(ct)).ShouldBe(1);
            (await asA.LookupEntries.CountAsync(ct)).ShouldBe(1);
        }

        await using (var asB = NewContext(TenantContext.ForTenant(TenantB)))
        {
            (await asB.OutputDestinations.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(0);
            (await asB.OutputBindings.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(0);
            (await asB.OutboxDeliveries.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(0);
            (await asB.DeliveryAttempts.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(0);
            (await asB.DocumentExternalRefs.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(0);
            (await asB.OutputMappings.IgnoreQueryFilters().CountAsync(m => m.TenantId != null, ct)).ShouldBe(0);
            (await asB.OutputMappingVersions.IgnoreQueryFilters().CountAsync(v => v.TenantId != null, ct)).ShouldBe(0);
            (await asB.LookupTables.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(0);
            (await asB.LookupEntries.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(0);
            (await asB.Database.ExecuteSqlAsync($"UPDATE dbo.OutputDestinations SET Name = N'stolen'", ct)).ShouldBe(0, "the filter hides A's rows from B's update");

            // B cannot write a row under A's tenant.
            asB.OutputDestinations.Add(new OutputDestination { Id = Guid.NewGuid(), TenantId = TenantA, Name = "forged", Transport = OutputTransports.Pull, CreatedBy = "b", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await Should.ThrowAsync<DbUpdateException>(() => asB.SaveChangesAsync(ct));
        }

        // A cannot move its own row to B.
        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            await Should.ThrowAsync<SqlException>(() => asA.Database.ExecuteSqlAsync($"UPDATE dbo.OutboxDeliveries SET TenantId = {TenantB} WHERE Id = {rows.DeliveryId}", ct));
            await Should.ThrowAsync<SqlException>(() => asA.Database.ExecuteSqlAsync($"UPDATE dbo.LookupEntries SET TenantId = {TenantB}", ct));
        }
    }

    /// <summary>The ids of the rows <see cref="SeedAsync"/> wrote.</summary>
    internal sealed record P3Rows(Guid DestinationId, Guid MappingId, Guid BindingId, Guid DeliveryId, Guid SupplierId);

    /// <summary>
    /// One of everything P3 writes for <paramref name="tenantId"/>: a supplier with attributes, a document with its
    /// outbox row, a destination with a secret reference, a mapping with a published version, a binding, a delivery
    /// with an attempt and an external reference, and a lookup table with an entry. Used by the migrate down and up test.
    /// </summary>
    internal static async Task<P3Rows> SeedAsync(DocProcDbContext db, Guid tenantId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var channel = new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = $"api-{Guid.NewGuid():N}", Type = ChannelType.ApiUpload, CreatedAt = now };
        var blob = new BlobReference { Container = "raw", Key = $"p3-{Guid.NewGuid():N}", Sha256 = new string('0', 64), SizeBytes = 1, ContentType = "application/pdf" };
        var ingestion = new Ingestion { Id = Guid.CreateVersion7(), TenantId = tenantId, ChannelId = channel.Id, ReceivedAt = now, RawBlob = blob, UpdatedAt = now };
        var item = new IngestionItem { Id = Guid.CreateVersion7(), TenantId = tenantId, IngestionId = ingestion.Id, FileName = "a.pdf", MimeType = "application/pdf", Sha256 = new string('1', 64), Blob = blob, Kind = ItemKind.Pdf, CreatedAt = now };
        var supplier = new Supplier { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "Nordwind", NormalizedName = $"NORDWIND {Guid.NewGuid():N}", AttributesJson = """{"erpVendorId":"V-10042"}""", CreatedAt = now, UpdatedAt = now };
        var document = new Document { Id = Guid.CreateVersion7(), TenantId = tenantId, IngestionItemId = item.Id, SupplierId = supplier.Id, Status = Domain.Documents.DocumentStatus.Approved, CreatedAt = now, UpdatedAt = now, ApprovedAt = now };
        var outbox = new OutboxMessage { Id = Guid.CreateVersion7(), TenantId = tenantId, DocumentId = document.Id, EventType = "document.approved", PayloadJson = "{}", CreatedAt = now, FannedOutAt = now };
        var destination = new OutputDestination { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "erp-ap", Transport = OutputTransports.Http, ConfigJson = """{"urlTemplate":"https://erp.example/api"}""", SecretRef = "tenant/x/destination/y/1", CreatedBy = "a", CreatedAt = now, UpdatedAt = now };
        var mapping = new OutputMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, Key = "erp-ap-import", Name = "ERP", Origin = OutputMappingOrigin.Custom, PublishedVersion = 1, CreatedBy = "a", CreatedAt = now, UpdatedAt = now };
        var version = new OutputMappingVersion { Id = Guid.CreateVersion7(), MappingId = mapping.Id, TenantId = tenantId, Version = 1, Engine = OutputEngines.Jsonata, Expression = "meta.documentId", TargetSchemaJson = """{"type":"string"}""", State = OutputMappingVersionState.Published, CreatedBy = "a", CreatedAt = now, UpdatedAt = now };
        var binding = new OutputBinding { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "ap", MappingId = mapping.Id, DestinationId = destination.Id, SupplierId = supplier.Id, ChannelId = channel.Id, CreatedBy = "a", CreatedAt = now, UpdatedAt = now };
        var delivery = new OutboxDelivery { Id = Guid.CreateVersion7(), TenantId = tenantId, OutboxMessageId = outbox.Id, DocumentId = document.Id, DocumentVersion = 1, BindingId = binding.Id, DestinationId = destination.Id, MappingVersionId = version.Id, IdempotencyKey = $"{document.Id}:1:{binding.Id}", Status = DeliveryStatus.Failed, Attempts = 1, NextAttemptAt = now, PayloadBlobKey = "exports/x", PayloadSha256 = new string('a', 64), CreatedAt = now };
        delivery.AttemptLog.Add(new DeliveryAttempt { TenantId = tenantId, At = now, DurationMs = 12, StatusCode = 503, ErrorClass = "retryable", ResponseExcerpt = "busy" });
        var table = new LookupTable { Id = Guid.CreateVersion7(), TenantId = tenantId, Key = "erp-uom", CreatedAt = now, UpdatedAt = now };
        table.Entries.Add(new LookupEntry { TenantId = tenantId, From = "KAR", To = "CT", ValidFrom = new DateOnly(2026, 1, 1) });
        mapping.Versions.Add(version);
        db.AddRange(channel, ingestion, item, supplier, document, outbox, destination, mapping, binding, delivery, table);
        db.DocumentExternalRefs.Add(new DocumentExternalRef { Id = Guid.CreateVersion7(), TenantId = tenantId, DocumentId = document.Id, DestinationId = destination.Id, ExternalId = "ERP-1", UpdatedAt = now });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return new P3Rows(destination.Id, mapping.Id, binding.Id, delivery.Id, supplier.Id);
    }

    private DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), _connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }
}
