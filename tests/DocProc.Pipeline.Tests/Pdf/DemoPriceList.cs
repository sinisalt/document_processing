using System.Globalization;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DocProc.Pipeline.Tests.Pdf;

/// <summary>
/// The Nordwind price list of the P2 demo (docs/11 §23, docs/09 "Demo: manual mapping"): 14 A4 pages, 412 articles in
/// nine groups, drawn once with PDFsharp and committed under <c>fixtures/demo/uploads/tenant-a</c> like the other demo
/// PDFs (<see cref="DemoPdfs"/>). It has what a long supplier list has: the column header repeated on every page under a
/// running header, rows that continue across a page break, a few continuation lines under a row, group headings in the
/// article number column ("Rotweine"), and tier price columns "ab 10" and "ab 50" filled for some rows only. Every value
/// is computed from the row number, so the expected rows are known to the tests (<see cref="Articles"/>).
/// </summary>
internal static class DemoPriceList
{
    public const string FileName = "Nordwind_Preisliste_2027.pdf";
    public const int PageCount = 14;
    public const int RowCount = 412;

    /// <summary>The validity printed in the title area ("gültig ab 01.01.2027").</summary>
    public const string ValidFrom = "2027-01-01";

    public const string SupplierName = "Nordwind Großhandel GmbH";
    public const string SupplierVatId = "DE312456789";

    /// <summary>Rows that start on each page: fewer on page 1 under the letterhead, then 30 a page.</summary>
    public static readonly int[] RowsPerPage = [22, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30];

    /// <summary>The groups in print order and how many articles each has (412 together).</summary>
    public static readonly (string Heading, int Count)[] Groups =
    [
        ("Weißweine", 60), ("Rotweine", 70), ("Roséweine", 25), ("Schaumweine und Sekt", 35), ("Spirituosen", 55),
        ("Bier", 45), ("Alkoholfreie Getränke", 60), ("Mineralwasser", 37), ("Feinkost", 25),
    ];

    // Column left edges (and right edges of right-aligned columns) in points on A4 (595 × 842).
    private const double Sku = 40, Description = 96, Pack = 318, PriceRight = 438, Tier10Right = 494, Tier50Right = 552;
    private const double LineHeight = 15.5;
    private const double FooterRule = 790;

    /// <summary>One article as printed: number, description, continuation line (if any), pack, price and tiers.</summary>
    public sealed record Article(int Number, string Group, string Sku, string Description, string? Continuation, string Pack, decimal Price, decimal? Tier10, decimal? Tier50, int Page, bool ContinuesOnNextPage);

    private static readonly string[] Grapes = ["Riesling", "Grauburgunder", "Silvaner", "Müller-Thurgau", "Weißburgunder", "Chardonnay", "Sauvignon Blanc", "Grüner Veltliner"];
    private static readonly string[] Reds = ["Spätburgunder", "Dornfelder", "Lemberger", "Trollinger", "Merlot", "Cabernet Sauvignon", "Zweigelt", "Primitivo"];
    private static readonly string[] Styles = ["trocken", "feinherb", "halbtrocken", "Kabinett trocken", "Spätlese"];
    private static readonly string[] Regions = ["Pfalz", "Rheinhessen", "Mosel", "Baden", "Franken", "Württemberg", "Nahe"];
    private static readonly string[] Spirits = ["Obstbrand Williams", "Kirschwasser", "Gin Dry", "Korn 32 %", "Rum braun", "Kräuterlikör", "Wodka", "Whisky Single Malt", "Enzian", "Himbeergeist", "Marillenbrand"];
    private static readonly string[] Beers = ["Helles", "Pils", "Weißbier hell", "Weißbier dunkel", "Dunkles", "Radler", "Märzen", "Bock", "Alkoholfrei Pils"];
    private static readonly string[] SoftDrinks = ["Apfelschorle", "Orangensaft", "Apfelsaft naturtrüb", "Cola", "Zitronenlimonade", "Johannisbeernektar", "Eistee Pfirsich", "Ingwerlimonade", "Rhabarberschorle", "Tomatensaft"];
    private static readonly string[] Waters = ["Mineralwasser classic", "Mineralwasser medium", "Mineralwasser still", "Heilwasser", "Tafelwasser"];
    private static readonly string[] Delicatessen = ["Olivenöl nativ extra", "Balsamico di Modena", "Senf mittelscharf", "Essiggurken", "Kapern", "Sardellenfilets", "Pesto Genovese", "Honig Akazie", "Salzbrezeln", "Erdnüsse geröstet"];

    /// <summary>Every article in print order, with the page it starts on.</summary>
    public static IReadOnlyList<Article> Articles { get; } = BuildArticles(); // after the word lists: static fields initialise in order

