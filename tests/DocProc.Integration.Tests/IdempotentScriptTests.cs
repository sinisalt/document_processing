using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Infrastructure.Configuration;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.MsSql;

namespace DocProc.Integration.Tests;

/// <summary>
/// Both ways a database is migrated (README "Database"), on a database the build before the correction-value fix left
/// behind, with rows behind the tenant policy:
/// <list type="bullet">
/// <item>the release path, <c>dotnet ef migrations script --idempotent</c> run batch by batch as sqlcmd runs it, twice;
/// EF puts a whole migration into one batch and SQL Server compiles a batch before running any of it, so a data
/// statement over a column its own migration adds (or one an applied migration already renamed) must run through
/// EXEC;</item>
/// <item>the compose path, <c>MigrationHostedService</c> with an anonymous tenant context, where a copy or back-fill
/// that does not open the policy sees no rows.</item>
/// </list>
/// Either way the data survives and every back-fill reaches the rows. The script must also build a database from
/// nothing, twice.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IdempotentScriptTests : IAsyncLifetime
{
    private const string MigrationBeforeCorrectionFix = "20261031024444_AddFlagParams";

    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Channel = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    private static readonly Guid IngestionId = Guid.Parse("00000000-0000-0000-0000-00000000b001");
    private static readonly Guid ItemId = Guid.Parse("00000000-0000-0000-0000-00000000c001");
    private static readonly Guid DocumentId = Guid.Parse("00000000-0000-0000-0000-00000000d001");
    private static readonly Guid OutboxId = Guid.Parse("00000000-0000-0000-0000-00000000e001");
    private static readonly DateTimeOffset At = new(2026, 10, 31, 9, 30, 0, TimeSpan.Zero);

    private const string OldValue = """{"amount":1.00,"currency":"EUR"}""";
    private const string NewValue = "[1,2]";

    private MsSqlContainer? _container;

    public async ValueTask InitializeAsync()
    {
        if (!DockerFacts.IsAvailable)
        {
            return;
        }

        _container = await SqlServerContainers.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Both_migration_paths_keep_the_data_and_reach_the_rows_behind_the_policy()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        string script;
        await using (var any = NewContext(await CreateDatabaseAsync("scripted", ct), TenantContext.Platform()))
        {
            script = any.GetService<IMigrator>().GenerateScript(fromMigration: null, toMigration: null, MigrationsSqlGenerationOptions.Idempotent);
        }

        // The release path, on the database the earlier build left behind, then again on the migrated one.
        var scripted = ConnectionString("scripted");
        await SeedPreviousBuildAsync(scripted, ct);
        await RunScriptAsync(scripted, script, ct);
        await AssertMigratedAsync(scripted, ct);
        await RunScriptAsync(scripted, script, ct);
        await AssertMigratedAsync(scripted, ct);

        // The compose path, on the same starting point.
        var hosted = await CreateDatabaseAsync("hosted", ct);
        await SeedPreviousBuildAsync(hosted, ct);
        await using (var anonymous = NewContext(hosted, TenantContext.Anonymous()))
        {
            await anonymous.Database.MigrateAsync(ct);
        }

        await AssertMigratedAsync(hosted, ct);

        // The script builds an empty database, and a second run changes nothing.
        var empty = await CreateDatabaseAsync("empty", ct);
        await RunScriptAsync(empty, script, ct);
        await RunScriptAsync(empty, script, ct);
        await using var fresh = NewContext(empty, TenantContext.Platform());
        (await fresh.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();
        (await fresh.Documents.CountAsync(ct)).ShouldBe(0);
    }

    /// <summary>The schema of <see cref="MigrationBeforeCorrectionFix"/> with one row of each kind, written in platform scope as that build wrote them.</summary>
    private static async Task SeedPreviousBuildAsync(string connectionString, CancellationToken ct)
    {
        await using var platform = NewContext(connectionString, TenantContext.Platform());
        await platform.GetService<IMigrator>().MigrateAsync(MigrationBeforeCorrectionFix, ct);
        await platform.Database.ExecuteSqlAsync(
            $$"""
            INSERT INTO [Tenants] ([Id], [Name], [Slug], [LegalName], [NameAliases], [Status], [DefaultCurrency], [CountryCode], [TimeZone], [CreatedAt], [UpdatedAt], [Settings])
            VALUES ({{TenantA}}, N'tenant-a', N'tenant-a', N'tenant-a', N'[]', N'Active', N'EUR', N'DE', N'Europe/Berlin', {{At}}, {{At}},
                    N'{"AlwaysReviewSupplierIds":[],"AutoApproveConfidenceThreshold":0.7,"RetentionYears":8,"ShareExtractorProfiles":false}');
            INSERT INTO [IngestionChannels] ([Id], [TenantId], [Name], [Type], [ConfigJson], [Status], [CreatedAt])
            VALUES ({{Channel}}, {{TenantA}}, N'api', N'ApiUpload', N'{}', N'Active', {{At}});
            INSERT INTO [Ingestions] ([Id], [TenantId], [ChannelId], [ReceivedAt], [SourceFrom], [Status], [Attempts], [UpdatedAt],
                                      [RawBlob_Container], [RawBlob_ContentType], [RawBlob_Key], [RawBlob_Sha256], [RawBlob_SizeBytes])
            VALUES ({{IngestionId}}, {{TenantA}}, {{Channel}}, {{At}}, N'reviewer-a', N'Processed', 1, {{At}}, N'raw', N'application/pdf', N'k1', REPLICATE(N'0', 64), 1);
            INSERT INTO [IngestionItems] ([Id], [TenantId], [IngestionId], [FileName], [MimeType], [SizeBytes], [Sha256], [Kind], [Disposition], [CreatedAt],
                                          [Blob_Container], [Blob_ContentType], [Blob_Key], [Blob_Sha256], [Blob_SizeBytes])
            VALUES ({{ItemId}}, {{TenantA}}, {{IngestionId}}, N'invoice.pdf', N'application/pdf', 1, REPLICATE(N'0', 64), N'Pdf', N'Accepted', {{At}},
                    N'raw', N'application/pdf', N'k1', REPLICATE(N'0', 64), 1);
            INSERT INTO [Documents] ([Id], [TenantId], [IngestionItemId], [PageStart], [PageEnd], [PageCount], [PageImageKeys], [Type], [Status], [CreatedAt], [UpdatedAt], [ApprovedAt], [Costs])
            VALUES ({{DocumentId}}, {{TenantA}}, {{ItemId}}, 1, 1, 1, N'[]', N'Invoice', N'Approved', {{At}}, {{At}}, {{At}}, N'{}');
            INSERT INTO [Corrections] ([TenantId], [DocumentId], [FieldPath], [OldValueJson], [NewValueJson], [By], [At])
            VALUES ({{TenantA}}, {{DocumentId}}, N'totalGross', {{OldValue}}, {{NewValue}}, N'reviewer-a', {{At}});
            INSERT INTO [OutboxMessages] ([Id], [TenantId], [DocumentId], [EventType], [PayloadJson], [CreatedAt])
            VALUES ({{OutboxId}}, {{TenantA}}, {{DocumentId}}, {{DocumentEventTypes.Approved}}, N'{}', {{At}});
            """,
            ct);
    }

    private static async Task AssertMigratedAsync(string connectionString, CancellationToken ct)
    {
        await using var asA = NewContext(connectionString, TenantContext.ForTenant(TenantA));
        (await asA.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();

        // CorrectionValuesAsText copied the values; before the fix both paths lost them (or did not compile).
        var correction = await asA.Corrections.AsNoTracking().SingleAsync(ct);
        correction.OldValueJson.ShouldBe(OldValue);
        correction.NewValueJson.ShouldBe(NewValue);
        correction.Op.ShouldBe("set");

        // P0PersistenceSeams back-filled the upload's actor and the flags object.
        var ingestion = await asA.Ingestions.AsNoTracking().SingleAsync(ct);
        ingestion.SubmittedBy.ShouldBe("reviewer-a");
        ingestion.SourceSender.ShouldBeNull();
        (await new EfTenantFeatures(asA).GetAsync(TenantA, ct)).ShouldBeSameAs(TenantFeatureSet.None);
        await using (var raw = new SqlConnection(connectionString))
        {
            await raw.OpenAsync(ct);
            await using var flags = new SqlCommand($"EXEC sp_set_session_context N'{TenantSessionContextInterceptor.ScopeKey}', N'{TenantSessionContextInterceptor.PlatformScopeValue}'; SELECT JSON_QUERY(CAST([Settings] AS nvarchar(max)), N'$.Features') FROM [Tenants]", raw);
            ((string?)await flags.ExecuteScalarAsync(ct)).ShouldNotBeNull("the flags object is stored, so switching one on is an in-place update");
        }

        // OutboxFannedOut marked the existing row as published when it was written.
        var outbox = await asA.OutboxMessages.AsNoTracking().SingleAsync(ct);
        outbox.FannedOutAt.ShouldBe(At);

        // TenantPolicyHistoryAndMoves is in force.
        (await asA.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_predicates WHERE [target_object_id] IN (OBJECT_ID(N'dbo.DocumentsHistory'), OBJECT_ID(N'dbo.ExtractorProfilesHistory'))").SingleAsync(ct))
            .ShouldBe(2);
    }

    private static async Task RunScriptAsync(string connectionString, string script, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var batch = new System.Text.StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            if (line.TrimEnd('\r').Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                await ExecuteAsync(connection, batch.ToString(), ct);
                batch.Clear();
            }
            else
            {
                batch.Append(line).Append('\n');
            }
        }

        await ExecuteAsync(connection, batch.ToString(), ct);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string batch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(batch))
        {
            return;
        }

        await using var command = new SqlCommand(batch, connection) { CommandTimeout = 300 };
        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException exception)
        {
            throw new InvalidOperationException($"The idempotent script failed ({exception.Message}) in the batch:\n{batch}", exception);
        }
    }

    private async Task<string> CreateDatabaseAsync(string name, CancellationToken ct)
    {
        await using (var master = new SqlConnection(_container!.GetConnectionString()))
        {
            await master.OpenAsync(ct);
            await using var create = new SqlCommand($"CREATE DATABASE [{name}]", master);
            await create.ExecuteNonQueryAsync(ct);
        }

        return ConnectionString(name);
    }

    private string ConnectionString(string database) =>
        new SqlConnectionStringBuilder(_container!.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    private static DocProcDbContext NewContext(string connectionString, ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }
}
