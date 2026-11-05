using System.Globalization;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Article = DocProc.Pipeline.Tests.Pdf.DemoPriceList.Article;

namespace DocProc.Pipeline.Tests.Pdf;

/// <summary>
/// The price lists and daily specials of the P5 demos (docs/11 §23 P5), drawn with PDFsharp in the design of the P2
/// Nordwind list (<see cref="DemoPriceList"/>) and committed under <c>fixtures/demo/uploads/tenant-a</c>; like every demo
/// PDF they are redrawn only on request (<c>REGENERATE_DEMO_PDFS=&lt;file name&gt;</c>, <c>DemoFixtureTests</c>). Every
/// value is computed, so the tests know what a layout reads:
/// <list type="bullet">
/// <item><see cref="February"/>: next month's Nordwind list (<c>PL-2027-02</c>, valid from 1 February 2027, "Anzahl
/// Artikel: 412"), with 12 prices changed against January, all within 25 % (<see cref="FebruaryChanges"/>).</item>
/// <item><see cref="March"/>: February's prices with one article up 40 % (<see cref="MarchRaisedArticle"/>).</item>
/// <item><see cref="Specials"/>, <see cref="SpecialsSecond"/>, <see cref="SpecialsDated"/>: two-page "Tagesangebote"
/// lists without a printed date (valid on the day they are received), a second one for the same day, and one printed
/// "gültig am 04.11.2026", which has expired on any later day.</item>
/// <item><see cref="SuedhofJanuary"/>, <see cref="SuedhofFebruary"/> (Südhof Feinkost, a supplier without layouts)
/// and <see cref="UnknownSender"/> (Bergquell, no supplier of the tenant): short price lists for the tenant fallback
/// identification rule.</item>
/// </list>
/// </summary>
internal static class DemoPriceLists
{
    public sealed record Supplier(string Name, string VatId, string Letterhead, string Footer);

    public static readonly Supplier Nordwind = new(
        DemoPriceList.SupplierName,
        DemoPriceList.SupplierVatId,
        "Getränke und Feinkost für die Gastronomie · Hafenstraße 21 · 20457 Hamburg · Tel. 040 3017 44-0",
        $"{DemoPriceList.SupplierName} · Amtsgericht Hamburg HRB 99812 · USt-IdNr. {DemoPriceList.SupplierVatId} · IBAN DE44 2005 0550 1234 5678 90");

    public static readonly Supplier Suedhof = new(
        "Südhof Feinkost GmbH",
        "DE298765432",
        "Feinkost und Spezialitäten · Am Viktualienmarkt 4 · 80331 München · Tel. 089 2301 55-0",
        "Südhof Feinkost GmbH · Amtsgericht München HRB 204417 · USt-IdNr. DE298765432 · IBAN DE12 7015 0000 0987 6543 21");

    public static readonly Supplier Bergquell = new(
        "Bergquell Getränke KG",
        "DE287654321",
        "Mineralbrunnen und Erfrischungsgetränke · Quellenweg 3 · 83700 Rottach-Egern",
        "Bergquell Getränke KG · Amtsgericht München HRA 90112 · USt-IdNr. DE287654321 · IBAN DE55 7016 0000 0011 2233 44");

    /// <summary>One price list: what its page 1 and running header print, its rows and how they fall on the pages.</summary>
    public sealed record PriceList(
        string FileName,
        Supplier Supplier,
        string Title,
        string ListNumber,
        string ValidFrom,
        string ValidFromPrinted,
        string Date,
        int? DeclaredCount,
        IReadOnlyList<Article> Articles,
        IReadOnlyList<int> RowsPerPage)
    {
        public int PageCount => RowsPerPage.Count;

        public int RowCount => Articles.Count;

        public byte[] Pdf() => Draw(this);
    }

