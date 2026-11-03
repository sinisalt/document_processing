using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DocProc.Api.Tests;

/// <summary>
/// Synthetic price-list pages for the API tests (docs/11 §5.8, §8.6): a header Art.-Nr., Bezeichnung, Preis, ab 10, ab 50
/// in the 1000 × 1000 page space, rows 15 units apart, optionally a page size in points as a PDF text layer has it.
/// </summary>
internal static class PriceListPages
{
    public static readonly (double X0, double X1, string? Field, string Header)[] Columns =
    [
        (50, 150, "supplierSku", "Art.-Nr."),
        (150, 500, "description", "Bezeichnung"),
        (500, 650, "price", "Preis"),
        (650, 800, null, "ab 10"),
        (800, 990, null, "ab 50"),
    ];

    private static readonly double[] Left = [55, 155, 505, 655, 805];

    public static List<PositionedWord> Line(double y, params string?[] cells)
    {
        var words = new List<PositionedWord>();
        for (var c = 0; c < cells.Length; c++)
        {
            var x = Left[c];
            foreach (var word in (cells[c] ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                words.Add(new PositionedWord(word, new BoundingBox(x, y, x + (word.Length * 6), y + 10)));
                x += (word.Length * 6) + 4;
            }
        }

        return words;
    }

    /// <summary>
    /// <paramref name="pages"/> pages of <paramref name="rowsPerPage"/> articles under a repeated header; the first page has a
    /// title above the header; page 2 a heading "Weißweine" in the article number column, page 3 a rejected price.
    /// </summary>
    public static TextLayer Layer(int pages, int rowsPerPage, bool rejectOnPage3 = false)
    {
        var textPages = new List<TextPage>();
        var sku = 1000;
        for (var p = 1; p <= pages; p++)
        {
            var words = new List<PositionedWord>();
            double y;
            if (p == 1)
            {
                words.AddRange(Line(40, "Nordwind Preisliste 2027"));
                words.AddRange(Line(100, "Art.-Nr.", "Bezeichnung", "Preis", "ab 10", "ab 50"));
                y = 120;
            }
            else
            {
                words.AddRange(Line(50, "Art.-Nr.", "Bezeichnung", "Preis", "ab 10", "ab 50"));
                y = 70;
            }

            if (p == 2)
            {
                words.AddRange(Line(y, "Weißweine"));
                y += 15;
            }

            for (var r = 0; r < rowsPerPage; r++)
            {
                sku++;
                var price = rejectOnPage3 && p == 3 && r == 0 ? "1,2x" : $"{r + 1},50";
                words.AddRange(Line(y, $"{sku}", $"Artikel {sku}", price, r % 2 == 0 ? $"{r},90" : null, r % 2 == 0 ? $"{r},80" : null));
                y += 15;
            }

            words.AddRange(Line(960, null, $"Seite {p} von {pages}"));
            textPages.Add(new TextPage(p, 1000, 1000, words));
        }

        return new TextLayer(TextLayerSource.PdfText, textPages);
    }

    public static object Mapping(int page = 1, string? pages = null, bool write = false) => new
    {
        page,
        bBox = new { x0 = 40, y0 = 95, x1 = 995, y1 = 940 },
        headerRow = new { y0 = 99, y1 = 111 },
        columns = Columns.Select(c => new { x0 = c.X0, x1 = c.X1, field = c.Field }).ToArray(),
        pages,
        groupHeader = new { target = "category" },
        pageMargins = new { bottom = 940 },
        write,
        reason = write ? "price list captured" : null,
    };

    /// <summary>A white page image of 1000 × 1414 pixels.</summary>
    public static byte[] WhitePng()
    {
        using var bitmap = new SKBitmap(1000, 1414);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>An OCR provider that answers what the test scripts, and records the region image it was given.</summary>
public sealed class ScriptedOcrProvider : IOcrProvider
{
    public string Name => "scripted";

    public Func<int, int, int, TextPage> Answer { get; set; } = (page, width, height) => new TextPage(page, width, height, []);

    public List<(int Page, int Width, int Height)> Calls { get; } = [];

    public Task<TextPage> RecognizeAsync(int pageNumber, ReadOnlyMemory<byte> png, IReadOnlyList<string> languageHints, CancellationToken cancellationToken)
    {
        using var bitmap = SKBitmap.Decode(png.ToArray());
        Calls.Add((pageNumber, bitmap.Width, bitmap.Height));
        return Task.FromResult(Answer(pageNumber, bitmap.Width, bitmap.Height));
    }
}

/// <summary>The API host with <see cref="ScriptedOcrProvider"/> as its OCR provider.</summary>
public sealed class OcrApiFactory : ApiFactory
{
    public ScriptedOcrProvider Ocr { get; } = new();

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IOcrProvider>(Ocr);
}

/// <summary>Shared set-up of the map mode tests: a tenant on configurable types with the price list adopted, documents with pages.</summary>
public abstract class MapModeTestBase(ApiFactory factory)
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected ApiFactory Factory { get; } = factory;

    private static readonly JsonSerializerOptions TextLayerJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, IgnoreReadOnlyProperties = true };

    protected Guid NewTenant(string name, bool configurable = true)
    {
        var tenant = Factory.CreateTenant(name);
        using (var db = Factory.PlatformDb())
        {
            TypeCatalogueSeeder.AddMissingAdoptedTypesAsync(db, tenant, Factory.Clock.GetUtcNow(), Ct).GetAwaiter().GetResult();
            db.SaveChanges();
        }

        Factory.Run(db => db.Tenants.Single(t => t.Id == tenant).Settings.Features!.ConfigurableTypes = configurable);
        if (!configurable)
        {
            return tenant;
        }

        using var admin = Factory.ClientFor(new TokenSpec("adm-" + tenant.ToString("N")[..8], [Roles.TenantAdmin], tenant.ToString()));
        admin.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.price-list", mode = "adopt" } }, Ct).GetAwaiter().GetResult().StatusCode.ShouldBe(HttpStatusCode.Created);
        return tenant;
    }

    protected HttpClient Reviewer(Guid tenant) => Factory.ClientFor(new TokenSpec("rev-" + tenant.ToString("N")[..8], [Roles.Reviewer], tenant.ToString()));

    protected HttpClient ReadOnly(Guid tenant) => Factory.ClientFor(new TokenSpec("ro-" + tenant.ToString("N")[..8], [Roles.Viewer], tenant.ToString()));

    protected static Uri Doc(Guid id, string suffix = "") => new($"/api/v1/documents/{id}{suffix}", UriKind.Relative);

    /// <summary>An Unknown document in review with <paramref name="layer"/> as its text layer and a page image per page.</summary>
    protected async Task<Guid> SeedAsync(Guid tenant, TextLayer? layer, int pages, byte[]? png = null)
    {
        var seeded = await Factory.SeedDocumentAsync(new DocumentSeed
        {
            TenantId = tenant,
            Type = DocumentTypeKey.Unknown,
            Invoice = null,
            SupplierName = null,
            ReviewTask = true,
            Flags = [FlagCodes.Create(FlagCodes.TypeUnknown, "The document type could not be determined.")],
        });
        Factory.RecordPipelineProgress(seeded.DocumentId, DocumentStatus.Validated);
        var blobs = Factory.Services.GetRequiredService<IContainerBlobStore>();
        var keys = new List<string>();
        for (var n = 1; n <= pages; n++)
        {
            keys.Add((await blobs.PutAsync(BlobContainers.Derived, tenant, new MemoryStream(png ?? TestInvoices.FakePng()), "image/png", Ct)).Key);
        }

        BlobRef? stored = layer is null ? null : await blobs.PutAsync(BlobContainers.Derived, tenant, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(layer, TextLayerJson)), "application/json", Ct);
        Factory.Run(db =>
        {
            var document = db.Documents.Single(d => d.Id == seeded.DocumentId);
            document.PageImageKeys = keys;
            document.PageCount = pages;
            document.PageStart = 1;
            document.PageEnd = pages;
            document.TextLayerBlob = stored is null ? null : new BlobReference { Container = stored.Container, Key = stored.Key, Sha256 = stored.Sha256, SizeBytes = stored.SizeBytes, ContentType = stored.ContentType };
        });
        return seeded.DocumentId;
    }

