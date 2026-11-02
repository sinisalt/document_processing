using DocProc.Application.Configuration;
using DocProc.Infrastructure.Configuration;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.MsSql;

namespace DocProc.Integration.Tests;

/// <summary>
/// The <c>P0PersistenceSeams</c> migration on a database the previous build left behind, run the way the compose
/// stack runs it (<c>MigrationHostedService</c>, anonymous context): the ingestion actor back-fill reaches the rows
/// behind the row-level security policy, old corrections get the <c>set</c> operation, and tenant settings written
/// before the feature flags existed still load, with every flag off.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class P0SeamsMigrationTests : IAsyncLifetime
{
    private const string MigrationBeforeSeams = "20261102152408_CorrectionValuesAsText";

    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UploadChannel = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    private static readonly Guid MailChannel = Guid.Parse("00000000-0000-0000-0000-00000000a002");
    private static readonly Guid UploadIngestion = Guid.Parse("00000000-0000-0000-0000-00000000b001");
    private static readonly Guid MailIngestion = Guid.Parse("00000000-0000-0000-0000-00000000b002");

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
    public async Task The_seams_migrate_an_existing_database_without_a_tenant()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        // The previous schema, with rows written by the previous build. The current model has columns the old tables
        // lack, so the rows are written in SQL, in platform scope as the old build's pipeline would.
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            await platform.GetService<IMigrator>().MigrateAsync(MigrationBeforeSeams, ct);
            var now = DateTimeOffset.UtcNow;
            await platform.Database.ExecuteSqlAsync(
                $$"""
                INSERT INTO [Tenants] ([Id], [Name], [Slug], [LegalName], [NameAliases], [Status], [DefaultCurrency], [CountryCode], [TimeZone], [CreatedAt], [UpdatedAt], [Settings])
                VALUES ({{TenantA}}, N'tenant-a', N'tenant-a', N'tenant-a', N'[]', N'Active', N'EUR', N'DE', N'Europe/Berlin', {{now}}, {{now}},
                        N'{"AlwaysReviewSupplierIds":[],"AutoApproveConfidenceThreshold":0.7,"RetentionYears":8,"ShareExtractorProfiles":false}');
                INSERT INTO [IngestionChannels] ([Id], [TenantId], [Name], [Type], [ConfigJson], [Status], [CreatedAt])
                VALUES ({{UploadChannel}}, {{TenantA}}, N'api', N'ApiUpload', N'{}', N'Active', {{now}}),
                       ({{MailChannel}}, {{TenantA}}, N'mail', N'Mailbox', N'{}', N'Active', {{now}});
                INSERT INTO [Ingestions] ([Id], [TenantId], [ChannelId], [ReceivedAt], [SourceFrom], [Status], [Attempts], [UpdatedAt],
                                          [RawBlob_Container], [RawBlob_ContentType], [RawBlob_Key], [RawBlob_Sha256], [RawBlob_SizeBytes])
                VALUES ({{UploadIngestion}}, {{TenantA}}, {{UploadChannel}}, {{now}}, N'reviewer-a', N'Processed', 1, {{now}}, N'raw', N'application/pdf', N'k1', REPLICATE(N'0', 64), 1),
                       ({{MailIngestion}}, {{TenantA}}, {{MailChannel}}, {{now}}, N'Rechnung@Nordwind.example', N'Processed', 1, {{now}}, N'raw', N'application/pdf', N'k2', REPLICATE(N'1', 64), 1);
                """,
                ct);
        }

        // The rest through an anonymous context, as MigrationHostedService runs it.
        await using (var anonymous = NewContext(TenantContext.Anonymous()))
        {
            await anonymous.Database.MigrateAsync(ct);
            (await anonymous.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();
        }

        await using var asA = NewContext(TenantContext.ForTenant(TenantA));
        var upload = await asA.Ingestions.AsNoTracking().SingleAsync(i => i.Id == UploadIngestion, ct);
        upload.SubmittedBy.ShouldBe("reviewer-a");
        upload.SourceSender.ShouldBeNull();
        upload.SourceFrom.ShouldBe("reviewer-a");

        var mail = await asA.Ingestions.AsNoTracking().SingleAsync(i => i.Id == MailIngestion, ct);
        mail.SubmittedBy.ShouldBeNull();
        mail.SourceSender.ShouldBeNull();
        mail.SourceFrom.ShouldBe("Rechnung@Nordwind.example");

        // Settings stored before the flags existed load unchanged, and every flag reads as off.
        var tenant = await asA.Tenants.AsNoTracking().SingleAsync(ct);
        tenant.Settings.RetentionYears.ShouldBe(8);
        tenant.Settings.ShareExtractorProfiles.ShouldBeFalse();
        (await new EfTenantFeatures(asA).GetAsync(TenantA, ct)).ShouldBeSameAs(TenantFeatureSet.None);

        // The back-fill gave the stored settings the flags object, so switching one on is an in-place JSON update.
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            var tracked = await platform.Tenants.SingleAsync(ct);
            tracked.Settings.Features.ShouldNotBeNull("the migration back-fills the flags object").SourceRouting = true;
            await platform.SaveChangesAsync(ct);
        }

        await using var reread = NewContext(TenantContext.ForTenant(TenantA));
        (await new EfTenantFeatures(reread).GetAsync(TenantA, ct)).Enabled.ShouldBe([TenantFeature.SourceRouting]);
        (await reread.Tenants.AsNoTracking().SingleAsync(ct)).Settings.RetentionYears.ShouldBe(8);

        // New corrections default to the set operation; the platform-row policy is in force.
        var sha = new string('2', 64);
        var item = new IngestionItem
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantA,
            IngestionId = UploadIngestion,
            FileName = "invoice.pdf",
            MimeType = "application/pdf",
            SizeBytes = 1,
            Sha256 = sha,
            Blob = new BlobReference { Container = "raw", Key = "k3", Sha256 = sha, SizeBytes = 1, ContentType = "application/pdf" },
            Kind = ItemKind.Pdf,
            Disposition = ItemDisposition.Accepted,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var document = new Document { Id = Guid.CreateVersion7(), TenantId = TenantA, IngestionItemId = item.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        reread.IngestionItems.Add(item);
        reread.Documents.Add(document);
        reread.Corrections.Add(new Correction { TenantId = TenantA, DocumentId = document.Id, FieldPath = "dueDate", By = "a", At = DateTimeOffset.UtcNow, SourceJson = "{\"page\":1}" });
        await reread.SaveChangesAsync(ct);
        var correction = await reread.Corrections.AsNoTracking().SingleAsync(ct);
        correction.Op.ShouldBe("set");
        correction.SourceJson.ShouldBe("{\"page\":1}");
    }

    private DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), _connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }
}