    /// <summary>The 12 articles (by number) whose price February changes, and by how much (all within 25 %).</summary>
    public static readonly IReadOnlyDictionary<int, decimal> FebruaryChanges = new Dictionary<int, decimal>
    {
        [3] = 1.05m, [27] = 1.08m, [58] = 1.10m, [91] = 0.95m, [120] = 1.12m, [151] = 1.06m,
        [188] = 1.20m, [222] = 0.90m, [260] = 1.15m, [301] = 1.07m, [345] = 1.24m, [399] = 1.09m,
    };

    /// <summary>The article March raises by 40 % against February.</summary>
    public const int MarchRaisedArticle = 77;

    public static PriceList February { get; } = new(
        "Nordwind_Preisliste_2027-02.pdf",
        Nordwind,
        "Preisliste Februar 2027",
        "PL-2027-02",
        "2027-02-01",
        "01.02.2027",
        "04.01.2027",
        DemoPriceList.RowCount,
        Reprice(DemoPriceList.Articles, FebruaryChanges),
        DemoPriceList.RowsPerPage);

    public static PriceList March { get; } = new(
        "Nordwind_Preisliste_2027-03.pdf",
        Nordwind,
        "Preisliste März 2027",
        "PL-2027-03",
        "2027-03-01",
        "01.03.2027",
        "01.02.2027",
        DemoPriceList.RowCount,
        Reprice(Reprice(DemoPriceList.Articles, FebruaryChanges), new Dictionary<int, decimal> { [MarchRaisedArticle] = 1.40m }),
        DemoPriceList.RowsPerPage);

    public static PriceList SuedhofJanuary { get; } = new(
        "Suedhof_Preisliste_2027-01.pdf", Suedhof, "Preisliste Januar 2027", "SF-2027-01", "2027-01-01", "01.01.2027", "15.12.2026", null,
        Subset("SF-", 330, 48, 1.0m), [22, 26]);

    public static PriceList SuedhofFebruary { get; } = new(
        "Suedhof_Preisliste_2027-02.pdf", Suedhof, "Preisliste Februar 2027", "SF-2027-02", "2027-02-01", "01.02.2027", "15.01.2027", null,
        Subset("SF-", 330, 48, 1.03m), [22, 26]);

    public static PriceList UnknownSender { get; } = new(
        "Bergquell_Preisliste_2027.pdf", Bergquell, "Preisliste 2027", "BQ-27", "2027-01-01", "01.01.2027", "01.12.2026", null,
        Subset("BQ-", 300, 36, 0.97m), [22, 14]);

    public static IReadOnlyList<PriceList> Lists { get; } = [February, March, SuedhofJanuary, SuedhofFebruary, UnknownSender];

    /// <summary>A copy of the articles with the given prices multiplied (tiers follow the new price).</summary>
    private static List<Article> Reprice(IReadOnlyList<Article> articles, IReadOnlyDictionary<int, decimal> factors) =>
        [.. articles.Select(a => factors.TryGetValue(a.Number, out var factor) ? Priced(a, Math.Round(a.Price * factor, 2)) : a)];

    private static Article Priced(Article article, decimal price) => article with
    {
        Price = price,
        Tier10 = article.Tier10 is null ? null : Math.Round(price * 0.95m, 2),
        Tier50 = article.Tier50 is null ? null : Math.Round(price * 0.9m, 2),
    };

    /// <summary>
    /// <paramref name="count"/> articles of the Nordwind range from number <paramref name="from"/> on, under the article
    /// numbers <c>&lt;prefix&gt;&lt;n&gt;</c>, at <paramref name="factor"/> times their price, without continuation lines.
    /// </summary>
    private static List<Article> Subset(string prefix, int from, int count, decimal factor) =>
        [.. DemoPriceList.Articles.Where(a => a.Number >= from).Take(count).Select((a, i) => Priced(a, Math.Round(a.Price * factor, 2)) with
        {
            Number = i + 1,
            Sku = prefix + (1000 + (i * 3)).ToString(CultureInfo.InvariantCulture),
            Continuation = null,
            ContinuesOnNextPage = false,
            Page = 0,
        })];

