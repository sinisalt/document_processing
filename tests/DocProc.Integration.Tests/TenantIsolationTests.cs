using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Integration.Tests;

/// <summary>
/// Applies the migrations to SQL Server 2025 and proves both tenancy layers (docs/01 "Multi-tenancy"): the EF query
/// filter, and row-level security that holds even for raw SQL that bypasses EF.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class TenantIsolationTests(SqlServerFixture sqlServer) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private string _connectionString = string.Empty;

    // The container is shared by the class; each test migrates and seeds a database of its own.
    public async ValueTask InitializeAsync()
    {
        if (!DockerFacts.IsAvailable)
        {
            return;
        }

        _connectionString = sqlServer.NewDatabaseConnectionString();

        await using var context = NewContext(TenantContext.Platform());
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await Seed(context);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
    public async Task The_field_index_is_hidden_from_and_closed_to_other_tenants_on_the_database()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        // docs/11 §6.2: DocumentFieldIndex is a strict tenant table (PolicyV5).
        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var mine = await asA.Documents.Select(d => d.Id).FirstAsync(ct);
            asA.DocumentFieldIndex.Add(new DocumentFieldIndexEntry { TenantId = TenantA, DocumentId = mine, FieldKey = "supplierCustomerRef", Ordinal = 0, StringValue = "123456789" });
            await asA.SaveChangesAsync(ct);
            (await asA.DocumentFieldIndex.CountAsync(ct)).ShouldBe(1);
        }

        await using var asB = NewContext(TenantContext.ForTenant(TenantB));
        (await asB.DocumentFieldIndex.IgnoreQueryFilters().CountAsync(ct)).ShouldBe(0);
        var theirs = await asB.Documents.Select(d => d.Id).FirstAsync(ct);
        asB.DocumentFieldIndex.Add(new DocumentFieldIndexEntry { TenantId = TenantA, DocumentId = theirs, FieldKey = "supplierCustomerRef", Ordinal = 0, StringValue = "forged" });
        await Should.ThrowAsync<DbUpdateException>(() => asB.SaveChangesAsync(ct));
    }

    /// <summary>
    /// The document list's <c>?field.&lt;key&gt;=value</c> filter on SQL Server (docs/11 §18): a value that parses as a
    /// number too large for the decimal(28,8) index column is compared as text only, so the query answers (an empty page,
    /// or the document whose text value it is) instead of throwing OverflowException or ArgumentException, which the list
    /// endpoint answered with 500. An indexed number still matches by value.
    /// </summary>
    [Theory]
    [InlineData("99999999999999999999999")]
    [InlineData("123456789012345678901.5")]
    [InlineData("-99999999999999999999.5")]
    public async Task A_field_filter_with_a_number_the_index_cannot_hold_compares_as_text_and_does_not_fail(string raw)
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using var asA = NewContext(TenantContext.ForTenant(TenantA));
        var mine = await asA.Documents.Select(d => d.Id).FirstAsync(ct);
        asA.DocumentFieldIndex.Add(new DocumentFieldIndexEntry { TenantId = TenantA, DocumentId = mine, FieldKey = "amount", Ordinal = 0, NumberValue = 12.5m });
        asA.DocumentFieldIndex.Add(new DocumentFieldIndexEntry { TenantId = TenantA, DocumentId = mine, FieldKey = "reference", Ordinal = 0, StringValue = DocumentFieldIndexWriter.Normalize(raw) });
        await asA.SaveChangesAsync(ct);

        var filter = DocumentFieldFilter.Of("amount", raw);
        filter.Number.ShouldBeNull("it is out of the index's range");
        (await filter.Apply(asA.Documents).CountAsync(ct)).ShouldBe(0);
        (await DocumentFieldFilter.Of("reference", raw).Apply(asA.Documents).Select(d => d.Id).ToListAsync(ct)).ShouldBe([mine], "a long identifier still matches as text");

        (await DocumentFieldFilter.Of("amount", "12.50").Apply(asA.Documents).Select(d => d.Id).ToListAsync(ct)).ShouldBe([mine], "an indexed number matches by value");
        DocumentFieldFilter.Of("amount", "99999999999999999.5").Number.ShouldBe(99999999999999999.5m, "below the bound it is a number");
        (await DocumentFieldFilter.Of("amount", "99999999999999999.5").Apply(asA.Documents).CountAsync(ct)).ShouldBe(0, "and the column holds it as a parameter");
        DocumentFieldFilter.Of("amount", "100000000000000000").Number.ShouldBeNull("the bound itself is outside");
        (await DocumentFieldFilter.Of("amount", "0.123456789012").Apply(asA.Documents).CountAsync(ct)).ShouldBe(0, "more decimals than the column are fine");
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

    /// <summary>
    /// SQL Server does not apply a table's predicates to its history table: without predicates of their own, a tenant
    /// session read every tenant's history through <c>FOR SYSTEM_TIME</c> or the history table itself.
    /// </summary>
    [Fact]
    public async Task History_tables_show_only_the_sessions_own_and_platform_rows()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        var platformRow = NewProfile(tenantId: null, "platform");
        var privateA = NewProfile(TenantA, "private-a");
        var privateB = NewProfile(TenantB, "private-b");
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            platform.ExtractorProfiles.AddRange(platformRow, privateA, privateB);
            await platform.SaveChangesAsync(ct);

            // Every row changes once, so every table has history of both tenants.
            foreach (var document in await platform.Documents.ToListAsync(ct))
            {
                document.Status = DocumentStatus.Normalized;
            }

            foreach (var profile in await platform.ExtractorProfiles.ToListAsync(ct))
            {
                profile.Attempts = 1;
            }

            await platform.SaveChangesAsync(ct);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.DocumentsHistory").SingleAsync(ct)).ShouldBe(2);
            (await platform.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.ExtractorProfilesHistory").SingleAsync(ct)).ShouldBe(3);
        }

        await using var asA = NewContext(TenantContext.ForTenant(TenantA));
        (await asA.Database.SqlQuery<string>($"SELECT DocumentNumber AS [Value] FROM dbo.DocumentsHistory").ToListAsync(ct)).ShouldBe(["A-1"]);
        (await asA.Database.SqlQuery<string>($"SELECT DocumentNumber AS [Value] FROM dbo.Documents FOR SYSTEM_TIME ALL").ToListAsync(ct)).ShouldBe(["A-1", "A-1"]);
        (await asA.Documents.TemporalAll().IgnoreQueryFilters().Select(d => d.DocumentNumber).ToListAsync(ct)).ShouldBe(["A-1", "A-1"]);
        (await asA.Database.SqlQuery<string>($"SELECT DocumentType AS [Value] FROM dbo.ExtractorProfilesHistory").ToListAsync(ct))
            .ShouldBe(["platform", "private-a"], ignoreOrder: true);
        (await asA.Database.SqlQuery<string>($"SELECT DocumentType AS [Value] FROM dbo.ExtractorProfiles FOR SYSTEM_TIME ALL").ToListAsync(ct))
            .ShouldBe(["platform", "platform", "private-a", "private-a"], ignoreOrder: true);

        // No session context: the platform rows' history only.
        await using var raw = new SqlConnection(_connectionString);
        await raw.OpenAsync(ct);
        await using (var documents = new SqlCommand("SELECT COUNT(*) FROM dbo.Documents FOR SYSTEM_TIME ALL", raw))
        {
            (await documents.ExecuteScalarAsync(ct)).ShouldBe(0);
        }

        await using var profiles = new SqlCommand("SELECT COUNT(*) FROM dbo.ExtractorProfilesHistory", raw);
        (await profiles.ExecuteScalarAsync(ct)).ShouldBe(1);
    }

    /// <summary>
    /// The history predicates are schema-bound like every other, and must not make a later migration's column change to
    /// a temporal table harder than the table's own predicate already does: each step of a plain ADD and of the
    /// versioning-off form (both tables altered, then versioning back on) succeeds or fails alike under the P0 policy
    /// and under the one with the history predicates. Under both, a plain ADD or DROP COLUMN on the current table passes
    /// (SQL Server carries it to the history table), while turning versioning off is refused (error 3729, the table is
    /// referenced by the schema-bound policy): a migration that needs it drops the policy first and re-creates it after.
    /// </summary>
    [Fact]
    public async Task The_history_predicates_add_no_restriction_on_column_changes_to_a_temporal_table()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        string[] steps =
        [
            "ALTER TABLE [Documents] ADD [Probe{0}A] int NULL",
            "ALTER TABLE [Documents] SET (SYSTEM_VERSIONING = OFF)",
            "ALTER TABLE [Documents] ADD [Probe{0}B] nvarchar(10) NULL",
            "ALTER TABLE [DocumentsHistory] ADD [Probe{0}B] nvarchar(10) NULL",
            "ALTER TABLE [Documents] DROP COLUMN [Probe{0}A]",
            "ALTER TABLE [DocumentsHistory] DROP COLUMN [Probe{0}A]",
            "ALTER TABLE [Documents] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[DocumentsHistory]))",
        ];

        async Task<List<string>> RunAsync(PolicyDefinition policy, string tag)
        {
            await using var platform = NewContext(TenantContext.Platform());
            await platform.Database.ExecuteSqlRawAsync(RowLevelSecurity.DropSql(), ct);
            await platform.Database.ExecuteSqlRawAsync(RowLevelSecurity.CreateSql(policy), ct);
            var outcomes = new List<string>();
            foreach (var step in steps)
            {
                try
                {
                    await platform.Database.ExecuteSqlRawAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, step, tag), ct);
                    outcomes.Add($"ok: {step}");
                }
                catch (SqlException exception)
                {
                    outcomes.Add($"{exception.Number}: {step}");
                }
            }

            // Back to a versioned table for the next run, whatever failed.
            await platform.Database.ExecuteSqlRawAsync("IF OBJECTPROPERTY(OBJECT_ID(N'dbo.Documents'), N'TableTemporalType') <> 2 ALTER TABLE [Documents] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[DocumentsHistory]))", ct);
            return outcomes;
        }

        var before = await RunAsync(RowLevelSecurity.PolicyV2, "V2");
        var after = await RunAsync(RowLevelSecurity.PolicyV3, "V3");
        after.ShouldBe(before);
        before[0].ShouldStartWith("ok", customMessage: "a plain ADD needs no policy re-creation");
        before[4].ShouldStartWith("ok", customMessage: "nor does a plain DROP COLUMN");
        before[1].ShouldStartWith("3729:", customMessage: "versioning off needs the policy dropped first, with or without the history predicates");

        await using var check = NewContext(TenantContext.Platform());
        (await check.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.security_predicates WHERE [target_object_id] = OBJECT_ID(N'dbo.DocumentsHistory')").SingleAsync(ct)).ShouldBe(1);
    }

    /// <summary>A tenant session cannot hand one of its own rows of a strict table to another tenant, with EF or raw SQL.</summary>
    [Fact]
    public async Task A_tenant_cannot_move_its_own_row_to_another_tenant()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            await Should.ThrowAsync<SqlException>(() => asA.Database.ExecuteSqlAsync($"UPDATE dbo.IngestionChannels SET TenantId = {TenantB} WHERE Name = N'api'", ct));
            var document = await asA.Documents.SingleAsync(ct);
            document.TenantId = TenantB;
            await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(ct));
        }

        // Changes that keep the tenant still go through.
        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            (await asA.Database.ExecuteSqlAsync($"UPDATE dbo.IngestionChannels SET Name = N'api-a' WHERE Name = N'api'", ct)).ShouldBe(1);
            var document = await asA.Documents.SingleAsync(ct);
            document.Status = DocumentStatus.Normalized;
            await asA.SaveChangesAsync(ct);
        }

        await using var asB = NewContext(TenantContext.ForTenant(TenantB));
        (await asB.IngestionChannels.IgnoreQueryFilters().Select(c => c.Name).ToListAsync(ct)).ShouldBe(["api"]);
        (await asB.Documents.IgnoreQueryFilters().Select(d => d.DocumentNumber).ToListAsync(ct)).ShouldBe(["B-1"]);

        // Platform scope may still move a row (an operator's correction).
        await using var platform = NewContext(TenantContext.Platform());
        (await platform.Database.ExecuteSqlAsync($"UPDATE dbo.IngestionChannels SET TenantId = {TenantB} WHERE Name = N'api-a'", ct)).ShouldBe(1);
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
