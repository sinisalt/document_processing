using System.Globalization;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DocProc.Pipeline.Tests.Pdf;

/// <summary>
/// Nordwind's invoices of the P4 layouts demo (docs/11 §23 P4, §12.10): three in the design of its ERP ("Rechnung" in
/// the title, the VAT id in the letterhead of page 1, one page) and three from its new web shop ("RECHNUNG / INVOICE"
/// top right, bilingual labels in a box, other columns, the totals and the VAT id, spaced, on page 2). Drawn once with
/// PDFsharp and committed under <c>fixtures/demo/uploads/tenant-a</c> like the other demo PDFs (<see cref="DemoPdfs"/>);
/// <c>DemoFixtureTests</c> redraws one only with <c>REGENERATE_DEMO_PDFS=&lt;file name&gt;</c>. Every value is in
/// <see cref="All"/>, so the tests know what a layout must read (the golden outputs).
/// </summary>
internal static class DemoNordwindInvoices
{
    public const string SupplierName = DemoPriceList.SupplierName;
    public const string SupplierVatId = DemoPriceList.SupplierVatId;
    public const string CustomerName = "Gasthaus zur Post GmbH";
    public const decimal VatRate = 19m;

    /// <summary>The page width in points (A4), which the text layer normalises to 1000 units.</summary>
    public const double PageWidth = 595.28;

    // Column edges of the ERP design, in points: left edges of left-aligned columns, right edges of right-aligned ones.
    public const double ErpPos = 40, ErpSku = 66, ErpDescription = 126, ErpQuantityRight = 360, ErpUnit = 368, ErpPriceRight = 470, ErpTotalRight = 545;

    // The web shop's columns.
    public const double WebSku = 40, WebDescription = 120, WebQuantityRight = 380, WebUnit = 388, WebPriceRight = 470, WebTotalRight = 548;

    public enum Design
    {
        Erp,
        WebShop,
    }

    public sealed record Line(int Number, string Sku, string Description, decimal Quantity, string Unit, decimal UnitPrice)
    {
        public decimal Net => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
    }

    public sealed record Invoice(Design Design, string Number, DateOnly Date, IReadOnlyList<Line> Lines)
    {
        public string FileName => Design == Design.Erp ? $"Nordwind_Rechnung_{Number}.pdf" : $"Nordwind_Webshop_Rechnung_{Number}.pdf";

        public decimal Net => Lines.Sum(l => l.Net);

        public decimal Tax => Math.Round(Net * VatRate / 100m, 2, MidpointRounding.AwayFromZero);

        public decimal Gross => Net + Tax;

        public byte[] Pdf() => Design == Design.Erp ? DrawErp(this) : DrawWebShop(this);
    }

    public static IReadOnlyList<Invoice> Erp { get; } =
    [
        new(Design.Erp, "NW-RE-2026-1041", new DateOnly(2026, 10, 12),
        [
            new(1, "100234", "Riesling trocken Pfalz 0,75 l", 24, "Stk", 6.90m),
            new(2, "100410", "Spätburgunder Baden 0,75 l", 12, "Stk", 9.40m),
            new(3, "300120", "Mineralwasser classic 12 x 0,7 l", 10, "Kiste", 7.20m),
            new(4, "500077", "Olivenöl nativ extra 5 l", 2, "Stk", 38.50m),
        ]),
        new(Design.Erp, "NW-RE-2026-1077", new DateOnly(2026, 10, 19),
        [
            new(1, "100235", "Grauburgunder Rheinhessen 0,75 l", 18, "Stk", 7.80m),
            new(2, "200015", "Pils 20 x 0,5 l", 8, "Kiste", 16.90m),
            new(3, "500102", "Balsamico di Modena 1 l", 4, "Stk", 12.60m),
        ]),
        new(Design.Erp, "NW-RE-2026-1102", new DateOnly(2026, 10, 26),
        [
            new(1, "300121", "Mineralwasser medium 12 x 0,7 l", 15, "Kiste", 7.20m),
            new(2, "400310", "Apfelsaft naturtrüb 6 x 1 l", 6, "Karton", 11.40m),
            new(3, "100410", "Spätburgunder Baden 0,75 l", 6, "Stk", 9.40m),
            new(4, "500140", "Kapern 0,5 kg", 3, "Stk", 8.95m),
            new(5, "500141", "Sardellenfilets 1 kg", 2, "kg", 21.30m),
        ]),
    ];

