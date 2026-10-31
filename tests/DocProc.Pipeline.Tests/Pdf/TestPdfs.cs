using System.Text;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace DocProc.Pipeline.Tests.Pdf;

/// <summary>
/// Test PDFs generated at test time with PDFsharp, copied from <c>tests/DocProc.Integration.Tests/Pdf/TestPdfs.cs</c>
/// (the subset the pipeline tests need). Layout is A4 in PDF points; y values are baselines from the top.
/// </summary>
internal static class TestPdfs
{
    public const string FontFamily = "Test Sans";
    public const string InvoiceNumber = "RE-2026-0042";
    public const string DeliveryNoteNumber = "LS-2026-0917";

    private static readonly Lock FontLock = new();
    private static bool _fontsReady;

    private static readonly Line[] InvoicePage1 =
    [
        new(60, 10, "Musterfirma GmbH · Industriestraße 12 · 80339 München"),
        new(120, 22, "Rechnung"),
        new(160, 11, $"Rechnungsnummer {InvoiceNumber}"),
        new(176, 11, "Rechnungsdatum 15.03.2026"),
        new(192, 11, "Kundennummer 10234"),
        new(208, 11, $"Lieferschein-Nr. {DeliveryNoteNumber}"),
        new(250, 11, "Pos Artikel Menge Einzelpreis Gesamt"),
        new(268, 11, "1 Schraube M8 100 Stk 0,50 EUR 50,00 EUR"),
        new(284, 11, "2 Mutter M8 100 Stk 0,20 EUR 20,00 EUR"),
        new(300, 11, "3 Montageleistung 10 Std 118,00 EUR 1.180,00 EUR"),
        new(800, 9, "Seite 1/2"),
    ];

    private static readonly Line[] InvoicePage2 =
    [
        new(60, 10, $"Musterfirma GmbH · Rechnung {InvoiceNumber}"),
        new(120, 11, "Übertrag 1.250,00 EUR"),
        new(160, 11, "Nettobetrag 1.250,00 EUR"),
        new(176, 11, "MwSt 19 % 237,50 EUR"),
        new(192, 11, "Gesamtbetrag 1.487,50 EUR"),
        new(230, 11, "Zahlbar innerhalb von 14 Tagen ohne Abzug. Vielen Dank für Ihren Auftrag."),
        new(800, 9, "Seite 2/2"),
    ];

    /// <summary>Two-page German invoice with header, labels, a line-item table and totals.</summary>
    public static byte[] GermanInvoice() => Build(doc =>
    {
        AddTextPage(doc, InvoicePage1);
        AddTextPage(doc, InvoicePage2);
    });

    /// <summary>A batch scan: the invoice's first page alone, then the whole two-page invoice (three pages).</summary>
    public static byte[] SinglePageThenGermanInvoice() => Build(doc =>
    {
        AddTextPage(doc, InvoicePage1);
        AddTextPage(doc, InvoicePage1);
        AddTextPage(doc, InvoicePage2);
    });

    /// <summary>The two-page German invoice with an XML attachment, as a Factur-X / ZUGFeRD PDF/A-3 carries one.</summary>
    public static byte[] GermanInvoiceWithAttachment(string fileName, byte[] xml) => Build(doc =>
    {
        AddTextPage(doc, InvoicePage1);
        AddTextPage(doc, InvoicePage2);
        doc.AddEmbeddedFile(fileName, new MemoryStream(xml));
    });

    /// <summary>Several pages of drawn shapes and no text: a multi-page scan with no text layer.</summary>
    public static byte[] ShapesOnlyPages(int count) => Build(doc =>
    {
        for (var i = 0; i < count; i++)
        {
            var page = doc.AddPage();
            page.Size = PageSize.A4;
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(XBrushes.Black, 50, 50 + (i * 30), 300, 20);
        }
    });

    public static byte[] NotAPdf() => Encoding.ASCII.GetBytes("%PDF-1.7 this is not a document at all, just a header and some words");

    /// <summary>One page of drawn shapes and no text at all: what a scanned invoice looks like to the text extractor.</summary>
    public static byte[] ShapesOnlyPage() => Build(doc =>
    {
        var page = doc.AddPage();
        page.Size = PageSize.A4;
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawRectangle(XBrushes.Black, 50, 50, 300, 20);
        gfx.DrawRectangle(XBrushes.DarkGray, 50, 120, 495, 2);
        for (var row = 0; row < 8; row++)
        {
            gfx.DrawRectangle(XBrushes.Gray, 50, 150 + (row * 24), 420, 10);
        }
    });

    private static byte[] Build(Action<PdfDocument> compose)
    {
        EnsureFonts();
        using var document = new PdfDocument();
        document.Info.Title = "DocProc test fixture";
        compose(document);
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static void AddTextPage(PdfDocument document, IEnumerable<Line> lines)
    {
        var page = document.AddPage();
        page.Size = PageSize.A4;
        using var gfx = XGraphics.FromPdfPage(page);
        foreach (var line in lines)
        {
            var font = new XFont(FontFamily, line.Size);
            gfx.DrawString(line.Text, font, XBrushes.Black, new XPoint(50, line.Baseline));
        }
    }

    /// <summary>PDFsharp 6.5 needs a licence choice before the first document; the AGPL one covers these tests. The font resolver is process-wide.</summary>
    internal static void EnsureFonts()
    {
        lock (FontLock)
        {
            if (_fontsReady)
            {
                return;
            }

            PdfSharpLicense.SetAgplLicense();
            GlobalFontSettings.FontResolver = new TestFonts();
            _fontsReady = true;
        }
    }

    private sealed record Line(double Baseline, double Size, string Text);
}
