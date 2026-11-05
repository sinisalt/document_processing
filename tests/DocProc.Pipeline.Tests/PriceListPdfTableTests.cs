using System.Globalization;
using DocProc.Definitions;
using DocProc.Domain.Fields;
using DocProc.Infrastructure.Pdf;
using DocProc.Mapping.Tables;
using DocProc.Mapping.Text;
using DocProc.Mapping.Values;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.Extensions.Options;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The table tool's engine over a real text layer (docs/11 §12.4): a price list PDF generated with PDFsharp and read by
/// the Normalize stage's PdfPig extractor, so the words come in PDF points with PdfPig's own boxes and are normalised as
/// the API does; the proposal on page 1 and "apply to all pages" read every row.
/// </summary>
public sealed class PriceListPdfTableTests
{
    // Column left edges in points on A4 (595 × 842).
    private const double Sku = 40, Description = 100, Content = 300, Price = 380, Tier10 = 450, Tier50 = 515;

    private static byte[] PriceListPdf(int pages, int rowsPerPage, out int total)
    {
        TestPdfs.EnsureFonts();
        using var document = new PdfDocument();
        var font = new XFont(TestPdfs.FontFamily, 9);
        var count = 0;
        for (var p = 1; p <= pages; p++)
        {
            var page = document.AddPage();
            page.Size = PageSize.A4;
            using var gfx = XGraphics.FromPdfPage(page);
            void Text(double x, double baseline, string text) => gfx.DrawString(text, font, XBrushes.Black, new XPoint(x, baseline));
            double y;
            if (p == 1)
            {
                Text(Sku, 50, "Nordwind Großhandel GmbH · Preisliste gültig ab 01.01.2027");
                y = 90;
            }
            else
            {
                Text(Description, 40, $"Preisliste 2027 · Seite {p}");
                y = 70;
            }

            Text(Sku, y, "Art.-Nr.");
            Text(Description, y, "Bezeichnung");
            Text(Content, y, "Inhalt");
            Text(Price, y, "Preis");
            Text(Tier10, y, "ab 10");
            Text(Tier50, y, "ab 50");
            y += 16;
            if (p == 2)
            {
                Text(Sku, y, "Weißweine");
                y += 14;
            }

            for (var r = 0; r < rowsPerPage; r++)
            {
                count++;
                var sku = (100000 + count).ToString(CultureInfo.InvariantCulture);
                Text(Sku, y, sku);
                Text(Description, y, $"Artikel {sku}");
                Text(Content, y, "6 x 0,75 l");
                Text(Price, y, $"{(r % 40) + 1},{r % 100:00}");
                if (r % 3 == 0)
                {
                    Text(Tier10, y, $"{(r % 40) + 1},00");
                    Text(Tier50, y, "0,99");
                }

                y += 14;
                if (r % 9 == 4)
                {
                    Text(Description, y, "trocken, Barrique");
                    y += 14;
                }
            }

            Text(Description, 815, $"Seite {p} von {pages}");
        }

        total = count;
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    [Fact]
    public async Task A_price_list_pdf_is_read_with_one_mapping_drawn_on_page_1()
    {
        var pdf = PriceListPdf(pages: 4, rowsPerPage: 40, out var total);
        var processor = new PdfPigPdfProcessor(Options.Create(new PdfProcessingOptions()));
        using var stream = new MemoryStream(pdf);
        var layer = await processor.ExtractTextAsync(stream, TestContext.Current.CancellationToken);
        var pages = layer.Pages.Select(WordPage.From).ToList();

        // The reviewer draws a box around the header and the first rows of page 1: the proposal names the columns.
        var page1 = pages[0];
        var header = page1.Words.First(w => w.Text == "Bezeichnung").Box;
        var region = new BoundingBox(50, header.Y0 - 5, 990, header.Y1 + 200);
        var fields = new List<ProposableField>
        {
            new("supplierSku", ["Art.-Nr."]),
            new("description", ["Bezeichnung"]),
            new("packDescription", ["Inhalt"]),
            new("price", ["Preis"]),
        };
        var proposal = ColumnProposer.Propose(page1, region, null, fields, new System.Text.RegularExpressions.Regex(@"^ab\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)));
        proposal.Columns.Select(c => c.Field).ShouldBe(["supplierSku", "description", "packDescription", "price", null, null]);
        proposal.Columns.Select(c => c.TierMinQuantity).ShouldBe([null, null, null, null, 10m, 50m]);

        // Applied to all pages, down to the footer.
        var footer = page1.Words.First(w => w.Text == "von").Box.Y0;
        var spec = new ColumnTableSpec
        {
            Page = 1,
            Region = region with { Y1 = footer - 5 },
            Columns = [.. proposal.Columns.Select(c => new TableColumn(c.X0, c.X1, c.Field, c.Header, c.TierMinQuantity))],
            Fields = new Dictionary<string, TableField>(StringComparer.Ordinal)
            {
                ["supplierSku"] = new("supplierSku", FieldKinds.Identifier),
                ["description"] = new("description", FieldKinds.Text),
                ["packDescription"] = new("packDescription", FieldKinds.String),
                ["price"] = new("price", FieldKinds.Money),
                ["category"] = new("category", FieldKinds.String),
            },
            HeaderRow = proposal.HeaderRow,
            RowAnchor = "supplierSku",
            Pages = [1, 2, 3, 4],
            GroupHeader = new GroupHeaderSpec("category"),
            TierColumns = new TierColumnsSpec(null),
            PageMargins = new PageMargins(null, footer - 5),
            Locale = ValueLocale.German,
        };

        var result = ColumnTableEngine.Extract(pages, spec, TestContext.Current.CancellationToken);

        result.Report.RejectedRows.ShouldBe(0, string.Join("; ", result.Report.PerPage.SelectMany(p => p.RejectedLines).Select(r => $"{r.Page}: {r.Text} ({r.Message})")));
        result.Report.Rows.ShouldBe(total);
        result.Report.PerPage.All(p => p.HeaderFound && p.Rows == 40).ShouldBeTrue();
        result.Rows[0].Cell("supplierSku")!.Text.ShouldBe("100001");
        result.Rows[0].Cell("price")!.Value!.GetValue<decimal>().ShouldBe(1.00m);
        result.Rows[0].Tiers.Select(t => t.MinQuantity).ShouldBe([10m, 50m]);
        result.Rows[4].Cell("description")!.Text.ShouldBe("Artikel 100005 trocken, Barrique");
        result.Rows[40].Cell("category")!.Text.ShouldBe("Weißweine");
        result.Rows[39].Cell("category").ShouldBeNull("the heading on page 2 fills the rows below it only");
        result.Rows.Select(r => r.Cell("supplierSku")!.Text).Distinct().Count().ShouldBe(total);
    }
}
