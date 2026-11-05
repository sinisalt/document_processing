using System.Diagnostics;
using System.Text;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Integration.Tests;

/// <summary>
/// Reading a document whose canonical tree is about 1 MB (a 400-row price list, the P5 demo's normal case) on SQL
/// Server 2025: a list reads the summary columns only, and the detail reads the tree in well under a second.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LargeDocumentReadSqlTests(SqlServerFixture sqlServer) : IClassFixture<SqlServerFixture>
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private string _connectionString = string.Empty;

    /// <summary>A read of a 1.1 MB tree that SqlClient's buffered async read takes about 1.5 s for (0.08 s streamed).</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_document_without_its_trees_reads_in_milliseconds_and_its_trees_stream_in_after_it()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        _connectionString = sqlServer.NewDatabaseConnectionString();
        var ids = await SeedAsync(3, ct);
        var expected = PriceList(1240);

        await using var db = NewContext(TenantContext.ForTenant(TenantA));
        (await db.Documents.AsNoTracking().CountAsync(ct)).ShouldBe(8); // the connection and the model warmed up

        // Every document of the tenant, three of them 1.1 MB price lists, without their trees.
        var clock = Stopwatch.StartNew();
        var all = await DocumentJsonColumns.WithoutTrees(db).AsNoTracking().OrderBy(d => d.CreatedAt).ToListAsync(ct);
        clock.Elapsed.ShouldBeLessThan(Budget);
        all.Count.ShouldBe(8);
        all.ShouldAllBe(d => d.CanonicalJson == null && d.AppliedConfigJson == null);
        all[0].LineCount.ShouldBe(1240, "every other column is read");
        all[0].PageImageKeys.ShouldBe(["pages/1.png", "pages/2.png"]);

        // One of them in full, its tree streamed in.
        clock.Restart();
        var detail = await DocumentJsonColumns.WithoutTrees(db).AsNoTracking().SingleAsync(d => d.Id == ids[0], ct);
        await DocumentJsonColumns.LoadTreesAsync(db, detail, ct);
        clock.Elapsed.ShouldBeLessThan(Budget);
        detail.CanonicalJson.ShouldBe(expected);
        detail.AppliedConfigJson.ShouldBe("""{"prefill":{"sources":{}}}""");
    }

    [Fact]
    public async Task A_tracked_document_read_without_its_trees_saves_only_what_the_caller_changed()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        _connectionString = sqlServer.NewDatabaseConnectionString();
        var ids = await SeedAsync(1, ct);

        await using (var db = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var document = await DocumentJsonColumns.WithoutTrees(db).SingleAsync(d => d.Id == ids[0], ct);
            await DocumentJsonColumns.LoadTreesAsync(db, document, ct);
            db.Entry(document).State.ShouldBe(EntityState.Unchanged, "the streamed trees are the document's original values");
            document.LineCount = 1239;
            await db.SaveChangesAsync(ct);
        }

        await using (var db = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var saved = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == ids[0], ct);
            saved.LineCount.ShouldBe(1239);
            saved.CanonicalJson.ShouldBe(PriceList(1240), "the tree was not written as NULL");

            // A tree the caller changes is saved.
            var document = await DocumentJsonColumns.WithoutTrees(db).SingleAsync(d => d.Id == ids[0], ct);
            await DocumentJsonColumns.LoadTreesAsync(db, document, ct);
            document.CanonicalJson = """{"schemaVersion":2,"values":{"lines":[]}}""";
            await db.SaveChangesAsync(ct);
        }

        await using (var db = NewContext(TenantContext.ForTenant(TenantA)))
        {
            (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == ids[0], ct)).CanonicalJson.ShouldBe("""{"schemaVersion":2,"values":{"lines":[]}}""");
            (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == ids[0], ct)).AppliedConfigJson.ShouldBe("""{"prefill":{"sources":{}}}""");
        }

        // Another tenant's session finds nothing (row-level security and the query filter both apply).
        await using var other = NewContext(TenantContext.ForTenant(Guid.NewGuid()));
        (await DocumentJsonColumns.WithoutTrees(other).AnyAsync(ct)).ShouldBeFalse();
    }

    private async Task<List<Guid>> SeedAsync(int large, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            await platform.Database.MigrateAsync(ct);
            platform.Tenants.Add(new Tenant { Id = TenantA, Name = "tenant-a", Slug = "tenant-a", LegalName = "tenant-a", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
            await platform.SaveChangesAsync(ct);
        }

        await using var seeding = NewContext(TenantContext.ForTenant(TenantA));
        var channel = new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = TenantA, Name = "api", Type = ChannelType.ApiUpload, CreatedAt = now };
        var blob = new BlobReference { Container = "raw", Key = "raw", Sha256 = new string('0', 64), SizeBytes = 1, ContentType = "application/pdf" };
        var ingestion = new Ingestion { Id = Guid.CreateVersion7(), TenantId = TenantA, ChannelId = channel.Id, ReceivedAt = now, RawBlob = blob, UpdatedAt = now };
        seeding.IngestionChannels.Add(channel);
        seeding.Ingestions.Add(ingestion);
        for (var i = 0; i < large + 5; i++)
        {
            var sha = i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
            var item = new IngestionItem
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                IngestionId = ingestion.Id,
                FileName = $"{i}.pdf",
                MimeType = "application/pdf",
                SizeBytes = 1,
                Sha256 = sha,
                Blob = new BlobReference { Container = "raw", Key = sha, Sha256 = sha, SizeBytes = 1, ContentType = "application/pdf" },
                Kind = ItemKind.Pdf,
                Disposition = ItemDisposition.Accepted,
                CreatedAt = now,
            };
            var document = new Document
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                IngestionItemId = item.Id,
                CreatedAt = now.AddMinutes(i),
                UpdatedAt = now,
                CanonicalJson = i < large ? PriceList(1240) : """{"schemaVersion":2,"values":{}}""",
                LineCount = i < large ? 1240 : null,
                PageImageKeys = i < large ? ["pages/1.png", "pages/2.png"] : [],
                AppliedConfigJson = i < large ? """{"prefill":{"sources":{}}}""" : null,
            };
            seeding.IngestionItems.Add(item);
            seeding.Documents.Add(document);
            ids.Add(document.Id);
        }

        await seeding.SaveChangesAsync(ct);
        return ids;
    }

    /// <summary>A canonical tree of about 1 MB: <paramref name="rows"/> article rows with their sources, as a layout writes them.</summary>
    internal static string PriceList(int rows)
    {
        var text = new StringBuilder("""{"schemaVersion":2,"values":{"validFrom":{"value":"2027-02-01"},"lines":[""");
        for (var i = 0; i < rows; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            var page = (1 + (i / 40)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var n = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string Cell(string value, string printed) =>
                "{\"value\":" + value + ",\"confidence\":0.97,\"source\":{\"page\":" + page + ",\"box\":[0.1,0.2,0.3,0.4],\"text\":\"" + printed + "\",\"method\":\"Layout\"}}";
            var article = "NW-" + n.PadLeft(5, '0');
            var description = "Artikel " + n + " Bio Vollmilch 3,8 % Fett 1 l Tetra Pak";
            var gtin = "40" + n.PadLeft(11, '0');
            text.Append("{\"articleNumber\":").Append(Cell("\"" + article + "\"", article))
                .Append(",\"description\":").Append(Cell("\"" + description + "\"", description))
                .Append(",\"unitPrice\":").Append(Cell("{\"amount\":" + n + ".25,\"currency\":\"EUR\"}", n + ",25"))
                .Append(",\"unit\":").Append(Cell("\"Stück\"", "Stück"))
                .Append(",\"packSize\":").Append(Cell("\"12x1l\"", "12x1l"))
                .Append(",\"gtin\":").Append(Cell("\"" + gtin + "\"", gtin))
                .Append('}');
        }

        return text.Append("]}}").ToString();
    }

    private DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), _connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }
}
