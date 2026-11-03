using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Documents;
using DocProc.Infrastructure.Pdf;
using DocProc.Mapping.Tables;
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
        // The portal asks for the proposal with group headings when the row has a category, as it previews (docs/09 step 6).
        var proposal = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), new { page = 1, bBox = box, groupHeader = new { target = "category" } }, Ct));
        proposal.Proposed.ShouldBeTrue();
        proposal.Columns.Select(c => c.Header).ShouldBe(["Art.-Nr.", "Bezeichnung", "Gebinde", "Preis", "ab 10", "ab 50"]);
        proposal.Columns.Select(c => c.Field).ShouldBe(["supplierSku", "description", "packDescription", "price", null, null], "the proposal names every column from the template's labels");
        proposal.Columns.Select(c => c.TierMinQuantity).ShouldBe([null, null, null, null, 10m, 50m]);
        proposal.RowAnchor.ShouldBe("supplierSku");
        proposal.Report.Rows.ShouldBe(22, "page 1 has 22 articles; the heading Weißweine is a heading");
        proposal.Report.RejectedRows.ShouldBe(0);
        proposal.Report.PerPage.ShouldHaveSingleItem().GroupHeadings.ShouldBe(1);
        proposal.Report.PerPage[0].Headings.ShouldBe(["Weißweine"]);
        proposal.Rows[0].Cells.Single(c => c.Field == "supplierSku").Text.ShouldBe("104007");
        proposal.Rows[0].Cells.Single(c => c.Field == "category").Text.ShouldBe("Weißweine");

        // Without group headings (an API client that names none) the heading is not an article either: a value in the
        // article-number column alone is a rejected line.
        var plain = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), new { page = 1, bBox = box }, Ct));
        plain.Report.Rows.ShouldBe(22);
        plain.Report.PerPage[0].RejectedLines.ShouldHaveSingleItem().Reason.ShouldBe(RejectionReasons.AnchorOnly);
        plain.Rows[0].Cells.Single(c => c.Field == "supplierSku").Text.ShouldBe("104007");

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

    /// <summary>The skip lines the portal's table tool sends for the footer texts the reviewer names ("Leave out lines that start with", digits standing for any number).</summary>
    private static readonly string[] FooterSkipLines = [@"^Nordwind\s+Großhandel\s+GmbH\s+·\s+Amtsgericht", @"^Seite\s+\d+\s+von\s+\d+"];

    private async Task<(Guid Id, HttpClient Reviewer)> DemoInReviewAsync(string name)
    {
        var processor = new PdfPigPdfProcessor(Options.Create(new PdfProcessingOptions()));
        await using var file = File.OpenRead(PriceListPath());
        var layer = await processor.ExtractTextAsync(file, Ct);
        var tenant = NewTenant(name);
        var id = await SeedAsync(tenant, layer, pages: 14);
        var reviewer = Reviewer(tenant);
        await SetTypeAsync(reviewer, id);
        return (id, reviewer);
    }

    [Fact]
    public async Task Footer_lines_inside_the_box_are_left_out_by_the_skip_lines_and_left_out_tier_columns_give_no_tiers()
    {
        var (id, reviewer) = await DemoInReviewAsync("Demo price list footers");
        using var _ = reviewer;

        // A box down to the foot of the page holds the footer of every page.
        var box = new { x0 = 45, y0 = 280, x1 = 945, y1 = 1000 };
        var proposal = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), new { page = 1, bBox = box }, Ct));
        proposal.Locale.ShouldBe("de-DE");
        // "(leave out)" for both tier columns, as the portal sends it: no field and no minimum quantity, the header kept.
        var leftOut = proposal.Columns.Select(c => c with { TierMinQuantity = null }).ToList();
        leftOut.Where(c => c.Field is null).Select(c => c.Header).ShouldBe(["ab 10", "ab 50"]);
        object Mapping(string[]? skipRows, string? repeatedLines = null) => new { page = 1, bBox = box, columns = leftOut, headerRow = proposal.HeaderRow, rowAnchor = proposal.RowAnchor, pages = "1-*", groupHeader = new { target = "category" }, skipRows, repeatedLines };

        // The footer is printed at the same place on all 14 pages: left out by itself. Its second line, "Seite n von 14",
        // stands in the "ab 50" column alone, which this mapping leaves out, so it counts as a left-out line.
        var byDefault = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), Mapping(null), Ct));
        byDefault.Report.RejectedRows.ShouldBe(0, string.Join("; ", byDefault.Report.PerPage.SelectMany(p => p.RejectedLines).Select(r => $"p. {r.Page}: {r.Text} ({r.Reason}: {r.Message})")));
        byDefault.Report.Rows.ShouldBe(412);
        byDefault.Report.PerPage.Select(p => p.Rows).ShouldBe(RowsPerPage);
        byDefault.Report.PerPage.ShouldAllBe(p => p.RepeatedLines == 1 && p.LeftOutLines == 1);
        byDefault.Rows.ShouldAllBe(r => !r.Cells.Any(c => c.Text.Contains("Seite", StringComparison.Ordinal) || c.Text.Contains("Amtsgericht", StringComparison.Ordinal)));
        byDefault.Rows.ShouldAllBe(r => r.Tiers.Count == 0, "a tier column left out yields no tiers");

        // With the proposal's tier columns both footer lines are running lines (docs/09 "Demo: manual mapping", step 6).
        var tiered = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), new { page = 1, bBox = box, columns = proposal.Columns, headerRow = proposal.HeaderRow, rowAnchor = proposal.RowAnchor, pages = "1-*", groupHeader = new { target = "category" } }, Ct));
        tiered.Report.RejectedRows.ShouldBe(0);
        tiered.Report.Rows.ShouldBe(412);
        tiered.Report.PerPage.ShouldAllBe(p => p.RepeatedLines == 2 && p.LeftOutLines == 0);

        // Kept (repeatedLines: keep), the footer of every page is a line that is not a row.
        var withFooters = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), Mapping(null, "keep"), Ct));
        var footers = withFooters.Report.PerPage.SelectMany(p => p.RejectedLines).ToList();
        footers.Count.ShouldBe(14, "the footer of every page is a line that is not a row");
        footers.ShouldAllBe(r => !r.Message.Contains(" column 5 column", StringComparison.Ordinal) && !r.Message.Contains(" column 6 column", StringComparison.Ordinal));
        footers.Where(r => r.Reason == "continuationConflict").ShouldAllBe(r => r.Field != null || r.Message.Contains("in column ", StringComparison.Ordinal));
        withFooters.Report.PerPage.ShouldAllBe(p => p.RepeatedLines == 0);

        var skipped = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), Mapping(FooterSkipLines, "keep"), Ct));
        skipped.Report.RejectedRows.ShouldBe(0, string.Join("; ", skipped.Report.PerPage.SelectMany(p => p.RejectedLines).Select(r => $"p. {r.Page}: {r.Text} ({r.Reason}: {r.Message})")));
        skipped.Report.Rows.ShouldBe(412);
        skipped.Report.PerPage.Select(p => p.Rows).ShouldBe(RowsPerPage);
        skipped.Report.PerPage.ShouldAllBe(p => p.SkippedLines == 2);
        skipped.Rows.ShouldAllBe(r => r.Tiers.Count == 0);
        skipped.Rows.ShouldAllBe(r => !r.Cells.Any(c => c.Text.Contains("Seite", StringComparison.Ordinal)));

        // A bottom page margin above the footer rule does the same on the pages after the first.
        var margins = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), new { page = 1, bBox = box with { y1 = 935 }, columns = proposal.Columns, headerRow = proposal.HeaderRow, rowAnchor = proposal.RowAnchor, pages = "1-*", groupHeader = new { target = "category" }, pageMargins = new { bottom = 935 } }, Ct));
        margins.Report.RejectedRows.ShouldBe(0);
        margins.Report.Rows.ShouldBe(412);
        margins.Report.PerPage.ShouldAllBe(p => p.Bottom == 935);
        margins.Rows.Count(r => r.Tiers.Count > 0).ShouldBeGreaterThan(0, "the proposal's tier columns are tiers");
    }

    [Fact]
    public async Task A_box_around_the_header_row_only_reads_every_page_down_to_its_foot()
    {
        var (id, reviewer) = await DemoInReviewAsync("Demo price list header box");
        using var _ = reviewer;

        // docs/11 §12.4 step 1: "or only around its header row".
        var box = new { x0 = 45, y0 = 280, x1 = 945, y1 = 300 };
        var proposal = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), new { page = 1, bBox = box }, Ct));
        proposal.Columns.Select(c => c.Header).ShouldBe(["Art.-Nr.", "Bezeichnung", "Gebinde", "Preis", "ab 10", "ab 50"]);
        proposal.Report.Rows.ShouldBeGreaterThan(0, "the proposal reads the rows below the header on page 1");

        var mapping = new { page = 1, bBox = box, columns = proposal.Columns, headerRow = proposal.HeaderRow, rowAnchor = proposal.RowAnchor, pages = "1-*", groupHeader = new { target = "category" }, skipRows = FooterSkipLines };
        var read = await Read<ReadTableResponse>(await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), mapping, Ct));
        read.Report.RejectedRows.ShouldBe(0, string.Join("; ", read.Report.PerPage.SelectMany(p => p.RejectedLines).Select(r => $"p. {r.Page}: {r.Text} ({r.Reason}: {r.Message})")));
        read.Report.Rows.ShouldBe(412);
        read.Report.PerPage.Select(p => p.Rows).ShouldBe(RowsPerPage);
        read.Report.PerPage.ShouldAllBe(p => p.Bottom == 1000);
        read.Report.PerPage[0].Top.ShouldNotBeNull().ShouldBe(proposal.HeaderRow!.Y1, 0.01);
    }
}
