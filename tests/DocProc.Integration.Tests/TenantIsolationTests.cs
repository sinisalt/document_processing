using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace DocProc.Integration.Tests;

/// <summary>
/// Applies the migrations to SQL Server 2025 and proves both tenancy layers (docs/01 "Multi-tenancy"): the EF query
/// filter, and row-level security that holds even for raw SQL that bypasses EF.
/// </summary>
public sealed class TenantIsolationTests : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private MsSqlContainer? _container;
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!DockerFacts.IsAvailable)
        {
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest").Build();
        await _container.StartAsync(TestContext.Current.CancellationToken);
        _connectionString = _container.GetConnectionString();

        await using var context = NewContext(TenantContext.Platform());
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await Seed(context);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Ef_query_filter_shows_only_the_current_tenant()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        await using var asA = NewContext(TenantContext.ForTenant(TenantA));
        (await asA.Documents.Select(d => d.DocumentNumber).ToListAsync(ct)).ShouldBe(["A-1"]);
        (await asA.Tenants.CountAsync(ct)).ShouldBe(1);

        await using var asPlatform = NewContext(TenantContext.Platform());
        (await asPlatform.Documents.CountAsync(ct)).ShouldBe(2);

        await using var anonymous = NewContext(TenantContext.Anonymous());
        (await anonymous.Documents.CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Row_level_security_blocks_cross_tenant_reads_and_writes_even_without_ef_filters()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        await using var asB = NewContext(TenantContext.ForTenant(TenantB));

        // IgnoreQueryFilters simulates a missed filter: the database must still hide tenant A.
        var visible = await asB.Documents.IgnoreQueryFilters().Select(d => d.DocumentNumber).ToListAsync(ct);
        visible.ShouldBe(["B-1"]);

        // A write for another tenant is rejected by the block predicate.
        var item = await asB.IngestionItems.FirstAsync(ct);
        asB.Documents.Add(new Document { Id = Guid.CreateVersion7(), TenantId = TenantA, IngestionItemId = item.Id, DocumentNumber = "forged", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Should.ThrowAsync<DbUpdateException>(() => asB.SaveChangesAsync(ct));

        // No session context at all: nothing is visible, even to raw SQL.
        await using var raw = new SqlConnection(_connectionString);
        await raw.OpenAsync(ct);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.Documents", raw);
        (await count.ExecuteScalarAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Documents_are_temporal_and_keep_history()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        await using var context = NewContext(TenantContext.ForTenant(TenantA));
        var document = await context.Documents.SingleAsync(ct);
        document.Status = DocumentStatus.Normalized;
        await context.SaveChangesAsync(ct);

        var versions = await context.Documents.TemporalAll().Where(d => d.Id == document.Id).CountAsync(ct);
        versions.ShouldBeGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// A correction records the field's old and new value as JSON, which for most fields is a bare string, number or
    /// null. SQL Server's json type rejects those (error 13609, "JSON text is not properly formatted"), which made every
    /// date or text correction fail with a 500, so the two columns are plain text.
    /// </summary>
    [Theory]
    [InlineData("\"2026-131\"", "\"2026-131-X\"")]
    [InlineData(null, "\"2026-11-15\"")]
    [InlineData("12.5", "null")]
    [InlineData("{\"amount\":1.00,\"currency\":\"EUR\"}", "[1,2]")]
    public async Task Corrections_store_any_json_value(string? oldValue, string? newValue)
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        await using var context = NewContext(TenantContext.ForTenant(TenantA));
        var document = await context.Documents.SingleAsync(ct);
        context.Corrections.Add(new Correction
        {
            TenantId = TenantA,
            DocumentId = document.Id,
            FieldPath = "dueDate",
            OldValueJson = oldValue,
            NewValueJson = newValue,
            By = "tenant-a-admin",
            At = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(ct);

        var stored = await context.Corrections.AsNoTracking().OrderByDescending(c => c.Id).FirstAsync(ct);
        stored.OldValueJson.ShouldBe(oldValue);
        stored.NewValueJson.ShouldBe(newValue);
    }

    /// <summary>
    /// docs/11 §6.10: a nullable-tenant table (ExtractorProfiles) lets every session read its platform rows (TenantId
    /// NULL) but only platform scope create, change or delete them; tenant rows stay private to their tenant.
    /// </summary>
    [Fact]
    public async Task Platform_rows_are_readable_by_every_tenant_and_writable_only_in_platform_scope()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        var platformRow = NewProfile(tenantId: null, "platform");
        var privateA = NewProfile(TenantA, "private-a");
        var privateB = NewProfile(TenantB, "private-b");
        Guid[] ids = [platformRow.Id, privateA.Id, privateB.Id];
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            platform.ExtractorProfiles.AddRange(platformRow, privateA, privateB);
            await platform.SaveChangesAsync(ct);
        }

        // Reads: the platform row and the own row, never the other tenant's, even with the EF filter bypassed.
        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            (await asA.ExtractorProfiles.IgnoreQueryFilters().Where(p => ids.Contains(p.Id)).Select(p => p.DocumentType).ToListAsync(ct))
                .ShouldBe(["platform", "private-a"], ignoreOrder: true);
        }

        await using (var asB = NewContext(TenantContext.ForTenant(TenantB)))
        {
            (await asB.ExtractorProfiles.IgnoreQueryFilters().Where(p => ids.Contains(p.Id)).Select(p => p.DocumentType).ToListAsync(ct))
                .ShouldBe(["platform", "private-b"], ignoreOrder: true);
        }

        // Writes to the platform row from a tenant session are blocked: update, delete, and creating a new one.
        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var shared = await asA.ExtractorProfiles.SingleAsync(p => p.Id == platformRow.Id, ct);
            shared.Attempts = 99;
            await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            asA.ExtractorProfiles.Remove(await asA.ExtractorProfiles.SingleAsync(p => p.Id == platformRow.Id, ct));
            await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            asA.ExtractorProfiles.Add(NewProfile(tenantId: null, "forged-platform"));
            await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
        }

        // Raw SQL bypasses EF entirely and is blocked all the same.
        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            await Should.ThrowAsync<SqlException>(() => asA.Database.ExecuteSqlAsync($"UPDATE dbo.ExtractorProfiles SET Attempts = 7 WHERE Id = {platformRow.Id}", ct));
            await Should.ThrowAsync<SqlException>(() => asA.Database.ExecuteSqlAsync($"DELETE FROM dbo.ExtractorProfiles WHERE Id = {platformRow.Id}", ct));
        }

        // A tenant keeps full control of its own rows, but cannot turn one into a platform row or hand it to another tenant.
        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var own = await asA.ExtractorProfiles.SingleAsync(p => p.Id == privateA.Id, ct);
            own.Attempts = 3;
            await asA.SaveChangesAsync(ct);

            own.TenantId = null;
            await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var own = await asA.ExtractorProfiles.SingleAsync(p => p.Id == privateA.Id, ct);
            own.TenantId = TenantB;
            await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
        }

        // Platform scope does everything; the platform row is unchanged by the refused writes above.
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            var shared = await platform.ExtractorProfiles.SingleAsync(p => p.Id == platformRow.Id, ct);
            shared.Attempts.ShouldBe(0);
            shared.Attempts = 1;
            await platform.SaveChangesAsync(ct);
            (await platform.ExtractorProfiles.SingleAsync(p => p.Id == privateA.Id, ct)).Attempts.ShouldBe(3);
            platform.ExtractorProfiles.Remove(await platform.ExtractorProfiles.SingleAsync(p => p.Id == privateB.Id, ct));
            await platform.SaveChangesAsync(ct);
        }

        // A session without any context sees the platform rows only.
        await using var raw = new SqlConnection(_connectionString);
        await raw.OpenAsync(ct);
        await using var count = new SqlCommand($"SELECT COUNT(*) FROM dbo.ExtractorProfiles WHERE Id IN ('{platformRow.Id}', '{privateA.Id}')", raw);
        (await count.ExecuteScalarAsync(ct)).ShouldBe(1);
    }

    private static ExtractorProfile NewProfile(Guid? tenantId, string documentType) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        DocumentType = documentType,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), _connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }

    private static async Task Seed(DocProcDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (tenantId, slug, number) in new[] { (TenantA, "tenant-a", "A-1"), (TenantB, "tenant-b", "B-1") })
        {
            var channel = new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "api", Type = ChannelType.ApiUpload, CreatedAt = now };
            var blob = new BlobReference { Container = "raw", Key = $"{tenantId}/{number}", Sha256 = new string('0', 64), SizeBytes = 1, ContentType = "application/pdf" };
            var ingestion = new Ingestion { Id = Guid.CreateVersion7(), TenantId = tenantId, ChannelId = channel.Id, ReceivedAt = now, RawBlob = blob, UpdatedAt = now };
            var item = new IngestionItem { Id = Guid.CreateVersion7(), TenantId = tenantId, IngestionId = ingestion.Id, FileName = "a.pdf", MimeType = "application/pdf", Sha256 = new string('0', 64), Blob = blob, Kind = ItemKind.Pdf, Disposition = ItemDisposition.Accepted, CreatedAt = now };
            context.Tenants.Add(new Tenant { Id = tenantId, Name = slug, Slug = slug, LegalName = slug, DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
            context.IngestionChannels.Add(channel);
            context.Ingestions.Add(ingestion);
            context.IngestionItems.Add(item);
            context.Documents.Add(new Document { Id = Guid.CreateVersion7(), TenantId = tenantId, IngestionItemId = item.Id, DocumentNumber = number, DocumentNumberNormalized = number, CreatedAt = now, UpdatedAt = now });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
