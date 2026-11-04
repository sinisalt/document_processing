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
[Collection(SqlServerCollection.Name)]
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

        _container = await SqlServerContainers.StartAsync(TestContext.Current.CancellationToken);
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
            // Only the id: the old schema lacks columns the current model maps (Ingestions.SubmittedBy and later ones).
            var ingestionId = await asA.Ingestions.Select(i => i.Id).SingleAsync(ct);
            var sha = new string('a', 64);
            var item = new { Id = Guid.CreateVersion7() };

            // The item in SQL as well: the old schema lacks IngestionItems.RouteJson (P4) the current model maps.
            await asA.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO [IngestionItems] ([Id], [TenantId], [IngestionId], [FileName], [MimeType], [SizeBytes], [Sha256], [Kind], [Disposition], [CreatedAt],
                                              [Blob_Container], [Blob_ContentType], [Blob_Key], [Blob_Sha256], [Blob_SizeBytes])
                VALUES ({item.Id}, {TenantA}, {ingestionId}, N'invoice.pdf', N'application/pdf', 1, {sha}, N'Pdf', N'Candidate', {DateTimeOffset.UtcNow},
                        N'raw', N'application/pdf', {TenantA + "/" + sha}, {sha}, 1);
                """,
                ct);
            // The document in SQL: the old Documents table lacks columns the current model maps (DocumentTypeVersionId).
            var now = DateTimeOffset.UtcNow;
            await asA.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO [Documents] ([Id], [TenantId], [IngestionItemId], [PageStart], [PageEnd], [PageCount], [PageImageKeys], [Type], [Status], [CreatedAt], [UpdatedAt], [Costs])
                VALUES ({Guid.CreateVersion7()}, {TenantA}, {item.Id}, 1, 1, 1, N'[]', N'Unknown', N'Received', {now}, {now}, {"{}"});
                """,
                ct);
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
        await context.SaveChangesAsync(ct);

        // The channel in SQL: the older schema lacks the channel columns of P4 (SourceKey, the defaults).
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO [IngestionChannels] ([Id], [TenantId], [Name], [Type], [ConfigJson], [Status], [CreatedAt])
            VALUES ({channel.Id}, {TenantA}, {channel.Name}, N'ApiUpload', {"{}"}, N'Active', {now});
            """,
            ct);

        // In SQL: the current model maps Ingestions columns this older schema does not have yet.
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO [Ingestions] ([Id], [TenantId], [ChannelId], [ReceivedAt], [Status], [Attempts], [UpdatedAt],
                                      [RawBlob_Container], [RawBlob_ContentType], [RawBlob_Key], [RawBlob_Sha256], [RawBlob_SizeBytes])
            VALUES ({Guid.CreateVersion7()}, {TenantA}, {channel.Id}, {now}, N'Received', 0, {now}, {blob.Container}, {blob.ContentType}, {blob.Key}, {blob.Sha256}, {blob.SizeBytes});
            """,
            ct);
    }
}
