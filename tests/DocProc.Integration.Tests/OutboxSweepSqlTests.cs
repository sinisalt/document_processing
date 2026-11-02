using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.MsSql;

namespace DocProc.Integration.Tests;

/// <summary>
/// The outbox half of P0 (docs/11 §13.5, §21 step 7) on SQL Server: the <c>OutboxFannedOut</c> migration, run on a
/// database the previous build left behind and the way the compose stack runs it (anonymous context), marks every
/// existing row as handed on, so the sweeper never publishes history again; the sweeper's query translates and
/// finds, across tenants and behind row-level security, exactly the settlements that were never handed on; and a
/// tenant session can claim its own row and cannot see another tenant's.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OutboxSweepSqlTests : IAsyncLifetime
{
    private const string MigrationBeforeOutbox = "20261102175238_P0PersistenceSeams";

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
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Old_rows_count_as_published_and_the_sweeper_finds_only_lost_settlements()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var old = now.AddMinutes(-30);

        // The previous schema, with a document the previous build published (its outbox row has no FannedOutAt yet).
        Guid published;
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            await platform.GetService<IMigrator>().MigrateAsync(MigrationBeforeOutbox, ct);
            await SeedTenantsAsync(platform, now, ct);
            published = await AddDocumentAsync(platform, TenantA, DocumentStatus.Approved, old, ct);
            await platform.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO [OutboxMessages] ([Id], [TenantId], [DocumentId], [EventType], [PayloadJson], [CreatedAt])
                VALUES ({Guid.CreateVersion7()}, {TenantA}, {published}, N'document.approved', {"{}"}, {old.AddSeconds(5)});
                """,
                ct);
        }

        await using (var anonymous = NewContext(TenantContext.Anonymous()))
        {
            await anonymous.Database.MigrateAsync(ct);
            (await anonymous.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var row = await asA.OutboxMessages.AsNoTracking().SingleAsync(ct);
            row.FannedOutAt.ShouldBe(row.CreatedAt, "rows of the previous build were published when they were written");
        }

        // Decisions of the new build: one whose publish message was lost, one from before the decision wrote its row
        // (tenant B), one inside the grace, one not settled.
        Guid lost, unrowed;
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            lost = await AddDocumentAsync(platform, TenantA, DocumentStatus.Approved, old, ct);
            DocumentOutbox.Add(platform, await platform.Documents.SingleAsync(d => d.Id == lost, ct), old);
            unrowed = await AddDocumentAsync(platform, TenantB, DocumentStatus.Rejected, old, ct);
            var recent = await AddDocumentAsync(platform, TenantA, DocumentStatus.AutoApproved, now.AddSeconds(-30), ct);
            DocumentOutbox.Add(platform, await platform.Documents.SingleAsync(d => d.Id == recent, ct), now.AddSeconds(-30));
            await AddDocumentAsync(platform, TenantB, DocumentStatus.NeedsReview, old, ct);
            await platform.SaveChangesAsync(ct);

            var due = await DocumentOutbox.Unpublished(platform, now.AddMinutes(-2), now.AddDays(-7)).ToListAsync(ct);
            due.Select(d => (d.DocumentId, d.TenantId, d.Status)).ShouldBe([(lost, TenantA, DocumentStatus.Approved), (unrowed, TenantB, DocumentStatus.Rejected)], ignoreOrder: true);
            due.ShouldAllBe(d => d.SettledAt == old);
        }

        // The publish stage claims the row in the tenant's own session; another tenant cannot see it.
        await using (var asB = NewContext(TenantContext.ForTenant(TenantB)))
        {
            (await asB.OutboxMessages.AsNoTracking().CountAsync(m => m.DocumentId == lost, ct)).ShouldBe(0);
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var row = await asA.OutboxMessages.SingleAsync(m => m.DocumentId == lost, ct);
            row.FannedOutAt = now;
            await asA.SaveChangesAsync(ct);
        }

        await using (var platform = NewContext(TenantContext.Platform()))
        {
            var due = await DocumentOutbox.Unpublished(platform, now.AddMinutes(-2), now.AddDays(-7)).Select(d => d.DocumentId).ToListAsync(ct);
            due.ShouldBe([unrowed]);
        }
    }

    private static async Task SeedTenantsAsync(DocProcDbContext platform, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (id, slug) in new[] { (TenantA, "tenant-a"), (TenantB, "tenant-b") })
        {
            var channel = Guid.CreateVersion7();
            await platform.Database.ExecuteSqlAsync(
                $$"""
                INSERT INTO [Tenants] ([Id], [Name], [Slug], [LegalName], [NameAliases], [Status], [DefaultCurrency], [CountryCode], [TimeZone], [CreatedAt], [UpdatedAt], [Settings])
                VALUES ({{id}}, {{slug}}, {{slug}}, {{slug}}, N'[]', N'Active', N'EUR', N'DE', N'Europe/Berlin', {{now}}, {{now}},
                        N'{"AlwaysReviewSupplierIds":[],"AutoApproveConfidenceThreshold":0.7,"RetentionYears":8,"ShareExtractorProfiles":false}');
                INSERT INTO [IngestionChannels] ([Id], [TenantId], [Name], [Type], [ConfigJson], [Status], [CreatedAt])
                VALUES ({{channel}}, {{id}}, N'api', N'ApiUpload', N'{}', N'Active', {{now}});
                INSERT INTO [Ingestions] ([Id], [TenantId], [ChannelId], [ReceivedAt], [Status], [Attempts], [UpdatedAt],
                                          [RawBlob_Container], [RawBlob_ContentType], [RawBlob_Key], [RawBlob_Sha256], [RawBlob_SizeBytes])
                VALUES ({{IngestionOf(id)}}, {{id}}, {{channel}}, {{now}}, N'Processed', 1, {{now}}, N'raw', N'application/pdf', N'k', REPLICATE(N'0', 64), 1);
                """,
                ct);
        }
    }

    private static Guid IngestionOf(Guid tenantId) => tenantId == TenantA ? Guid.Parse("00000000-0000-0000-0000-00000000b00a") : Guid.Parse("00000000-0000-0000-0000-00000000b00b");

    /// <summary>A document of the tenant's ingestion in <paramref name="status"/>, settled (or last changed) at <paramref name="at"/>.</summary>
    private static async Task<Guid> AddDocumentAsync(DocProcDbContext platform, Guid tenantId, DocumentStatus status, DateTimeOffset at, CancellationToken ct)
    {
        var sha = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var item = new IngestionItem
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            IngestionId = IngestionOf(tenantId),
            FileName = "invoice.pdf",
            MimeType = "application/pdf",
            SizeBytes = 1,
            Sha256 = sha,
            Blob = new BlobReference { Container = "raw", Key = sha, Sha256 = sha, SizeBytes = 1, ContentType = "application/pdf" },
            Kind = ItemKind.Pdf,
            Disposition = ItemDisposition.Accepted,
            CreatedAt = at,
        };
        var document = new Document
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            IngestionItemId = item.Id,
            Status = status,
            ApprovedAt = status is DocumentStatus.Approved or DocumentStatus.AutoApproved ? at : null,
            CreatedAt = at,
            UpdatedAt = at,
        };
        platform.IngestionItems.Add(item);
        platform.Documents.Add(document);
        await platform.SaveChangesAsync(ct);
        return document.Id;
    }

    private DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), _connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }
}
