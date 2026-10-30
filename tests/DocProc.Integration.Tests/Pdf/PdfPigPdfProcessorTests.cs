using DocProc.Application.Abstractions;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Pdf;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace DocProc.Integration.Tests.Pdf;

/// <summary>Stage 2 (docs/03) over PdfPig and PDFtoImage; runs without Docker, the natives come with the packages.</summary>
public class PdfPigPdfProcessorTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly PdfPigPdfProcessor _processor = new(Options.Create(new PdfProcessingOptions()));

    [Fact]
    public async Task Inspect_reports_page_count_text_layer_and_producer()
    {
        var info = await _processor.InspectAsync(TestPdfs.AsStream(TestPdfs.GermanInvoice()), TestContext.Current.CancellationToken);

        info.PageCount.ShouldBe(2);
        info.HasTextLayer.ShouldBeTrue();
        info.IsEncrypted.ShouldBeFalse();
        info.Producer.ShouldNotBeNull();
        info.Producer.ShouldContain("PDFsharp");
    }

    [Fact]
    public async Task Inspect_flags_an_image_only_page_as_needing_ocr()
    {
        var info = await _processor.InspectAsync(TestPdfs.AsStream(TestPdfs.ImageOnlyPage()), TestContext.Current.CancellationToken);

        info.PageCount.ShouldBe(1);
        info.HasTextLayer.ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_returns_one_page_per_pdf_page_in_point_coordinates_with_top_left_origin()
    {
        var layer = await _processor.ExtractTextAsync(TestPdfs.AsStream(TestPdfs.GermanInvoice()), TestContext.Current.CancellationToken);

        layer.Source.ShouldBe(TextLayerSource.PdfText);
        layer.Pages.Count.ShouldBe(2);
        layer.Pages.Select(p => p.Number).ShouldBe([1, 2]);

        var first = layer.Pages[0];
        first.Width.ShouldBe(TestPdfs.PageWidth, tolerance: 1);
        first.Height.ShouldBe(TestPdfs.PageHeight, tolerance: 1);
        first.Words.ShouldNotBeEmpty();
        foreach (var word in first.Words)
        {
            word.Text.ShouldNotBeNullOrWhiteSpace();
            word.Box.X0.ShouldBeLessThan(word.Box.X1);
            word.Box.Y0.ShouldBeLessThan(word.Box.Y1);
            word.Box.X0.ShouldBeGreaterThanOrEqualTo(0);
            word.Box.Y0.ShouldBeGreaterThanOrEqualTo(0);
            word.Box.X1.ShouldBeLessThanOrEqualTo(first.Width);
            word.Box.Y1.ShouldBeLessThanOrEqualTo(first.Height);
        }

        // The letterhead is drawn on baseline 60 pt with a 10 pt font: its box starts a little above 60 and nowhere near the bottom.
        first.Words[0].Text.ShouldBe("Musterfirma");
        first.Words[0].Box.Y0.ShouldBeInRange(45, 60);
        first.Words[0].Box.Y1.ShouldBeInRange(55, 65);
        first.Words[0].Box.X0.ShouldBe(50, tolerance: 2);

        // "Rechnung" sits on baseline 120 in a 22 pt font: below the letterhead, above the labels (y grows downwards).
        var title = first.Words.Single(w => w.Text == "Rechnung");
        title.Box.Y0.ShouldBeInRange(95, 120);
        title.Box.Y1.ShouldBeInRange(115, 128);
        title.Box.Height.ShouldBeGreaterThan(first.Words[0].Box.Height);

        var footer = first.Words.Single(w => w.Text == "Seite");
        footer.Box.Y0.ShouldBeGreaterThan(title.Box.Y1);
        footer.Box.Y0.ShouldBeInRange(780, 800);
    }

    [Fact]
    public async Task Extract_puts_words_into_reading_order()
    {
        var layer = await _processor.ExtractTextAsync(TestPdfs.AsStream(TestPdfs.GermanInvoice()), TestContext.Current.CancellationToken);
        var text = layer.Pages[0].PlainText;

        text.ShouldStartWith("Musterfirma GmbH");
        text.ShouldContain($"Rechnungsnummer {TestPdfs.InvoiceNumber}");
        text.ShouldContain("Pos Artikel Menge Einzelpreis Gesamt");
        text.ShouldEndWith("Seite 1/2");
        text.IndexOf("Rechnung ", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("Rechnungsnummer", StringComparison.Ordinal));
        layer.Pages[1].PlainText.ShouldContain("Gesamtbetrag 1.487,50 EUR");
    }

    [Fact]
    public async Task Extract_returns_an_empty_page_for_an_image_only_pdf()
    {
        var layer = await _processor.ExtractTextAsync(TestPdfs.AsStream(TestPdfs.ImageOnlyPage()), TestContext.Current.CancellationToken);

        layer.Pages.Count.ShouldBe(1);
        layer.Pages[0].Words.ShouldBeEmpty();
        layer.Pages[0].Width.ShouldBe(TestPdfs.PageWidth, tolerance: 1);
        layer.WordCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(72, 595, 842)]
    [InlineData(200, 1653, 2339)]
    public async Task Render_produces_a_decodable_png_per_page_at_the_requested_dpi(int dpi, int expectedWidth, int expectedHeight)
    {
        var pdf = TestPdfs.InvoiceWithDeliveryNote();
        TestArtifacts.Save("invoice-with-delivery-note.pdf", pdf);
        var pages = new List<RenderedPage>();
        await foreach (var page in _processor.RenderPagesAsync(TestPdfs.AsStream(pdf), dpi, TestContext.Current.CancellationToken))
        {
            pages.Add(page);
            TestArtifacts.Save($"invoice-with-delivery-note-page{page.PageNumber}-{dpi}dpi.png", page.Png);
        }

        pages.Select(p => p.PageNumber).ShouldBe([1, 2, 3]);
        foreach (var page in pages)
        {
            page.Dpi.ShouldBe(dpi);
            page.Width.ShouldBeInRange(expectedWidth - 2, expectedWidth + 2);
            page.Height.ShouldBeInRange(expectedHeight - 2, expectedHeight + 2);
            page.Png.Span[..PngSignature.Length].ToArray().ShouldBe(PngSignature);

            using var bitmap = SKBitmap.Decode(page.Png.ToArray());
            bitmap.ShouldNotBeNull();
            bitmap.Width.ShouldBe(page.Width);
            bitmap.Height.ShouldBe(page.Height);
        }
    }

    [Fact]
    public async Task Render_draws_the_text_so_the_first_page_is_not_blank()
    {
        RenderedPage? first = null;
        await foreach (var page in _processor.RenderPagesAsync(TestPdfs.AsStream(TestPdfs.GermanInvoice()), 72, TestContext.Current.CancellationToken))
        {
            first = page;
            break;
        }

        first.ShouldNotBeNull();
        using var bitmap = SKBitmap.Decode(first.Png.ToArray());
        var dark = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                if (bitmap.GetPixel(x, y).Red < 128)
                {
                    dark++;
                }
            }
        }

        dark.ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task Render_handles_an_image_only_page()
    {
        var pdf = TestPdfs.ImageOnlyPage();
        TestArtifacts.Save("image-only-scan.pdf", pdf);
        var pages = new List<RenderedPage>();
        await foreach (var page in _processor.RenderPagesAsync(TestPdfs.AsStream(pdf), 72, TestContext.Current.CancellationToken))
        {
            pages.Add(page);
            TestArtifacts.Save("image-only-scan-page1-72dpi.png", page.Png);
        }

        pages.Count.ShouldBe(1);
        pages[0].Width.ShouldBeInRange(593, 597);
        pages[0].Height.ShouldBeInRange(840, 844);
        using var bitmap = SKBitmap.Decode(pages[0].Png.ToArray());
        ((int)bitmap.GetPixel(bitmap.Width / 2, 90).Red).ShouldBeLessThan(64);
        ((int)bitmap.GetPixel(bitmap.Width / 2, 20).Red).ShouldBeGreaterThan(200);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task Render_rejects_dpi_outside_the_allowed_range(int dpi)
    {
        var pdf = TestPdfs.GermanInvoice();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in _processor.RenderPagesAsync(TestPdfs.AsStream(pdf), dpi, TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task Finds_the_factur_x_attachment_and_returns_its_content()
    {
        var xml = await _processor.FindEmbeddedInvoiceXmlAsync(TestPdfs.AsStream(TestPdfs.FacturXInvoice()), TestContext.Current.CancellationToken);

        xml.ShouldNotBeNull();
        xml.FileName.ShouldBe("factur-x.xml");
        xml.Flavour.ShouldBe(EmbeddedInvoiceFlavours.FacturX);
        xml.Content.ToArray().ShouldBe(TestPdfs.FacturXXml);
    }

    [Fact]
    public async Task Recognises_an_e_invoice_attachment_by_its_root_element_when_the_name_is_unknown()
    {
        var cii = await _processor.FindEmbeddedInvoiceXmlAsync(
            TestPdfs.AsStream(TestPdfs.InvoiceWithUnknownXmlName("invoice-data.xml", TestPdfs.FacturXXml)), TestContext.Current.CancellationToken);
        var ubl = await _processor.FindEmbeddedInvoiceXmlAsync(
            TestPdfs.AsStream(TestPdfs.InvoiceWithUnknownXmlName("attachment.xml", TestPdfs.UblInvoiceXml)), TestContext.Current.CancellationToken);

        cii.ShouldNotBeNull();
        cii.FileName.ShouldBe("invoice-data.xml");
        cii.Flavour.ShouldBe(EmbeddedInvoiceFlavours.Cii);
        ubl.ShouldNotBeNull();
        ubl.Flavour.ShouldBe(EmbeddedInvoiceFlavours.Ubl);
    }

    [Fact]
    public async Task Ignores_attachments_that_are_not_e_invoices()
    {
        var notes = "<?xml version=\"1.0\"?><Notes><Note>hello</Note></Notes>"u8.ToArray();

        var none = await _processor.FindEmbeddedInvoiceXmlAsync(
            TestPdfs.AsStream(TestPdfs.InvoiceWithUnknownXmlName("notes.xml", notes)), TestContext.Current.CancellationToken);
        var plain = await _processor.FindEmbeddedInvoiceXmlAsync(TestPdfs.AsStream(TestPdfs.GermanInvoice()), TestContext.Current.CancellationToken);

        none.ShouldBeNull();
        plain.ShouldBeNull();
    }

    [Fact]
    public async Task Encrypted_pdf_fails_with_a_clear_reason()
    {
        var pdf = TestPdfs.EncryptedInvoice();

        var inspect = await Should.ThrowAsync<DocumentProcessingException>(() => _processor.InspectAsync(TestPdfs.AsStream(pdf), TestContext.Current.CancellationToken));
        var extract = await Should.ThrowAsync<DocumentProcessingException>(() => _processor.ExtractTextAsync(TestPdfs.AsStream(pdf), TestContext.Current.CancellationToken));
        var embedded = await Should.ThrowAsync<DocumentProcessingException>(() => _processor.FindEmbeddedInvoiceXmlAsync(TestPdfs.AsStream(pdf), TestContext.Current.CancellationToken));

        inspect.Failure.ShouldBe(DocumentProcessingFailure.EncryptedPdf);
        inspect.Message.ShouldContain("password");
        extract.Failure.ShouldBe(DocumentProcessingFailure.EncryptedPdf);
        embedded.Failure.ShouldBe(DocumentProcessingFailure.EncryptedPdf);
    }

    [Fact]
    public async Task Corrupt_or_empty_input_fails_with_a_clear_reason()
    {
        var corrupt = await Should.ThrowAsync<DocumentProcessingException>(() => _processor.InspectAsync(TestPdfs.AsStream(TestPdfs.NotAPdf()), TestContext.Current.CancellationToken));
        var empty = await Should.ThrowAsync<DocumentProcessingException>(() => _processor.ExtractTextAsync(new MemoryStream(), TestContext.Current.CancellationToken));
        var render = await Should.ThrowAsync<DocumentProcessingException>(async () =>
        {
            await foreach (var _ in _processor.RenderPagesAsync(TestPdfs.AsStream(TestPdfs.NotAPdf()), 72, TestContext.Current.CancellationToken))
            {
            }
        });

        corrupt.Failure.ShouldBe(DocumentProcessingFailure.CorruptPdf);
        corrupt.InnerException.ShouldNotBeNull();
        empty.Failure.ShouldBe(DocumentProcessingFailure.CorruptPdf);
        render.Failure.ShouldBe(DocumentProcessingFailure.CorruptPdf);
    }

    [Fact]
    public async Task Accepts_non_seekable_streams()
    {
        var pdf = TestPdfs.GermanInvoice();
        await using var stream = new NonSeekableStream(pdf);

        var info = await _processor.InspectAsync(stream, TestContext.Current.CancellationToken);

        info.PageCount.ShouldBe(2);
    }

    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