    // ------------------------------------------------------------------ daily specials

    /// <summary>One row of a specials list: the special price and, when printed, the regular price ("statt").</summary>
    public sealed record Special(string Sku, string Description, string Pack, decimal Price, decimal? Regular);

    /// <summary>A two-page "Tagesangebote" list; <see cref="ValidOnPrinted"/> null prints no date (valid on the day received).</summary>
    public sealed record SpecialsList(string FileName, string Title, string? ValidOnPrinted, string? ValidOn, IReadOnlyList<Special> Rows, IReadOnlyList<int> RowsPerPage)
    {
        public int PageCount => RowsPerPage.Count;

        public byte[] Pdf() => DrawSpecials(this);
    }

    /// <summary>The rows of a specials list: Nordwind articles at 80 % (and a few with no regular price printed).</summary>
    private static List<Special> SpecialRows(int from, int count) =>
        [.. DemoPriceList.Articles.Where(a => a.Number >= from).Take(count).Select((a, i) =>
            new Special(a.Sku, a.Description, a.Pack, Math.Round(a.Price * 0.8m, 2), i % 7 == 3 ? null : a.Price))];

    public static SpecialsList Specials { get; } = new("Nordwind_Tagesangebote.pdf", "Tagesangebote", null, null, SpecialRows(12, 44), [24, 20]);

    public static SpecialsList SpecialsSecond { get; } = new("Nordwind_Tagesangebote_Nachtrag.pdf", "Tagesangebote Nachtrag", null, null, SpecialRows(200, 30), [18, 12]);

    public static SpecialsList SpecialsDated { get; } = new("Nordwind_Tagesangebote_2026-11-04.pdf", "Tagesangebote", "04.11.2026", "2026-11-04", SpecialRows(12, 44), [24, 20]);

    public static IReadOnlyList<SpecialsList> SpecialsLists { get; } = [Specials, SpecialsSecond, SpecialsDated];

    // ------------------------------------------------------------------ drawing

    // Column edges in points on A4, as in DemoPriceList.
    private const double Sku = 40, Description = 96, Pack = 318, PriceRight = 438, Tier10Right = 494, Tier50Right = 552;
    private const double LineHeight = 15.5;
    private const double FooterRule = 790;

    /// <summary>The page assignment of a list's rows (<see cref="Article.Page"/>), as the drawing lays them out.</summary>
    public static IReadOnlyList<int> PagesOf(PriceList list) =>
        [.. list.RowsPerPage.SelectMany((count, i) => Enumerable.Repeat(i + 1, count))];