    private static List<Article> BuildArticles()
    {
        var articles = new List<Article>(RowCount);
        var number = 0;
        foreach (var (heading, count) in Groups)
        {
            for (var i = 0; i < count; i++)
            {
                number++;
                var (description, pack) = heading switch
                {
                    "Weißweine" => ($"{Grapes[i % Grapes.Length]} {Styles[i % Styles.Length]} {2023 + (i % 3)}", "6 x 0,75 l"),
                    "Rotweine" => ($"{Reds[i % Reds.Length]} {Regions[i % Regions.Length]} {2021 + (i % 4)}", i % 5 == 4 ? "1 x 1,5 l" : "6 x 0,75 l"),
                    "Roséweine" => ($"{Reds[i % Reds.Length]} Rosé {Styles[i % 3]}", "6 x 0,75 l"),
                    "Schaumweine und Sekt" => ($"{(i % 2 == 0 ? "Winzersekt" : "Crémant")} {Grapes[i % Grapes.Length]} brut", "6 x 0,75 l"),
                    "Spirituosen" => ($"{Spirits[i % Spirits.Length]} {(i % 3 == 0 ? "40 %" : "38 %")}", i % 4 == 0 ? "1 x 0,5 l" : "1 x 0,7 l"),
                    "Bier" => ($"{Beers[i % Beers.Length]} Brauerei Nr. {1 + (i / Beers.Length)}", i % 2 == 0 ? "20 x 0,5 l" : "24 x 0,33 l"),
                    "Alkoholfreie Getränke" => ($"{SoftDrinks[i % SoftDrinks.Length]}", i % 3 == 0 ? "6 x 1,0 l" : "12 x 0,2 l"),
                    "Mineralwasser" => ($"{Waters[i % Waters.Length]} Quelle {1 + (i / Waters.Length)}", i % 2 == 0 ? "12 x 0,7 l" : "6 x 1,0 l PET"),
                    _ => ($"{Delicatessen[i % Delicatessen.Length]}", i % 2 == 0 ? "6 x 250 g" : "12 x 190 ml"),
                };

                // Prices in cents from the row number: varied, always positive, always two decimals.
                var cents = 199 + ((number * 739) % 3800);
                var price = cents / 100m;
                decimal? tier10 = number % 3 != 0 ? Math.Round(price * 0.95m, 2) : null;
                decimal? tier50 = number % 4 == 1 ? Math.Round(price * 0.9m, 2) : null;
                // A few long descriptions continue on a second line (number 7, 46, 85, ...).
                var continuation = number % 39 == 7 ? (heading.EndsWith("weine", StringComparison.Ordinal) ? "im Holzfass gereift, limitiert" : "Aktionsware, solange Vorrat reicht") : null;
                articles.Add(new Article(number, heading, (104000 + number * 7).ToString(CultureInfo.InvariantCulture), description, continuation, pack, price, tier10, tier50, 0, false));
            }
        }

        // Page assignment: RowsPerPage rows start on each page. The last row of pages 4 and 9 has a continuation line
        // printed at the top of the next page, under its header (a row carried across a page break).
        var index = 0;
        for (var page = 1; page <= PageCount; page++)
        {
            for (var r = 0; r < RowsPerPage[page - 1]; r++, index++)
            {
                var carried = (page == 4 || page == 9) && r == RowsPerPage[page - 1] - 1;
                var article = articles[index];
                articles[index] = article with
                {
                    Page = page,
                    ContinuesOnNextPage = carried,
                    Continuation = carried ? "Restposten, Nachlieferung nicht möglich" : article.Continuation,
                };
            }
        }

        return articles;
    }

    /// <summary>A description as the table reads it: the printed line plus its continuation line, joined by a space.</summary>
    public static string FullDescription(Article article) => article.Continuation is null ? article.Description : $"{article.Description} {article.Continuation}";