    protected static async Task SetTypeAsync(HttpClient reviewer, Guid id) =>
        (await reviewer.PostAsJsonAsync(Doc(id, ":set-type"), new SetDocumentTypeRequest("price-list"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

    protected static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(body);
        return JsonSerializer.Deserialize<T>(body, ApiFactory.Json)!;
    }

    protected static async Task<JsonElement> ProblemOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
}

/// <summary>
/// Map mode and the table tool through the API (docs/11 §12.3, §12.4): <c>/pages/{n}/words</c> with the selection's
/// text and readings, the table tool's proposal, "apply to all pages" with the report and page strip, the
/// single-correction write with its <c>DocumentTableReport</c>, and the refusals; <c>regions:read</c> without OCR.
/// </summary>
public sealed class MapModeEndpointsTests(ApiFactory factory) : MapModeTestBase(factory), IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Words_are_returned_in_page_units_and_a_box_gives_its_text_and_readings_per_kind()
    {
        var tenant = NewTenant("Words");

        // A PDF text layer in points (A4): the API answers in the normalised 1000 × 1000 page units.
        var page = new TextPage(1, 595, 842,
        [
            new PositionedWord("Rechnungsdatum:", new BoundingBox(59.5, 84.2, 119, 92.62)),
            new PositionedWord("28.10.2026", new BoundingBox(125, 84.2, 160, 92.62)),
            new PositionedWord("Summe", new BoundingBox(59.5, 421, 90, 429.42)),
            new PositionedWord("1.234,50", new BoundingBox(400, 421, 430, 429.42)),
            new PositionedWord("EUR", new BoundingBox(432, 421, 445, 429.42)),
        ]);
        var id = await SeedAsync(tenant, new TextLayer(TextLayerSource.PdfText, [page]), pages: 1);
        using var reader = ReadOnly(tenant);

        var all = await Read<PageWordsResponse>(await reader.GetAsync(Doc(id, "/pages/1/words"), Ct));
        all.Source.ShouldBe(WordSources.PdfText);
        all.Locale.ShouldBe("de-DE", "the tenant is in Germany");
        all.Words.Count.ShouldBe(5);
        all.Words[0].Index.ShouldBe(0);
        all.Words[0].BBox.ShouldBe(new SourceBox(100, 100, 200, 110));
        all.Text.ShouldBeNull();
        all.Candidates.ShouldBeEmpty();

        var date = await Read<PageWordsResponse>(await reader.GetAsync(Doc(id, "/pages/1/words?bbox=205,95,300,115"), Ct));
        date.Words.ShouldHaveSingleItem().Index.ShouldBe(1);
        date.Text.ShouldBe("28.10.2026");
        date.Candidates.Single(c => c.Kind == "date").Value.GetString().ShouldBe("2026-10-28");

        var amount = await Read<PageWordsResponse>(await reader.GetAsync(Doc(id, "/pages/1/words?bbox=600,490,800,520"), Ct));
        amount.Text.ShouldBe("1.234,50 EUR");
        amount.Words.Select(w => w.Index).ShouldBe([3, 4]);
        var money = amount.Candidates.Single(c => c.Kind == "money").Value;
        money.GetProperty("amount").GetDecimal().ShouldBe(1234.50m);
        money.GetProperty("currency").GetString().ShouldBe("EUR");

        // In another locale a selection reads the other way where the separators do not decide.
        var us = await Read<PageWordsResponse>(await reader.GetAsync(Doc(id, "/pages/1/words?bbox=600,490,800,520&locale=en-US"), Ct));
        us.Locale.ShouldBe("en-US");

        (await reader.GetAsync(Doc(id, "/pages/1/words?bbox=1,2,3"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reader.GetAsync(Doc(id, "/pages/1/words?locale=klingon"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reader.GetAsync(Doc(id, "/pages/2/words"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A page without a text layer (an image) has no words: draw a box and read it with regions:read.
        var image = await SeedAsync(tenant, null, pages: 1);
        var none = await Read<PageWordsResponse>(await reader.GetAsync(Doc(image, "/pages/1/words"), Ct));
        none.Source.ShouldBe(WordSources.None);
        none.Words.ShouldBeEmpty();

        // Another tenant's document is not there.
        var other = NewTenant("Words other");
        using var stranger = ReadOnly(other);
        (await stranger.GetAsync(Doc(id, "/pages/1/words"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_table_tool_proposes_columns_from_the_box_and_names_them_from_the_field_labels()
    {
        var tenant = NewTenant("Table propose");
        var id = await SeedAsync(tenant, PriceListPages.Layer(pages: 1, rowsPerPage: 6), pages: 1);
        using var reviewer = Reviewer(tenant);
        await SetTypeAsync(reviewer, id);

        var proposal = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), new { page = 1, bBox = new { x0 = 40, y0 = 95, x1 = 995, y1 = 300 } }, Ct));

        proposal.Proposed.ShouldBeTrue();
        proposal.Path.ShouldBe("lines");
        proposal.HeaderRow.ShouldNotBeNull().Y0.ShouldBe(100);
        proposal.Columns.Select(c => c.Header).ShouldBe(["Art.-Nr.", "Bezeichnung", "Preis", "ab 10", "ab 50"]);
        proposal.Columns.Select(c => c.Field).ShouldBe(["supplierSku", "description", "price", null, null]);
        proposal.Columns.Select(c => c.TierMinQuantity).ShouldBe([null, null, null, 10m, 50m]);
        proposal.RowAnchor.ShouldBe("supplierSku", "the price list's rows are anchored on the article number");
        proposal.Report.Rows.ShouldBe(6);
        proposal.Rows[0].Tiers.Select(t => t.MinQuantity).ShouldBe([10m, 50m]);
        proposal.Written.ShouldBeFalse();

        // Proposing writes nothing.
        Factory.Query(db => db.Corrections.Count(c => c.DocumentId == id && c.FieldPath == "lines")).ShouldBe(0);
    }

    [Fact]
    public async Task One_mapping_on_page_1_reads_every_page_and_write_stores_all_rows_as_one_correction_with_its_report()
    {
        var tenant = NewTenant("Table write");
        var id = await SeedAsync(tenant, PriceListPages.Layer(pages: 3, rowsPerPage: 20, rejectOnPage3: true), pages: 3);
        using var reviewer = Reviewer(tenant);
        await SetTypeAsync(reviewer, id);

        // The preview: rows per page, the rejected row with its text, the dividers per page.
        var preview = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), PriceListPages.Mapping(pages: "1-*"), Ct));
        preview.Proposed.ShouldBeFalse();
        preview.Written.ShouldBeFalse();
        preview.Report.Pages.ShouldBe([1, 2, 3]);
        preview.Report.Rows.ShouldBe(59);
        preview.Report.RejectedRows.ShouldBe(1);
        preview.Report.PerPage.Select(p => (p.Page, p.Rows, p.Rejected, p.HeaderFound)).ShouldBe([(1, 20, 0, true), (2, 20, 0, true), (3, 19, 1, true)]);
        var rejected = preview.Report.PerPage[2].RejectedLines.ShouldHaveSingleItem();
        rejected.Text.ShouldContain("1,2x");
        rejected.Field.ShouldBe("price");
        preview.Report.PerPage[1].GroupHeadings.ShouldBe(1);
        preview.Report.PerPage[0].FirstRows.Count.ShouldBe(3);
        preview.Report.PerPage[0].Columns.Count.ShouldBe(5);
        preview.Rows.Count.ShouldBe(59);
        preview.Rows.Where(r => r.Page == 2).All(r => r.Cells.Any(c => c.Field == "category" && c.Text == "Weißweine")).ShouldBeTrue();
        Factory.Query(db => db.Corrections.Count(c => c.DocumentId == id && c.FieldPath == "lines")).ShouldBe(0);

        // The write: one correction, the rows in the tree with their places, the report stored, the inbox row count.
        var written = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), PriceListPages.Mapping(pages: "1-*", write: true), Ct));
        written.Written.ShouldBeTrue();
        var detail = written.Document.ShouldNotBeNull();
        detail.LineCount.ShouldBe(59);
        var lines = detail.Canonical!.Value.GetProperty("lines");
        lines.GetArrayLength().ShouldBe(59);
        var first = lines[0];
        first.GetProperty("supplierSku").GetProperty("value").GetProperty("verbatim").GetString().ShouldBe("1001");
        first.GetProperty("price").GetProperty("value").GetProperty("amount").GetDecimal().ShouldBe(1.50m);
        first.GetProperty("price").GetProperty("value").GetProperty("currency").GetString().ShouldBe("EUR");
        first.GetProperty("price").GetProperty("source").GetProperty("page").GetInt32().ShouldBe(1);
        first.GetProperty("price").GetProperty("source").GetProperty("rawText").GetString().ShouldBe("1,50");
        first.GetProperty("tiers").GetArrayLength().ShouldBe(2);
        lines[20].GetProperty("category").GetProperty("value").GetString().ShouldBe("Weißweine");
        lines[20].GetProperty("price").GetProperty("source").GetProperty("page").GetInt32().ShouldBe(2);

        var corrections = Factory.Query(db => db.Corrections.Where(c => c.DocumentId == id && c.FieldPath == "lines").ToList());
        var correction = corrections.ShouldHaveSingleItem();
        correction.Op.ShouldBe(CorrectionOps.Set);
        correction.Reason.ShouldBe("price list captured");
        var source = JsonNode.Parse(correction.SourceJson!)!;
        source["method"]!.GetValue<string>().ShouldBe("table");
        source["table"]!["spec"]!["pages"]!.GetValue<string>().ShouldBe("1-*");
        source["table"]!["spec"]!["rowAnchor"]!.GetValue<string>().ShouldBe("supplierSku");
        source["table"]!["report"]!["rows"]!.GetValue<int>().ShouldBe(59);
        JsonNode.Parse(correction.NewValueJson!)!.AsArray().Count.ShouldBe(59);

        var stored = Factory.Query(db => db.DocumentTableReports.Single(r => r.DocumentId == id));
        (stored.Path, stored.Origin, stored.CorrectionId).ShouldBe(("lines", TableReportOrigin.ReviewTool, correction.Id));
        stored.ProducedAtVersion.ShouldBeGreaterThan(0);

        // The page strip after a reload.
        var reports = await Read<List<DocumentTableReportDto>>(await reviewer.GetAsync(Doc(id, "/table-reports"), Ct));
        var report = reports.ShouldHaveSingleItem();
        report.Report.PerPage.Select(p => p.Rows).ShouldBe([20, 20, 19]);
        report.Report.PerPage[2].RejectedLines.ShouldHaveSingleItem().Text.ShouldContain("1,2x");

        // The timeline has one corrected entry for the capture.
        var events = Factory.Query(db => db.DocumentEvents.Where(e => e.DocumentId == id && e.EventType == DocumentEventTypes.Corrected).ToList());
        events.ShouldHaveSingleItem().DetailsJson!.ShouldContain("\"rejectedRows\":1");

        // A row edited afterwards is an ordinary correction; the report stays.
        (await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new { corrections = new[] { new { path = "lines[0].description", value = "Artikel 1001 neu" } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Factory.Query(db => db.DocumentTableReports.Count(r => r.DocumentId == id)).ShouldBe(1);

        // Capturing again replaces the rows and the report (still one report for the path).
        var again = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), PriceListPages.Mapping(pages: "1-2", write: true), Ct));
        again.Document!.LineCount.ShouldBe(40);
        Factory.Query(db => db.DocumentTableReports.Single(r => r.DocumentId == id)).ReportJson.ShouldContain("\"rows\":40");
        Factory.Query(db => db.Corrections.Count(c => c.DocumentId == id && c.FieldPath == "lines")).ShouldBe(2);

        // A reviewer replacing all rows by hand wrote the group from another source: the report goes.
        (await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new { corrections = new[] { new { path = "lines", value = new[] { new { supplierSku = "X1", description = "Handarbeit", price = 9.99m } } } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Factory.Query(db => db.DocumentTableReports.Count(r => r.DocumentId == id)).ShouldBe(0);
        Factory.Query(db => db.Documents.Single(d => d.Id == id).LineCount).ShouldBe(1);
    }

    [Fact]
    public async Task A_new_type_or_a_split_drops_the_table_report()
    {
        var tenant = NewTenant("Table retype");
        var id = await SeedAsync(tenant, PriceListPages.Layer(pages: 1, rowsPerPage: 3), pages: 1);
        using var reviewer = Reviewer(tenant);
        await SetTypeAsync(reviewer, id);
        (await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), PriceListPages.Mapping(write: true), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Factory.Query(db => db.DocumentTableReports.Count(r => r.DocumentId == id)).ShouldBe(1);

        (await reviewer.PostAsJsonAsync(Doc(id, ":set-type"), new SetDocumentTypeRequest("DeliveryNote"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        Factory.Query(db => db.DocumentTableReports.Count(r => r.DocumentId == id)).ShouldBe(0);

        // A split clears the data of a document whose pages change, and its reports with it.
        var two = await SeedAsync(tenant, PriceListPages.Layer(pages: 2, rowsPerPage: 3), pages: 2);
        await SetTypeAsync(reviewer, two);
        (await reviewer.PostAsJsonAsync(Doc(two, "/regions:table"), PriceListPages.Mapping(pages: "1-2", write: true), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Factory.Query(db => db.DocumentTableReports.Count(r => r.DocumentId == two)).ShouldBe(1);
        var split = await reviewer.PostAsJsonAsync(Doc(two, ":split"), new SplitDocumentRequest([new SplitPartRequest([1]), new SplitPartRequest([2])]), Ct);
        split.StatusCode.ShouldBe(HttpStatusCode.Accepted, await split.Content.ReadAsStringAsync(Ct));
        Factory.Query(db => db.DocumentTableReports.Count(r => r.DocumentId == two)).ShouldBe(0);
    }

    [Fact]
    public async Task Requests_that_do_not_fit_are_refused_with_the_reason()
    {
        var tenant = NewTenant("Table refusals");
        var id = await SeedAsync(tenant, PriceListPages.Layer(pages: 2, rowsPerPage: 3), pages: 2);
        using var reviewer = Reviewer(tenant);

        // No type yet: nothing says which fields a table fills.
        var untyped = await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), PriceListPages.Mapping(), Ct);
        untyped.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemOf(untyped)).GetProperty("title").GetString().ShouldBe("No type");

        await SetTypeAsync(reviewer, id);
        async Task<JsonElement> Refused(object body)
        {
            var response = await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), body, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
            return (await ProblemOf(response)).GetProperty("errors");
        }

        var box = new { x0 = 40, y0 = 95, x1 = 995, y1 = 940 };
        (await Refused(new { page = 1, bBox = box, columns = new[] { new { x0 = 50, x1 = 150, field = "nope" } } })).TryGetProperty("columns[0].field", out _).ShouldBeTrue();
        (await Refused(new { page = 1, bBox = box, columns = new[] { new { x0 = 50, x1 = 150, field = "description" } }, rowAnchor = "supplierSku" })).TryGetProperty("rowAnchor", out _).ShouldBeTrue();
        (await Refused(new { page = 1, bBox = box, columns = new[] { new { x0 = 150, x1 = 50, field = "supplierSku" } } })).TryGetProperty("columns[0]", out _).ShouldBeTrue();
        (await Refused(new { page = 1, bBox = box, columns = new[] { new { x0 = 50, x1 = 150, field = "supplierSku" } }, pages = "1-9" })).TryGetProperty("pages", out _).ShouldBeTrue();
        (await Refused(new { page = 1, bBox = box, columns = new[] { new { x0 = 50, x1 = 150, field = "supplierSku" } }, skipRows = new[] { "(a)\\1" } })).TryGetProperty("skipRows[0]", out _).ShouldBeTrue();
        (await Refused(new { page = 1, bBox = box, columns = new[] { new { x0 = 50, x1 = 150, field = "supplierSku" } }, continuation = "sometimes" })).TryGetProperty("continuation", out _).ShouldBeTrue();
        (await Refused(new { page = 1, bBox = box, write = true })).TryGetProperty("columns", out _).ShouldBeTrue();
        var wrongDivider = new { page = 1, bBox = box, headerRow = new { y0 = 99, y1 = 111 }, columns = new[] { new { x0 = 50.0, x1 = 500.0, field = "description" }, new { x0 = 990.0, x1 = 999.0, field = "supplierSku" } }, write = true };
        (await Refused(wrongDivider)).GetProperty("columns")[0].GetString()!.ShouldContain("read no rows");
        var tooMany = Enumerable.Range(1, 3).Select(p => new { page = p, columns = new[] { new { x0 = 50, x1 = 150 } } }).ToArray();
        (await Refused(new { page = 1, bBox = box, columns = new[] { new { x0 = 50, x1 = 150, field = "supplierSku" } }, pageOverrides = tooMany })).TryGetProperty("pageOverrides", out _).ShouldBeTrue();
        (await Refused(new { page = 1, bBox = box, columns = new[] { new { x0 = 50, x1 = 150, field = "supplierSku" } }, pageOverrides = new[] { new { page = 2, columns = new[] { new { x0 = 50, x1 = 150 }, new { x0 = 150, x1 = 250 } } } } })).TryGetProperty("pageOverrides[0].columns", out _).ShouldBeTrue();
        (await Refused(new { page = 1, bBox = box, path = "currency", columns = new[] { new { x0 = 50, x1 = 150, field = "supplierSku" } } })).TryGetProperty("path", out _).ShouldBeTrue();

        // A read-only member reads words but does not run the table tool.
        using var reader = ReadOnly(tenant);
        (await reader.PostAsJsonAsync(Doc(id, "/regions:table"), PriceListPages.Mapping(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_settled_document_and_a_coded_invoice_record_are_not_written()
    {
        var tenant = NewTenant("Table not written");
        var id = await SeedAsync(tenant, PriceListPages.Layer(pages: 1, rowsPerPage: 3), pages: 1);
        using var reviewer = Reviewer(tenant);
        await SetTypeAsync(reviewer, id);
        Factory.Run(db => db.Documents.Single(d => d.Id == id).Status = DocumentStatus.Approved);

        var settled = await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), PriceListPages.Mapping(write: true), Ct);
        settled.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemOf(settled)).GetProperty("title").GetString().ShouldBe("Document not in review");

        // Reading the table of a settled document is still possible (nothing is written).
        (await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), PriceListPages.Mapping(), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // An invoice of a tenant without configurable types is the coded record: the tool reads, the write is refused.
        var legacy = NewTenant("Table legacy", configurable: false);
        var invoice = await Factory.SeedDocumentAsync(new DocumentSeed { TenantId = legacy, ReviewTask = true });
        Factory.Run(db =>
        {
            var source = db.Documents.Single(d => d.Id == id);
            var document = db.Documents.Single(d => d.Id == invoice.DocumentId);
            document.TextLayerBlob = new BlobReference { Container = source.TextLayerBlob!.Container, Key = source.TextLayerBlob.Key, Sha256 = source.TextLayerBlob.Sha256, SizeBytes = source.TextLayerBlob.SizeBytes, ContentType = source.TextLayerBlob.ContentType };
        });
        using var legacyReviewer = Reviewer(legacy);
        var mapping = new
        {
            page = 1,
            bBox = new { x0 = 40, y0 = 95, x1 = 995, y1 = 940 },
            headerRow = new { y0 = 99, y1 = 111 },
            columns = new[] { new { x0 = 150, x1 = 500, field = "description" }, new { x0 = 500, x1 = 650, field = "unitPrice" } },
            write = true,
        };
        var coded = await legacyReviewer.PostAsJsonAsync(Doc(invoice.DocumentId, "/regions:table"), mapping, Ct);
        coded.StatusCode.ShouldBe(HttpStatusCode.Conflict, await coded.Content.ReadAsStringAsync(Ct));
        (await ProblemOf(coded)).GetProperty("title").GetString().ShouldBe("Not a definition-driven document");
    }

    [Fact]
    public async Task Region_ocr_without_a_provider_answers_501()
    {
        var tenant = NewTenant("No OCR");
        var id = await SeedAsync(tenant, null, pages: 1, png: PriceListPages.WhitePng());
        using var reviewer = Reviewer(tenant);

        var response = await reviewer.PostAsJsonAsync(Doc(id, "/regions:read"), new ReadRegionRequest(1, new SourceBox(100, 100, 300, 130)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        (await ProblemOf(response)).GetProperty("code").GetString().ShouldBe(DocumentEndpoints.OcrNotConfigured);
    }
}

/// <summary>Region OCR through the API (docs/11 §12.3): the page image cut to the box, read by the OCR provider, its words placed back on the page.</summary>
public sealed class RegionOcrEndpointsTests(OcrApiFactory factory) : MapModeTestBase(factory), IClassFixture<OcrApiFactory>
{
    [Fact]
    public async Task A_box_on_a_scan_is_cut_out_read_by_ocr_and_its_words_come_back_in_page_units()
    {
        var tenant = NewTenant("OCR");
        var id = await SeedAsync(tenant, null, pages: 1, png: PriceListPages.WhitePng());
        using var reviewer = Reviewer(tenant);

        // The scripted OCR finds two words in the left and right half of the region it is given.
        factory.Ocr.Answer = (page, width, height) => new TextPage(page, width, height,
        [
            new PositionedWord("28.10.2026", new BoundingBox(0, 0, width / 2.0, height), 0.93),
            new PositionedWord("x", new BoundingBox(width / 2.0, 0, width, height), 0.5),
        ]);

        var read = await Read<PageWordsResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:read"), new ReadRegionRequest(1, new SourceBox(100, 100, 300, 130)), Ct));

        // The cut: 200 × 30 page units of a 1000 × 1414 pixel page are 200 × 43 pixels (whole pixels, rounded out).
        var call = factory.Ocr.Calls.ShouldHaveSingleItem();
        (call.Page, call.Width).ShouldBe((1, 200));
        call.Height.ShouldBeInRange(42, 44);

        read.Source.ShouldBe(WordSources.Ocr);
        read.Provider.ShouldBe("scripted");
        read.Words.Select(w => w.Text).ShouldBe(["28.10.2026", "x"]);
        read.Words.All(w => w.Index is null).ShouldBeTrue("OCR words of a region are not in the stored text layer");
        read.Words[0].BBox.X0.ShouldBe(100, 0.5);
        read.Words[0].BBox.X1.ShouldBe(200, 0.5);
        read.Words[1].BBox.X1.ShouldBe(300, 0.5);
        read.Words[0].BBox.Y0.ShouldBe(100, 1);
        read.Words[0].Confidence.ShouldBe(0.93);
        read.Text.ShouldBe("28.10.2026 x");
        read.BBox.ShouldNotBeNull().X0.ShouldBe(100, 0.5);

        // Refused: a box off the page, a page that is not there, a read-only member.
        (await reviewer.PostAsJsonAsync(Doc(id, "/regions:read"), new ReadRegionRequest(1, new SourceBox(100, 100, 1300, 130)), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reviewer.PostAsJsonAsync(Doc(id, "/regions:read"), new ReadRegionRequest(2, new SourceBox(100, 100, 300, 130)), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var reader = ReadOnly(tenant);
        (await reader.PostAsJsonAsync(Doc(id, "/regions:read"), new ReadRegionRequest(1, new SourceBox(100, 100, 300, 130)), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