    private static byte[] Draw(PriceList list)
    {
        if (list.RowsPerPage.Sum() != list.Articles.Count)
        {
            throw new InvalidOperationException($"{list.FileName}: {list.RowsPerPage.Sum()} rows on the pages, {list.Articles.Count} articles.");
        }

        TestPdfs.EnsureFonts();
        using var document = new PdfDocument();
        document.Info.Title = list.Title;
        document.Info.Author = list.Supplier.Name;
        document.Info.Creator = "NW-Sortiment 7.4";
        document.Info.CreationDate = new DateTime(2026, 11, 2, 7, 30, 0, DateTimeKind.Utc);

        var regular = new XFont(TestPdfs.FontFamily, 9);
        var bold = new XFont(TestPdfs.FontFamily, 9.5, XFontStyleEx.Bold);
        var index = 0;
        string? currentGroup = null;
        for (var pageNumber = 1; pageNumber <= list.PageCount; pageNumber++)
        {
            var page = document.AddPage();
            page.Size = PageSize.A4;
            using var gfx = XGraphics.FromPdfPage(page);
            double y;
            if (pageNumber == 1)
            {
                y = Letterhead(gfx, list.Supplier, list.Date, ("Listen-Nr.", list.ListNumber));
                DrawText(gfx, Sku, 184, new XFont(TestPdfs.FontFamily, 18, XFontStyleEx.Bold), list.Title);
                DrawText(gfx, Sku, 202, new XFont(TestPdfs.FontFamily, 10), $"gültig ab {list.ValidFromPrinted} · ersetzt alle früheren Listen");
                DrawText(gfx, Sku, 216, new XFont(TestPdfs.FontFamily, 9), "Alle Preise in EUR je Gebinde, netto ab Lager. Staffelpreise ab 10 und ab 50 Gebinden einer Sorte.");
                if (list.DeclaredCount is { } count)
                {
                    DrawText(gfx, Sku, 230, new XFont(TestPdfs.FontFamily, 9), $"Anzahl Artikel: {count.ToString(CultureInfo.InvariantCulture)}");
                }

                y = 246;
            }
            else
            {
                DrawText(gfx, Sku, 46, new XFont(TestPdfs.FontFamily, 8.5), $"{list.Supplier.Name} · {list.Title} · gültig ab {list.ValidFromPrinted}");
                y = 74;
            }

            DrawText(gfx, Sku, y, bold, "Art.-Nr.");
            DrawText(gfx, Description, y, bold, "Bezeichnung");
            DrawText(gfx, Pack, y, bold, "Gebinde");
            RightAligned(gfx, PriceRight, y, bold, "Preis");
            RightAligned(gfx, Tier10Right, y, bold, "ab 10");
            RightAligned(gfx, Tier50Right, y, bold, "ab 50");
            Rule(gfx, y + 5);
            y += LineHeight + 2;

            if (pageNumber > 1 && list.Articles[index - 1].ContinuesOnNextPage)
            {
                DrawText(gfx, Description, y, regular, list.Articles[index - 1].Continuation!);
                y += LineHeight;
            }

            for (var r = 0; r < list.RowsPerPage[pageNumber - 1]; r++, index++)
            {
                var article = list.Articles[index];
                if (article.Group != currentGroup)
                {
                    currentGroup = article.Group;
                    DrawText(gfx, Sku, y + 2, new XFont(TestPdfs.FontFamily, 10, XFontStyleEx.Bold), article.Group);
                    y += LineHeight + 4;
                }

                DrawText(gfx, Sku, y, regular, article.Sku);
                DrawText(gfx, Description, y, regular, article.Description);
                DrawText(gfx, Pack, y, regular, article.Pack);
                RightAligned(gfx, PriceRight, y, regular, DemoPriceList.Amount(article.Price));
                if (article.Tier10 is { } t10)
                {
                    RightAligned(gfx, Tier10Right, y, regular, DemoPriceList.Amount(t10));
                }

                if (article.Tier50 is { } t50)
                {
                    RightAligned(gfx, Tier50Right, y, regular, DemoPriceList.Amount(t50));
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
                throw new InvalidOperationException($"Page {pageNumber} of {list.FileName} runs into its footer ({y:0} pt).");
            }

            Footer(gfx, list.Supplier, pageNumber, list.PageCount);
        }

        return Save(document);
    }

    // Specials columns: number, description, pack, the special price and the regular price.
    private const double SpecialRight = 470, RegularRight = 552;

    private static byte[] DrawSpecials(SpecialsList list)
    {
        if (list.RowsPerPage.Sum() != list.Rows.Count)
        {
            throw new InvalidOperationException($"{list.FileName}: {list.RowsPerPage.Sum()} rows on the pages, {list.Rows.Count} rows.");
        }

        TestPdfs.EnsureFonts();
        using var document = new PdfDocument();
        document.Info.Title = list.Title;
        document.Info.Author = Nordwind.Name;
        document.Info.Creator = "NW-Sortiment 7.4";
        document.Info.CreationDate = new DateTime(2026, 11, 2, 5, 40, 0, DateTimeKind.Utc);

        var regular = new XFont(TestPdfs.FontFamily, 9);
        var bold = new XFont(TestPdfs.FontFamily, 9.5, XFontStyleEx.Bold);
        var index = 0;
        for (var pageNumber = 1; pageNumber <= list.PageCount; pageNumber++)
        {
            var page = document.AddPage();
            page.Size = PageSize.A4;
            using var gfx = XGraphics.FromPdfPage(page);
            double y;
            if (pageNumber == 1)
            {
                Letterhead(gfx, Nordwind, null, ("Kunden-Nr.", "40 1187"));
                DrawText(gfx, Sku, 184, new XFont(TestPdfs.FontFamily, 18, XFontStyleEx.Bold), list.Title);
                DrawText(gfx, Sku, 202, new XFont(TestPdfs.FontFamily, 10), list.ValidOnPrinted is { } on ? $"gültig am {on} · solange Vorrat reicht" : "Angebot des Tages · nur heute, solange Vorrat reicht");
                DrawText(gfx, Sku, 216, new XFont(TestPdfs.FontFamily, 9), "Alle Preise in EUR je Gebinde, netto ab Lager. Bestellung bis 10 Uhr, Lieferung am selben Tag.");
                y = 246;
            }
            else
            {
                DrawText(gfx, Sku, 46, new XFont(TestPdfs.FontFamily, 8.5), $"{Nordwind.Name} · {list.Title}");
                y = 74;
            }

            DrawText(gfx, Sku, y, bold, "Art.-Nr.");
            DrawText(gfx, Description, y, bold, "Bezeichnung");
            DrawText(gfx, Pack, y, bold, "Gebinde");
            RightAligned(gfx, SpecialRight, y, bold, "Angebot");
            RightAligned(gfx, RegularRight, y, bold, "statt");
            Rule(gfx, y + 5);
            y += LineHeight + 2;
            for (var r = 0; r < list.RowsPerPage[pageNumber - 1]; r++, index++)
            {
                var row = list.Rows[index];
                DrawText(gfx, Sku, y, regular, row.Sku);
                DrawText(gfx, Description, y, regular, row.Description);
                DrawText(gfx, Pack, y, regular, row.Pack);
                RightAligned(gfx, SpecialRight, y, regular, DemoPriceList.Amount(row.Price));
                if (row.Regular is { } before)
                {
                    RightAligned(gfx, RegularRight, y, regular, DemoPriceList.Amount(before));
                }

                y += LineHeight;
            }

            Footer(gfx, Nordwind, pageNumber, list.PageCount);
        }

        return Save(document);
    }

    private static double Letterhead(XGraphics gfx, Supplier supplier, string? date, (string Label, string Value) number)
    {
        DrawText(gfx, Sku, 56, new XFont(TestPdfs.FontFamily, 16, XFontStyleEx.Bold), supplier.Name);
        DrawText(gfx, Sku, 72, new XFont(TestPdfs.FontFamily, 8.5), supplier.Letterhead);
        Rule(gfx, 80);
        DrawText(gfx, Sku, 112, new XFont(TestPdfs.FontFamily, 10), "Gasthaus zur Post GmbH");
        DrawText(gfx, Sku, 126, new XFont(TestPdfs.FontFamily, 10), "Hauptstraße 12");
        DrawText(gfx, Sku, 140, new XFont(TestPdfs.FontFamily, 10), "83022 Rosenheim");
        Info(gfx, 112, "Kunden-Nr.", "40 1187");
        if (date is not null)
        {
            Info(gfx, 126, "Datum", date);
        }

        if (number.Label != "Kunden-Nr.")
        {
            Info(gfx, 140, number.Label, number.Value);
        }

        return 246;
    }

    private static void Footer(XGraphics gfx, Supplier supplier, int page, int pages)
    {
        Rule(gfx, FooterRule);
        DrawText(gfx, Sku, 802, new XFont(TestPdfs.FontFamily, 7.5), supplier.Footer);
        RightAligned(gfx, Tier50Right, 818, new XFont(TestPdfs.FontFamily, 8), $"Seite {page.ToString(CultureInfo.InvariantCulture)} von {pages.ToString(CultureInfo.InvariantCulture)}");
    }

    private static byte[] Save(PdfDocument document)
    {
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
