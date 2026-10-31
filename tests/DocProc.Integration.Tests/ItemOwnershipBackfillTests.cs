using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.MsSql;

namespace DocProc.Integration.Tests;

/// <summary>
/// The <c>AddItemOwnershipIndex</c> back-fill runs behind the row-level security policy. Neither migration path sets a
/// tenant (the release script runs through sqlcmd, <c>MigrationHostedService</c> with an anonymous context), so the
/// migration opens platform scope for its data statements itself; without that the UPDATE silently touches no rows.
/// The shape of those statements is checked without a database by <see cref="ItemOwnershipBackfillSqlTests"/>.
/// </summary>
public sealed class ItemOwnershipBackfillTests : IAsyncLifetime
{
    private const string MigrationBeforeOwnership = "20261030183519_AddConcurrencyTokens";

    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

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
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_back_fill_marks_existing_owners_accepted_when_migrating_without_a_tenant()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        // A database as the previous build left it: an item that owns a document but is still Candidate.
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            await platform.GetService<IMigrator>().MigrateAsync(MigrationBeforeOwnership, ct);
            await SeedTenantAsync(platform, ct);
        }

        Guid itemId;
        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var ingestion = await asA.Ingestions.SingleAsync(ct);
            var sha = new string('a', 64);
            var item = new IngestionItem
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                IngestionId = ingestion.Id,
                FileName = "invoice.pdf",
                MimeType = "application/pdf",
                SizeBytes = 1,
                Sha256 = sha,
                Blob = new BlobReference { Container = "raw", Key = $"{TenantA}/{sha}", Sha256 = sha, SizeBytes = 1, ContentType = "application/pdf" },
                Kind = ItemKind.Pdf,
                Disposition = ItemDisposition.Candidate,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            asA.IngestionItems.Add(item);
            asA.Documents.Add(new Document { Id = Guid.CreateVersion7(), TenantId = TenantA, IngestionItemId = item.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await asA.SaveChangesAsync(ct);
            itemId = item.Id;
        }

        // The remaining migration through an anonymous context, the way MigrationHostedService runs it.
        await using (var anonymous = NewContext(TenantContext.Anonymous()))
        {
            await anonymous.Database.MigrateAsync(ct);
        }

        await using var check = NewContext(TenantContext.Platform());
        (await check.IngestionItems.AsNoTracking().SingleAsync(i => i.Id == itemId, ct)).Disposition.ShouldBe(ItemDisposition.Accepted);
    }

    private DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), _connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }

    private static async Task SeedTenantAsync(DocProcDbContext context, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var channel = new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = TenantA, Name = "api", Type = ChannelType.ApiUpload, CreatedAt = now };
        var blob = new BlobReference { Container = "raw", Key = $"{TenantA}/raw", Sha256 = new string('0', 64), SizeBytes = 1, ContentType = "application/pdf" };
        context.Tenants.Add(new Tenant { Id = TenantA, Name = "tenant-a", Slug = "tenant-a", LegalName = "tenant-a", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
        context.IngestionChannels.Add(channel);
        context.Ingestions.Add(new Ingestion { Id = Guid.CreateVersion7(), TenantId = TenantA, ChannelId = channel.Id, ReceivedAt = now, RawBlob = blob, UpdatedAt = now });
        await context.SaveChangesAsync(ct);
    }
}