    public static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>The PDF (PDFsharp; a new document id on every call, so the committed bytes are kept, see <see cref="DemoPdfs"/>).</summary>
    public static byte[] Pdf()
    {
        TestPdfs.EnsureFonts();
        using var document = new PdfDocument();
        document.Info.Title = "Preisliste 2027";
        document.Info.Author = SupplierName;
        document.Info.Creator = "NW-Sortiment 7.4";
        document.Info.CreationDate = new DateTime(2026, 11, 2, 7, 30, 0, DateTimeKind.Utc);

        var regular = new XFont(TestPdfs.FontFamily, 9);
        var bold = new XFont(TestPdfs.FontFamily, 9.5, XFontStyleEx.Bold);
        var index = 0;
        string? currentGroup = null;
        for (var pageNumber = 1; pageNumber <= PageCount; pageNumber++)
        {
            var page = document.AddPage();
            page.Size = PageSize.A4;
            using var gfx = XGraphics.FromPdfPage(page);
            double y;
            if (pageNumber == 1)
            {
                DrawText(gfx, Sku, 56, new XFont(TestPdfs.FontFamily, 16, XFontStyleEx.Bold), SupplierName);
                DrawText(gfx, Sku, 72, new XFont(TestPdfs.FontFamily, 8.5), "Getränke und Feinkost für die Gastronomie · Hafenstraße 21 · 20457 Hamburg · Tel. 040 3017 44-0");
                Rule(gfx, 80);
                DrawText(gfx, Sku, 112, new XFont(TestPdfs.FontFamily, 10), "Gasthaus zur Post GmbH");
                DrawText(gfx, Sku, 126, new XFont(TestPdfs.FontFamily, 10), "Hauptstraße 12");
                DrawText(gfx, Sku, 140, new XFont(TestPdfs.FontFamily, 10), "83022 Rosenheim");
                Info(gfx, 112, "Kunden-Nr.", "40 1187");
                Info(gfx, 126, "Datum", "02.11.2026");
                Info(gfx, 140, "Listen-Nr.", "PL-2027-01");
                DrawText(gfx, Sku, 184, new XFont(TestPdfs.FontFamily, 18, XFontStyleEx.Bold), "Preisliste 2027");
                DrawText(gfx, Sku, 202, new XFont(TestPdfs.FontFamily, 10), "gültig ab 01.01.2027 · ersetzt alle früheren Listen");
                DrawText(gfx, Sku, 216, new XFont(TestPdfs.FontFamily, 9), "Alle Preise in EUR je Gebinde, netto ab Lager Hamburg. Staffelpreise ab 10 und ab 50 Gebinden einer Sorte.");
                y = 246;
            }
            else
            {
                DrawText(gfx, Sku, 46, new XFont(TestPdfs.FontFamily, 8.5), $"{SupplierName} · Preisliste 2027 · gültig ab 01.01.2027");
                y = 74;
            }

            // The column header, repeated on every page.
            DrawText(gfx, Sku, y, bold, "Art.-Nr.");
            DrawText(gfx, Description, y, bold, "Bezeichnung");
            DrawText(gfx, Pack, y, bold, "Gebinde");
            RightAligned(gfx, PriceRight, y, bold, "Preis");
            RightAligned(gfx, Tier10Right, y, bold, "ab 10");
            RightAligned(gfx, Tier50Right, y, bold, "ab 50");
            Rule(gfx, y + 5);
            y += LineHeight + 2;

            if (pageNumber > 1 && Articles[index - 1].ContinuesOnNextPage)
            {
                DrawText(gfx, Description, y, regular, Articles[index - 1].Continuation!);
                y += LineHeight;
            }

            for (var r = 0; r < RowsPerPage[pageNumber - 1]; r++, index++)
            {
                var article = Articles[index];
                if (article.Group != currentGroup)
                {
                    currentGroup = article.Group;
                    DrawText(gfx, Sku, y + 2, new XFont(TestPdfs.FontFamily, 10, XFontStyleEx.Bold), article.Group);
                    y += LineHeight + 4;
                }

                DrawText(gfx, Sku, y, regular, article.Sku);
                DrawText(gfx, Description, y, regular, article.Description);
                DrawText(gfx, Pack, y, regular, article.Pack);
                RightAligned(gfx, PriceRight, y, regular, Amount(article.Price));
                if (article.Tier10 is { } t10)
                {
                    RightAligned(gfx, Tier10Right, y, regular, Amount(t10));
                }

                if (article.Tier50 is { } t50)
                {
                    RightAligned(gfx, Tier50Right, y, regular, Amount(t50));
                }

                y += LineHeight;
                if (article.Continuation is { } more && !article.ContinuesOnNextPage)
                {
                    DrawText(gfx, Description, y, regular, more);
                    y += LineHeight;
                }
            }

            if (y > FooterRule - 8)
            {
                throw new InvalidOperationException($"Page {pageNumber} of the demo price list runs into its footer ({y:0} pt).");
            }

            Rule(gfx, FooterRule);
            DrawText(gfx, Sku, 802, new XFont(TestPdfs.FontFamily, 7.5), $"{SupplierName} · Amtsgericht Hamburg HRB 99812 · USt-IdNr. {SupplierVatId} · IBAN DE44 2005 0550 1234 5678 90");
            RightAligned(gfx, Tier50Right, 818, new XFont(TestPdfs.FontFamily, 8), $"Seite {pageNumber} von {PageCount}");
        }

        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static void Info(XGraphics gfx, double baseline, string label, string value)
    {
        var font = new XFont(TestPdfs.FontFamily, 10);
        DrawText(gfx, 360, baseline, font, label);
        RightAligned(gfx, Tier50Right, baseline, font, value);
    }

    private static void Rule(XGraphics gfx, double y) => gfx.DrawLine(new XPen(XColors.Gray, 0.5), Sku, y, Tier50Right, y);

    private static void DrawText(XGraphics gfx, double x, double baseline, XFont font, string text) =>
        gfx.DrawString(text, font, XBrushes.Black, x, baseline);

    private static void RightAligned(XGraphics gfx, double right, double baseline, XFont font, string text)
    {
        var width = gfx.MeasureString(text, font).Width;
        gfx.DrawString(text, font, XBrushes.Black, right - width, baseline);
    }
}
