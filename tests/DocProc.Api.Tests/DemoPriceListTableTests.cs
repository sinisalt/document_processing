using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Documents;
using DocProc.Infrastructure.Pdf;
using Microsoft.Extensions.Options;

namespace DocProc.Api.Tests;

/// <summary>
/// The P2 demo of docs/11 §23 through the API, on the committed demo PDF (<c>fixtures/demo/uploads/tenant-a/Nordwind_Preisliste_2027.pdf</c>,
/// docs/09 "Demo: manual mapping"): the text layer as the Normalize stage reads it, the type set in review, the table
/// tool's proposal from a box drawn on page 1, and the mapping applied to all 14 pages as the portal sends it: 412 rows,
/// none rejected, the header found again on every page, every heading a category, the tier prices, the continuation
/// lines, a row carried across a page break, then written as one correction.
/// </summary>
public sealed class DemoPriceListTableTests(ApiFactory factory) : MapModeTestBase(factory), IClassFixture<ApiFactory>
{
    private static readonly int[] RowsPerPage = [22, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30];

    private static string PriceListPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocProc.slnx")))
            {
                return Path.Combine(directory.FullName, "fixtures", "demo", "uploads", "tenant-a", "Nordwind_Preisliste_2027.pdf");
            }
        }

        throw new DirectoryNotFoundException($"No DocProc.slnx above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public async Task The_demo_price_list_is_captured_with_one_mapping_drawn_on_page_1()
    {
        var processor = new PdfPigPdfProcessor(Options.Create(new PdfProcessingOptions()));
        await using var file = File.OpenRead(PriceListPath());
        var layer = await processor.ExtractTextAsync(file, Ct);
        layer.Pages.Count.ShouldBe(14);

        var tenant = NewTenant("Demo price list");
        var id = await SeedAsync(tenant, layer, pages: 14);
        using var reviewer = Reviewer(tenant);
        await SetTypeAsync(reviewer, id);

        // The reviewer drags a box on page 1 from the column header down to the footer rule (page units).
        var header = await Read<PageWordsResponse>(await reviewer.GetAsync(Doc(id, "/pages/1/words?bbox=40,280,960,300"), Ct));
        header.Text.ShouldBe("Art.-Nr. Bezeichnung Gebinde Preis ab 10 ab 50");
        var box = new { x0 = 45, y0 = 280, x1 = 945, y1 = 935 };
        var proposal = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), new { page = 1, bBox = box }, Ct));
        proposal.Proposed.ShouldBeTrue();
        proposal.Columns.Select(c => c.Header).ShouldBe(["Art.-Nr.", "Bezeichnung", "Gebinde", "Preis", "ab 10", "ab 50"]);
        proposal.Columns.Select(c => c.Field).ShouldBe(["supplierSku", "description", "packDescription", "price", null, null], "the proposal names every column from the template's labels");
        proposal.Columns.Select(c => c.TierMinQuantity).ShouldBe([null, null, null, null, 10m, 50m]);
        proposal.RowAnchor.ShouldBe("supplierSku");
        // The proposal reads page 1 without group headings, so "Weißweine" is one row more; the mapping below names them.
        proposal.Report.Rows.ShouldBe(23);

        // "Apply to all pages", as the portal's table tool sends it.
        var mapping = new
        {
            page = 1,
            bBox = box,
            columns = proposal.Columns,
            headerRow = proposal.HeaderRow,
            rowAnchor = proposal.RowAnchor,
            pages = "1-*",
            groupHeader = new { target = "category" },
            write = true,
            reason = "Price list mapped in review",
        };
        var response = await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), mapping, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var written = await Read<ReadTableResponse>(response);

        var report = written.Report;
        report.RejectedRows.ShouldBe(0, string.Join("; ", report.PerPage.SelectMany(p => p.RejectedLines).Select(r => $"p. {r.Page}: {r.Text} ({r.Reason}: {r.Message})")));
        report.Rows.ShouldBe(412);
        report.Pages.Count.ShouldBe(14);
        report.PagesWithoutRows.ShouldBeEmpty();
        report.PerPage.Select(p => p.Rows).ShouldBe(RowsPerPage);
        report.PerPage.ShouldAllBe(p => p.HeaderFound);
        report.PerPage.Sum(p => p.GroupHeadings).ShouldBe(9, "nine groups, each heading once");
        report.PerPage.Sum(p => p.ContinuationLines).ShouldBe(13, "eleven continuation lines under a row and two carried across a page break");

        var lines = written.Document.ShouldNotBeNull().Canonical!.Value.GetProperty("lines");
        lines.GetArrayLength().ShouldBe(412);
        written.Document!.LineCount.ShouldBe(412);
        string Text(int row, string field) => lines[row].GetProperty(field).GetProperty("value").ValueKind == System.Text.Json.JsonValueKind.Object
            ? lines[row].GetProperty(field).GetProperty("value").GetProperty("verbatim").GetString()!
            : lines[row].GetProperty(field).GetProperty("value").GetString()!;

        Text(0, "supplierSku").ShouldBe("104007");
        Text(0, "category").ShouldBe("Weißweine");
        Text(0, "packDescription").ShouldBe("6 x 0,75 l");
        lines[0].GetProperty("price").GetProperty("value").GetProperty("amount").GetDecimal().ShouldBe(9.38m);
        lines[0].GetProperty("tiers").GetArrayLength().ShouldBe(2);
        Text(6, "description").ShouldBe("Sauvignon Blanc feinherb 2023 im Holzfass gereift, limitiert");
        Text(60, "category").ShouldBe("Rotweine");
        // The last row of page 4 continues at the top of page 5.
        var carried = 22 + 30 + 30 + 30 - 1;
        Text(carried, "description").ShouldEndWith("Restposten, Nachlieferung nicht möglich");
        lines[carried].GetProperty("price").GetProperty("source").GetProperty("page").GetInt32().ShouldBe(4);
        Text(411, "category").ShouldBe("Feinkost");
        Enumerable.Range(0, 412).Select(i => Text(i, "supplierSku")).Distinct().Count().ShouldBe(412);

        Factory.Query(db => db.Corrections.Count(c => c.DocumentId == id && c.FieldPath == "lines")).ShouldBe(1);
    }
}
