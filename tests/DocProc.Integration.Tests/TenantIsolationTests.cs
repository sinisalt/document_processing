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
            var item = new IngestionItem { Id = Guid.CreateVersion7(), TenantId = tenantId, IngestionId = ingestion.Id, FileName = "a.pdf", MimeType = "application/pdf", Sha256 = new string('0', 64), Blob = blob, Kind = ItemKind.Pdf, CreatedAt = now };
            context.Tenants.Add(new Tenant { Id = tenantId, Name = slug, Slug = slug, LegalName = slug, DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
            context.IngestionChannels.Add(channel);
            context.Ingestions.Add(ingestion);
            context.IngestionItems.Add(item);
            context.Documents.Add(new Document { Id = Guid.CreateVersion7(), TenantId = tenantId, IngestionItemId = item.Id, DocumentNumber = number, DocumentNumberNormalized = number, CreatedAt = now, UpdatedAt = now });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