    public static IReadOnlyList<Invoice> WebShop { get; } =
    [
        new(Design.WebShop, "WS-2026-000318", new DateOnly(2026, 10, 23),
        [
            new(1, "100234", "Riesling trocken Pfalz 0,75 l", 6, "Stk", 7.20m),
            new(2, "500077", "Olivenöl nativ extra 5 l", 1, "Stk", 39.90m),
        ]),
        new(Design.WebShop, "WS-2026-000342", new DateOnly(2026, 10, 28),
        [
            new(1, "200015", "Pils 20 x 0,5 l", 3, "Kiste", 17.50m),
            new(2, "300120", "Mineralwasser classic 12 x 0,7 l", 4, "Kiste", 7.50m),
            new(3, "500102", "Balsamico di Modena 1 l", 2, "Stk", 12.90m),
        ]),
        new(Design.WebShop, "WS-2026-000377", new DateOnly(2026, 10, 29),
        [
            new(1, "400310", "Apfelsaft naturtrüb 6 x 1 l", 5, "Karton", 11.90m),
        ]),
    ];

    /// <summary>
    /// One more of each design for the compose demo (docs/09 "Demo: routing and layouts"): the ERP invoice dropped into the
    /// watched folder, and the web-shop invoice the second tenant gets while its layout is still a Candidate. Kept out of
    /// <see cref="Erp"/> and <see cref="WebShop"/>, whose counts the pipeline tests rely on.
    /// </summary>
    public static IReadOnlyList<Invoice> More { get; } =
    [
        new(Design.Erp, "NW-RE-2026-1130", new DateOnly(2026, 11, 2),
        [
            new(1, "100234", "Riesling trocken Pfalz 0,75 l", 12, "Stk", 6.90m),
            new(2, "200015", "Pils 20 x 0,5 l", 10, "Kiste", 16.90m),
            new(3, "500077", "Olivenöl nativ extra 5 l", 1, "Stk", 38.50m),
        ]),
        new(Design.WebShop, "WS-2026-000391", new DateOnly(2026, 11, 3),
        [
            new(1, "100410", "Spätburgunder Baden 0,75 l", 6, "Stk", 9.90m),
            new(2, "500140", "Kapern 0,5 kg", 2, "Stk", 9.20m),
        ]),
    ];

    public static IEnumerable<Invoice> All => Erp.Concat(WebShop).Concat(More);

    /// <summary>A German amount (<c>1.234,50</c>; the build runs with invariant globalization, so the separators are swapped by hand).</summary>
    public static string Amount(decimal value) =>
        value.ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(',', '_').Replace('.', ',').Replace('_', '.');

