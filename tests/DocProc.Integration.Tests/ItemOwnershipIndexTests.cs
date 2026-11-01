using System.Text.RegularExpressions;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace DocProc.Integration.Tests;

/// <summary>
/// The migrations on SQL Server 2025 create the filtered unique index behind duplicate ownership (docs/03
/// "Implementation"), and it rejects exactly the second <c>Accepted</c> item per tenant and SHA-256.
/// </summary>
public sealed partial class ItemOwnershipIndexTests : IAsyncLifetime
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
    public async Task The_migrations_create_the_filtered_unique_ownership_index()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT is_unique, has_filter, filter_definition FROM sys.indexes WHERE name = @name AND object_id = OBJECT_ID(N'dbo.IngestionItems')",
            connection);
        command.Parameters.AddWithValue("@name", IngestionItemOwnership.IndexName);
        await using var reader = await command.ExecuteReaderAsync(ct);

        (await reader.ReadAsync(ct)).ShouldBeTrue($"index {IngestionItemOwnership.IndexName} exists on IngestionItems");
        reader.GetBoolean(0).ShouldBeTrue("the index is unique");
        reader.GetBoolean(1).ShouldBeTrue("the index is filtered");
        // SQL Server normalises the filter text (parentheses, spacing); the predicate itself must be the one the model declares.
        FilterShape().IsMatch(reader.GetString(2)).ShouldBeTrue(reader.GetString(2));
        (await reader.ReadAsync(ct)).ShouldBeFalse("one index with that name");
    }

    [Fact]
    public async Task A_second_accepted_item_per_tenant_and_hash_is_rejected_and_recognised()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var sha = new string('1', 64);

        await using var asA = NewContext(TenantContext.ForTenant(TenantA));
        var ingestionA = await asA.Ingestions.SingleAsync(ct);
        asA.IngestionItems.Add(NewItem(TenantA, ingestionA.Id, sha, ItemDisposition.Accepted));
        await asA.SaveChangesAsync(ct);

        asA.IngestionItems.Add(NewItem(TenantA, ingestionA.Id, sha, ItemDisposition.Accepted));
        var violation = await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
        IngestionItemOwnership.IsViolation(violation).ShouldBeTrue(violation.InnerException?.Message);
        asA.ChangeTracker.Clear();

        // The same bytes may be a candidate or a duplicate any number of times, and another tenant's owner is another tenant's.
        asA.IngestionItems.Add(NewItem(TenantA, ingestionA.Id, sha, ItemDisposition.Candidate));
        asA.IngestionItems.Add(NewItem(TenantA, ingestionA.Id, sha, ItemDisposition.Duplicate));
        await asA.SaveChangesAsync(ct);

        await using var asB = NewContext(TenantContext.ForTenant(TenantB));
        var ingestionB = await asB.Ingestions.SingleAsync(ct);
        asB.IngestionItems.Add(NewItem(TenantB, ingestionB.Id, sha, ItemDisposition.Accepted));
        await asB.SaveChangesAsync(ct);

        await using var platform = NewContext(TenantContext.Platform());
        (await platform.IngestionItems.CountAsync(i => i.Sha256 == sha && i.Disposition == ItemDisposition.Accepted, ct)).ShouldBe(2, "one owner per tenant");
    }

    [GeneratedRegex(@"^\(?\[Disposition\]\s*=\s*N?'Accepted'\)?$")]
    private static partial Regex FilterShape();

    private static IngestionItem NewItem(Guid tenantId, Guid ingestionId, string sha, ItemDisposition disposition) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        IngestionId = ingestionId,
        FileName = $"{disposition}.pdf",
        MimeType = "application/pdf",
        SizeBytes = 1,
        Sha256 = sha,
        Blob = new BlobReference { Container = "raw", Key = $"{tenantId}/{sha}", Sha256 = sha, SizeBytes = 1, ContentType = "application/pdf" },
        Kind = ItemKind.Pdf,
        Disposition = disposition,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), _connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }

    private static async Task Seed(DocProcDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (tenantId, slug) in new[] { (TenantA, "tenant-a"), (TenantB, "tenant-b") })
        {
            var channel = new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "api", Type = ChannelType.ApiUpload, CreatedAt = now };
            var blob = new BlobReference { Container = "raw", Key = $"{tenantId}/raw", Sha256 = new string('0', 64), SizeBytes = 1, ContentType = "application/pdf" };
            context.Tenants.Add(new Tenant { Id = tenantId, Name = slug, Slug = slug, LegalName = slug, DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
            context.IngestionChannels.Add(channel);
            context.Ingestions.Add(new Ingestion { Id = Guid.CreateVersion7(), TenantId = tenantId, ChannelId = channel.Id, ReceivedAt = now, RawBlob = blob, UpdatedAt = now });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
