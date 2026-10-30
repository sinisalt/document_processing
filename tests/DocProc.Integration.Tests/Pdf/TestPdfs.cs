using System.Text;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using SkiaSharp;

namespace DocProc.Integration.Tests.Pdf;

/// <summary>
/// Test PDFs generated at test time with PDFsharp (no binary fixtures in the repository). Layout is A4 in PDF points;
/// y values are baselines measured from the top, as PDFsharp draws them.
/// </summary>
internal static class TestPdfs
{
    public const double PageWidth = 595;
    public const double PageHeight = 842;
    public const string FontFamily = "Test Sans";
    public const string InvoiceNumber = "RE-2026-0042";
    public const string DeliveryNoteNumber = "LS-2026-0917";
    public const string EncryptedPassword = "secret";

    /// <summary>A minimal CII document; stage 6a parses the real thing, stage 2 only needs the root element.</summary>
    public static readonly byte[] FacturXXml = Encoding.UTF8.GetBytes(
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100"
                                  xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100">
          <rsm:ExchangedDocument><ram:ID>RE-2026-0042</ram:ID><ram:TypeCode>380</ram:TypeCode></rsm:ExchangedDocument>
        </rsm:CrossIndustryInvoice>
        """);

    public static readonly byte[] UblInvoiceXml = Encoding.UTF8.GetBytes(
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
                 xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
          <cbc:ID>INV-7</cbc:ID>
        </Invoice>
        """);

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

    private static readonly Line[] DeliveryNotePage =
    [
        new(60, 10, "Musterfirma GmbH · Industriestraße 12 · 80339 München"),
        new(120, 22, "Lieferschein"),
        new(160, 11, $"Lieferschein-Nr. {DeliveryNoteNumber}"),
        new(176, 11, "Lieferdatum 12.03.2026"),
        new(192, 11, "Die Rechnung folgt separat."),
        new(250, 11, "Pos Artikel Menge"),
        new(268, 11, "1 Schraube M8 100 Stk"),
        new(284, 11, "2 Mutter M8 100 Stk"),
        new(800, 9, "Seite 1/1"),
    ];

    /// <summary>Two-page German invoice with header, labels, a line-item table and totals.</summary>
    public static byte[] GermanInvoice() => Build(doc =>
    {
        AddTextPage(doc, InvoicePage1);
        AddTextPage(doc, InvoicePage2);
    });

    /// <summary>The invoice above followed by its delivery note: pages 1–2 and page 3 are separate documents.</summary>
    public static byte[] InvoiceWithDeliveryNote() => Build(doc =>
    {
        AddTextPage(doc, InvoicePage1);
        AddTextPage(doc, InvoicePage2);
        AddTextPage(doc, DeliveryNotePage);
    });

    /// <summary>One-page invoice with a Factur-X attachment and a decoy text attachment.</summary>
    public static byte[] FacturXInvoice() => Build(doc =>
    {
        AddTextPage(doc, InvoicePage1);
        doc.AddEmbeddedFile("readme.txt", new MemoryStream(Encoding.UTF8.GetBytes("not an invoice")));
        doc.AddEmbeddedFile("factur-x.xml", new MemoryStream(FacturXXml));
    });

    /// <summary>One-page invoice with an e-invoice attachment under a name the finder does not know.</summary>
    public static byte[] InvoiceWithUnknownXmlName(string fileName, byte[] xml) => Build(doc =>
    {
        AddTextPage(doc, InvoicePage1);
        doc.AddEmbeddedFile(fileName, new MemoryStream(xml));
    });

    /// <summary>
    /// One-page invoice whose attachment sits in the name tree under an opaque key (a counter, a UUID) while the real
    /// file name only lives in the file specification (<c>/F</c>, <c>/UF</c>), as PDF 32000 7.11.4 allows.
    /// PDFsharp's <c>AddEmbeddedFile</c> always uses the file name as key, so the tree is written by hand here.
    /// </summary>
    public static byte[] InvoiceWithAttachmentUnderOpaqueKey(string key, string fileName, byte[] xml) => Build(doc =>
    {
        AddTextPage(doc, InvoicePage1);

        var embeddedFile = new PdfEmbeddedFileStream(doc, new MemoryStream(xml));
        var specification = new PdfFileSpecification(doc, embeddedFile, fileName);
        doc.Internals.AddObject(specification);

        var names = new PdfArray(doc);
        names.Elements.Add(new PdfString(key));
        names.Elements.Add(specification.Reference!);
        var embeddedFiles = new PdfDictionary(doc);
        embeddedFiles.Elements["/Names"] = names;
        var nameDictionary = new PdfDictionary(doc);
        nameDictionary.Elements["/EmbeddedFiles"] = embeddedFiles;
        doc.Internals.Catalog.Elements["/Names"] = nameDictionary;
    });

    /// <summary>A scanned page: one full-page raster image and no text objects, the "needs OCR" path.</summary>
    public static byte[] ImageOnlyPage() => Build(doc =>
    {
        var page = doc.AddPage();
        page.Size = PageSize.A4;
        using var gfx = XGraphics.FromPdfPage(page);
        using var image = XImage.FromStream(new MemoryStream(ScannedPagePng()));
        gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
    });

    /// <summary>Password-protected invoice (user password set), which no stage may process.</summary>
    public static byte[] EncryptedInvoice() => Build(doc =>
    {
        AddTextPage(doc, InvoicePage1);
        doc.SecuritySettings.UserPassword = EncryptedPassword;
    });

    /// <summary>
    /// Invoice with an owner password only (print and copy restricted, no user password): readers open it without
    /// asking, so every stage must process it. Very common for supplier invoices.
    /// </summary>
    public static byte[] OwnerPasswordProtectedInvoice() => Build(doc =>
    {
        AddTextPage(doc, InvoicePage1);
        AddTextPage(doc, InvoicePage2);
        doc.SecuritySettings.OwnerPassword = "owner-" + EncryptedPassword;
        doc.SecuritySettings.PermitPrint = false;
        doc.SecuritySettings.PermitExtractContent = false;
    });

    public static byte[] NotAPdf() => Encoding.ASCII.GetBytes("%PDF-1.7 this is not a document at all, just a header and some words");

    public static MemoryStream AsStream(byte[] pdf) => new(pdf, writable: false);

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

    /// <summary>A grey page with black bars where text lines would be: looks like a scan, carries no text layer.</summary>
    private static byte[] ScannedPagePng()
    {
        using var bitmap = new SKBitmap(1240, 1754);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0xF4, 0xF4, 0xF0));
            using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false };
            for (var y = 180; y < 1500; y += 48)
            {
                canvas.DrawRect(120, y, 600 + (y * 7 % 400), 18, ink);
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>PDFsharp 6.5 needs a licence choice before the first document; the AGPL one covers these tests. The font resolver is process-wide.</summary>
    private static void EnsureFonts()
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