    private static string Quantity(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');

    private static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static byte[] DrawErp(Invoice invoice) => Build(invoice, "NW-Faktura 9.2", document =>
    {
        using var gfx = NewPage(document);
        Text(gfx, ErpPos, 56, 16, SupplierName, bold: true);
        Text(gfx, ErpPos, 72, 8.5, $"Getränke und Feinkost für die Gastronomie · Hafenstraße 21 · 20457 Hamburg · USt-IdNr. {SupplierVatId}");
        Rule(gfx, 80, ErpPos, ErpTotalRight);
        Text(gfx, ErpPos, 112, 10, CustomerName);
        Text(gfx, ErpPos, 126, 10, "Hauptstraße 12");
        Text(gfx, ErpPos, 140, 10, "83022 Rosenheim");
        Info(gfx, 112, "Rechnungs-Nr.", invoice.Number);
        Info(gfx, 126, "Rechnungsdatum", Date(invoice.Date));
        Info(gfx, 140, "Kunden-Nr.", "40 1187");
        Text(gfx, ErpPos, 184, 18, "Rechnung", bold: true);
        Text(gfx, ErpPos, 202, 10, $"Wir berechnen Ihnen für die Lieferung vom {Date(invoice.Date)}:");

        var y = 232.0;
        var header = new XFont(TestPdfs.FontFamily, 9.5, XFontStyleEx.Bold);
        gfx.DrawString("Pos", header, XBrushes.Black, ErpPos, y);
        gfx.DrawString("Art.-Nr.", header, XBrushes.Black, ErpSku, y);
        gfx.DrawString("Bezeichnung", header, XBrushes.Black, ErpDescription, y);
        RightAligned(gfx, "Menge", header, ErpQuantityRight, y);
        gfx.DrawString("Einheit", header, XBrushes.Black, ErpUnit, y);
        RightAligned(gfx, "Einzelpreis", header, ErpPriceRight, y);
        RightAligned(gfx, "Betrag", header, ErpTotalRight, y);
        Rule(gfx, y + 6, ErpPos, ErpTotalRight);
        y += 22;
        foreach (var line in invoice.Lines)
        {
            Text(gfx, ErpPos, y, 10, line.Number.ToString(CultureInfo.InvariantCulture));
            Text(gfx, ErpSku, y, 10, line.Sku);
            Text(gfx, ErpDescription, y, 10, line.Description);
            RightText(gfx, ErpQuantityRight, y, 10, Quantity(line.Quantity));
            Text(gfx, ErpUnit, y, 10, line.Unit);
            RightText(gfx, ErpPriceRight, y, 10, Amount(line.UnitPrice));
            RightText(gfx, ErpTotalRight, y, 10, Amount(line.Net));
            y += 18;
        }

        Rule(gfx, y - 8, ErpPos, ErpTotalRight);
        Text(gfx, 330, y + 10, 10, "Summe netto");
        RightText(gfx, ErpTotalRight, y + 10, 10, $"{Amount(invoice.Net)} EUR");
        Text(gfx, 330, y + 26, 10, "USt 19 %");
        RightText(gfx, ErpTotalRight, y + 26, 10, $"{Amount(invoice.Tax)} EUR");
        Rule(gfx, y + 34, 330, ErpTotalRight);
        Text(gfx, 330, y + 50, 12, "Gesamtbetrag", bold: true);
        RightText(gfx, ErpTotalRight, y + 50, 12, $"{Amount(invoice.Gross)} EUR", bold: true);
        Text(gfx, ErpPos, y + 96, 10, "Zahlbar innerhalb von 14 Tagen ohne Abzug.");
        Text(gfx, ErpPos, y + 112, 10, $"Bitte geben Sie bei der Überweisung die Rechnungsnummer {invoice.Number} an.");

        Rule(gfx, 790, ErpPos, ErpTotalRight);
        Text(gfx, ErpPos, 802, 7.5, $"{SupplierName} · Amtsgericht Hamburg HRB 99812 · Hamburger Sparkasse · IBAN DE44 2005 0550 1234 5678 90");
        RightText(gfx, ErpTotalRight, 818, 8, "Seite 1 von 1");
    });

    private static byte[] DrawWebShop(Invoice invoice) => Build(invoice, "Nordwind Webshop", document =>
    {
        using (var gfx = NewPage(document))
        {
            Text(gfx, WebSku, 50, 13, "NORDWIND", bold: true);
            Text(gfx, WebSku, 64, 8, "shop.nordwind.example");
            Text(gfx, 330, 54, 17, "RECHNUNG / INVOICE", bold: true);
            gfx.DrawRectangle(new XPen(XColors.Gray, 0.5), 300, 80, 252, 74);
            Text(gfx, 306, 96, 9, "Rechnungsnummer / Invoice no.:");
            RightText(gfx, WebTotalRight, 96, 9, invoice.Number);
            Text(gfx, 306, 112, 9, "Datum / Date:");
            RightText(gfx, WebTotalRight, 112, 9, Date(invoice.Date));
            Text(gfx, 306, 128, 9, "Kundennummer / Customer no.:");
            RightText(gfx, WebTotalRight, 128, 9, "401187");
            Text(gfx, 306, 144, 9, "Bestellung / Order:");
            RightText(gfx, WebTotalRight, 144, 9, $"B-{invoice.Number[^5..]}");

            Text(gfx, WebSku, 100, 8, "Rechnungsadresse / Billing address");
            Text(gfx, WebSku, 116, 10, CustomerName);
            Text(gfx, WebSku, 130, 10, "Hauptstraße 12");
            Text(gfx, WebSku, 144, 10, "83022 Rosenheim");

            var y = 200.0;
            var header = new XFont(TestPdfs.FontFamily, 9, XFontStyleEx.Bold);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(235, 235, 235)), WebSku - 4, y - 11, WebTotalRight - WebSku + 8, 16);
            gfx.DrawString("Artikel-Nr.", header, XBrushes.Black, WebSku, y);
            gfx.DrawString("Beschreibung", header, XBrushes.Black, WebDescription, y);
            RightAligned(gfx, "Anzahl", header, WebQuantityRight, y);
            gfx.DrawString("Einheit", header, XBrushes.Black, WebUnit, y);
            RightAligned(gfx, "Preis", header, WebPriceRight, y);
            RightAligned(gfx, "Summe", header, WebTotalRight, y);
            y += 22;
            foreach (var line in invoice.Lines)
            {
                Text(gfx, WebSku, y, 9.5, line.Sku);
                Text(gfx, WebDescription, y, 9.5, line.Description);
                RightText(gfx, WebQuantityRight, y, 9.5, Quantity(line.Quantity));
                Text(gfx, WebUnit, y, 9.5, line.Unit);
                RightText(gfx, WebPriceRight, y, 9.5, Amount(line.UnitPrice));
                RightText(gfx, WebTotalRight, y, 9.5, Amount(line.Net));
                y += 17;
            }

            Text(gfx, WebSku, 760, 8, "Fortsetzung umseitig / continued overleaf");
            RightText(gfx, WebTotalRight, 818, 8, "Seite 1 von 2");
        }

        using (var gfx = NewPage(document))
        {
            Text(gfx, WebSku, 50, 9, $"NORDWIND Webshop · {invoice.Number}");
            Text(gfx, 330, 90, 10, "Zwischensumme / Subtotal");
            RightText(gfx, WebTotalRight, 90, 10, $"{Amount(invoice.Net)} EUR");
            Text(gfx, 330, 106, 10, "MwSt. 19 % / VAT");
            RightText(gfx, WebTotalRight, 106, 10, $"{Amount(invoice.Tax)} EUR");
            Text(gfx, 330, 126, 12, "Gesamt / Total", bold: true);
            RightText(gfx, WebTotalRight, 126, 12, $"{Amount(invoice.Gross)} EUR", bold: true);
            Text(gfx, WebSku, 170, 9, "Bezahlt per Lastschrift. Paid by direct debit.");

            Rule(gfx, 790, WebSku, WebTotalRight);
            Text(gfx, WebSku, 802, 7.5, $"{SupplierName} · Webshop · Hafenstraße 21 · 20457 Hamburg · USt-IdNr. DE 312 456 789");
            RightText(gfx, WebTotalRight, 818, 8, "Seite 2 von 2");
        }
    });

    private static byte[] Build(Invoice invoice, string creator, Action<PdfDocument> compose)
    {
        TestPdfs.EnsureFonts();
        using var document = new PdfDocument();
        document.Info.Title = $"Rechnung {invoice.Number}";
        document.Info.Author = SupplierName;
        document.Info.Creator = creator;
        document.Info.CreationDate = invoice.Date.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc);
        compose(document);
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static XGraphics NewPage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Size = PageSize.A4;
        return XGraphics.FromPdfPage(page);
    }

    private static void Info(XGraphics gfx, double baseline, string label, string value)
    {
        Text(gfx, 360, baseline, 10, label);
        RightText(gfx, ErpTotalRight, baseline, 10, value);
    }

    private static void Rule(XGraphics gfx, double y, double from, double to) =>
        gfx.DrawLine(new XPen(XColors.Gray, 0.5), from, y, to, y);

    private static void Text(XGraphics gfx, double x, double baseline, double size, string text, bool bold = false) =>
        gfx.DrawString(text, new XFont(TestPdfs.FontFamily, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular), XBrushes.Black, x, baseline);

    private static void RightText(XGraphics gfx, double right, double baseline, double size, string text, bool bold = false) =>
        RightAligned(gfx, text, new XFont(TestPdfs.FontFamily, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular), right, baseline);

    private static void RightAligned(XGraphics gfx, string text, XFont font, double right, double baseline)
    {
        var width = gfx.MeasureString(text, font).Width;
        gfx.DrawString(text, font, XBrushes.Black, right - width, baseline);
    }
}
