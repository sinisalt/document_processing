using DocProc.Application.Abstractions;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Pdf;
using DocProc.Integration.Tests.Pdf;
using Microsoft.Extensions.Options;

namespace DocProc.Integration.Tests.Classification;

/// <summary>Stage 3 heuristics (docs/03 "Split", step 1) over generated PDFs and synthetic text layers.</summary>
public class HeuristicDocumentSplitterTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly IReadOnlyList<ReadOnlyMemory<byte>> NoImages = [];
    private readonly HeuristicDocumentSplitter _splitter = new(Options.Create(new HeuristicSplitterOptions()));

    private static async Task<TextLayer> ExtractAsync(byte[] pdf)
    {
        var processor = new PdfPigPdfProcessor(Options.Create(new PdfProcessingOptions()));
        return await processor.ExtractTextAsync(TestPdfs.AsStream(pdf), TestContext.Current.CancellationToken);
    }

    private static void ShouldCoverAllPages(IReadOnlyList<PageRange> ranges, int pageCount)
    {
        ranges.ShouldNotBeEmpty();
        ranges[0].StartPage.ShouldBe(1);
        ranges[^1].EndPage.ShouldBe(pageCount);
        for (var i = 0; i < ranges.Count; i++)
        {
            ranges[i].StartPage.ShouldBeLessThanOrEqualTo(ranges[i].EndPage);
            if (i > 0)
            {
                ranges[i].StartPage.ShouldBe(ranges[i - 1].EndPage + 1);
            }
        }
    }

    [Fact]
    public async Task Finds_the_boundary_between_the_invoice_and_its_delivery_note()
    {
        var layer = await ExtractAsync(TestPdfs.InvoiceWithDeliveryNote());

        var analysis = await _splitter.AnalyzeAsync(Tenant, layer, NoImages, TestContext.Current.CancellationToken);

        analysis.Ranges.ShouldBe([new PageRange(1, 2), new PageRange(3, 3)]);
        ShouldCoverAllPages(analysis.Ranges, 3);
        analysis.IsConfident.ShouldBeTrue();
        analysis.Confidence.ShouldBeGreaterThanOrEqualTo(0.9);
        analysis.Signals.ShouldContain(s => s.Page == 3 && s.Kind == SplitSignalKind.PageNumberingReset);
        analysis.Signals.ShouldContain(s => s.Page == 3 && s.Kind == SplitSignalKind.DocumentHeaderRepeated && s.Evidence == "Lieferschein");
        analysis.Signals.ShouldContain(s => s.Page == 2 && s.Kind == SplitSignalKind.PageNumberingContinued);
        analysis.Signals.ShouldNotContain(s => s.Page == 2 && s.Kind == SplitSignalKind.PageNumberingReset);

        // The invoice references its delivery note ("Lieferschein-Nr. LS-2026-0917" on page 1); that is not the invoice's
        // number, so the delivery note carrying the same value is not a continuation of the invoice.
        analysis.Signals.ShouldNotContain(s => s.Page == 3 && s.Kind == SplitSignalKind.DocumentNumberRepeated);
    }

    [Fact]
    public async Task Two_page_invoice_is_one_range()
    {
        var layer = await ExtractAsync(TestPdfs.GermanInvoice());

        var analysis = await _splitter.AnalyzeAsync(Tenant, layer, NoImages, TestContext.Current.CancellationToken);
        var ranges = await _splitter.SplitAsync(Tenant, layer, NoImages, TestContext.Current.CancellationToken);

        analysis.Ranges.ShouldBe([new PageRange(1, 2)]);
        ranges.ShouldBe(analysis.Ranges);
        analysis.IsConfident.ShouldBeTrue();
        analysis.Signals.ShouldContain(s => s.Page == 2 && s.Kind == SplitSignalKind.DocumentNumberRepeated);
        analysis.Signals.ShouldContain(s => s.Page == 2 && s.Kind == SplitSignalKind.ContinuationMarker);
    }

    [Fact]
    public void Single_page_is_one_confident_range()
    {
        var analysis = _splitter.Analyze(TextLayers.Layer(TextLayers.Page(1, (120, "Rechnung"), (150, "Rechnungsnummer 1"))), 1);

        analysis.Ranges.ShouldBe([new PageRange(1, 1)]);
        analysis.Confidence.ShouldBe(1);
        analysis.IsConfident.ShouldBeTrue();
        analysis.Signals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_text_layer_everything_is_one_range_and_the_llm_must_decide()
    {
        var images = Enumerable.Range(0, 4).Select(_ => new ReadOnlyMemory<byte>([1, 2, 3])).ToList();

        var analysis = await _splitter.AnalyzeAsync(Tenant, null, images, TestContext.Current.CancellationToken);
        var blank = _splitter.Analyze(TextLayers.Layer(TextLayers.BlankPage(1), TextLayers.BlankPage(2)), 2);

        analysis.Ranges.ShouldBe([new PageRange(1, 4)]);
        analysis.Confidence.ShouldBe(0);
        analysis.IsConfident.ShouldBeFalse();
        blank.Ranges.ShouldBe([new PageRange(1, 2)]);
        blank.IsConfident.ShouldBeFalse();
    }

    [Fact]
    public void Blank_page_separates_two_documents_and_stays_with_the_first()
    {
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (120, "Invoice"), (150, "Invoice number INV-1"), (300, "Total 100.00")),
            TextLayers.BlankPage(2),
            TextLayers.Page(3, (300, "Total 200.00"), (320, "Thank you for your business")));

        var analysis = _splitter.Analyze(layer, 3);

        analysis.Ranges.ShouldBe([new PageRange(1, 2), new PageRange(3, 3)]);
        analysis.Signals.ShouldContain(s => s.Page == 2 && s.Kind == SplitSignalKind.BlankSeparatorPage);

        // Page 3 shows nothing but the blank page before it; that alone is as uncertain as a page without any signal.
        analysis.Confidence.ShouldBe(new HeuristicSplitterOptions().NoSignalCertainty);
        analysis.IsConfident.ShouldBeFalse();
    }

    [Fact]
    public void Blank_page_followed_by_a_new_document_header_is_a_confident_split()
    {
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (120, "Invoice"), (150, "Invoice number INV-1"), (300, "Total 100.00")),
            TextLayers.BlankPage(2),
            TextLayers.Page(3, (120, "Invoice"), (150, "Invoice number INV-2"), (300, "Total 200.00")));

        var analysis = _splitter.Analyze(layer, 3);

        analysis.Ranges.ShouldBe([new PageRange(1, 2), new PageRange(3, 3)]);
        analysis.Confidence.ShouldBe(1);
        analysis.IsConfident.ShouldBeTrue();
    }

    [Fact]
    public void Blank_page_inside_a_continuing_invoice_does_not_split_it()
    {
        // A duplex scan of a two-page invoice with an empty back page in the middle.
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (120, "Rechnung"), (150, "Rechnungsnummer RE-1"), (800, "Seite 1/2")),
            TextLayers.BlankPage(2),
            TextLayers.Page(3, (60, "Rechnung RE-1"), (120, "Übertrag 500,00"), (800, "Seite 2/2")));

        var analysis = _splitter.Analyze(layer, 3);

        analysis.Ranges.ShouldBe([new PageRange(1, 3)]);
        analysis.Confidence.ShouldBe(1);
        analysis.IsConfident.ShouldBeTrue();
        analysis.Signals.ShouldContain(s => s.Page == 2 && s.Kind == SplitSignalKind.BlankSeparatorPage);
        analysis.Signals.ShouldContain(s => s.Page == 3 && s.Kind == SplitSignalKind.PageNumberingContinued);
        analysis.Signals.ShouldContain(s => s.Page == 3 && s.Kind == SplitSignalKind.DocumentNumberRepeated);
        analysis.Signals.ShouldContain(s => s.Page == 3 && s.Kind == SplitSignalKind.ContinuationMarker);
    }

    [Fact]
    public void Footer_only_page_inside_a_document_does_not_split_it()
    {
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (120, "Rechnung"), (150, "Rechnungsnummer RE-1"), (800, "Seite 1/3")),
            TextLayers.Page(2, (800, "Seite 2/3")),
            TextLayers.Page(3, (60, "Rechnung RE-1"), (300, "Gesamtbetrag 100,00 EUR"), (800, "Seite 3/3")));

        var analysis = _splitter.Analyze(layer, 3);

        analysis.Ranges.ShouldBe([new PageRange(1, 3)]);
        analysis.IsConfident.ShouldBeTrue();
        analysis.Signals.ShouldContain(s => s.Page == 3 && s.Kind == SplitSignalKind.PageNumberingContinued);
    }

    [Fact]
    public void Two_same_day_one_page_invoices_without_page_numbers_are_two_documents()
    {
        // The usual batch scan: same supplier, same date, consecutive numbers; the date must not count as a repeated number.
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (60, "Musterfirma GmbH"), (120, "Rechnung"), (150, "Rechnungsnummer RE-1"), (166, "Rechnungsdatum 15.03.2026"), (300, "Gesamtbetrag 100,00 EUR")),
            TextLayers.Page(2, (60, "Musterfirma GmbH"), (120, "Rechnung"), (150, "Rechnungsnummer RE-2"), (166, "Rechnungsdatum 15.03.2026"), (300, "Gesamtbetrag 200,00 EUR")));

        var analysis = _splitter.Analyze(layer, 2);

        analysis.Ranges.ShouldBe([new PageRange(1, 1), new PageRange(2, 2)]);
        analysis.Confidence.ShouldBe(1);
        analysis.IsConfident.ShouldBeTrue();
        analysis.Signals.ShouldContain(s => s.Page == 2 && s.Kind == SplitSignalKind.DocumentNumberChanged && s.Evidence == "Rechnungsnummer RE-2");
        analysis.Signals.ShouldNotContain(s => s.Kind == SplitSignalKind.DocumentNumberRepeated);
    }

    [Fact]
    public void A_changed_number_wins_over_a_repeated_one_on_the_same_page()
    {
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (120, "Rechnung"), (150, "Rechnungsnummer RE-1"), (300, "Gesamtbetrag 100,00 EUR")),
            TextLayers.Page(2, (120, "Rechnung"), (150, "Rechnungsnummer RE-2"), (166, "Rechnung RE-1 wird hiermit ersetzt."), (300, "Gesamtbetrag 100,00 EUR")));

        var analysis = _splitter.Analyze(layer, 2);

        analysis.Ranges.ShouldBe([new PageRange(1, 1), new PageRange(2, 2)]);
        analysis.Signals.ShouldContain(s => s.Page == 2 && s.Kind == SplitSignalKind.DocumentNumberChanged);
        analysis.Signals.ShouldNotContain(s => s.Page == 2 && s.Kind == SplitSignalKind.DocumentNumberRepeated);
    }

    [Fact]
    public void A_header_title_of_another_type_starts_a_new_document_even_when_it_carries_a_number()
    {
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (120, "Lieferschein 4711"), (300, "1 Schraube M8 100 Stk")),
            TextLayers.Page(2, (120, "Rechnung RE-1"), (300, "1 Schraube M8 100 Stk 0,50 EUR 50,00 EUR")));

        var analysis = _splitter.Analyze(layer, 2);

        analysis.Ranges.ShouldBe([new PageRange(1, 1), new PageRange(2, 2)]);
        analysis.IsConfident.ShouldBeTrue();
        analysis.Signals.ShouldContain(s => s.Page == 2 && s.Kind == SplitSignalKind.DocumentHeaderRepeated && s.Evidence == "Rechnung");
        analysis.Signals.ShouldNotContain(s => s.Kind == SplitSignalKind.DocumentNumberRepeated || s.Kind == SplitSignalKind.DocumentNumberChanged);
    }

    [Fact]
    public void A_different_document_number_starts_a_new_document_even_without_page_numbers()
    {
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (60, "Acme Ltd"), (120, "Invoice"), (150, "Invoice number INV-1"), (300, "Total 100.00")),
            TextLayers.Page(2, (60, "Acme Ltd"), (120, "Invoice"), (150, "Invoice number INV-2"), (300, "Total 200.00")),
            TextLayers.Page(3, (60, "Acme Ltd"), (150, "Invoice number INV-2"), (300, "Total 200.00")));

        var analysis = _splitter.Analyze(layer, 3);

        analysis.Ranges.ShouldBe([new PageRange(1, 1), new PageRange(2, 3)]);
        analysis.Signals.ShouldContain(s => s.Page == 2 && s.Kind == SplitSignalKind.DocumentNumberChanged);
        analysis.Signals.ShouldContain(s => s.Page == 2 && s.Kind == SplitSignalKind.SupplierHeaderRepeated);
        analysis.Signals.ShouldContain(s => s.Page == 3 && s.Kind == SplitSignalKind.DocumentNumberRepeated);
        analysis.IsConfident.ShouldBeTrue();
    }

    [Fact]
    public void Continuation_page_header_with_the_document_number_does_not_split()
    {
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (120, "Rechnung Nr. 4711"), (150, "Rechnungsdatum 01.03.2026"), (800, "Seite 1 von 2")),
            TextLayers.Page(2, (60, "Rechnung Nr. 4711"), (120, "Übertrag 500,00"), (800, "Seite 2 von 2")));

        var analysis = _splitter.Analyze(layer, 2);

        analysis.Ranges.ShouldBe([new PageRange(1, 2)]);
        analysis.IsConfident.ShouldBeTrue();
        analysis.Signals.ShouldNotContain(s => s.Kind == SplitSignalKind.DocumentHeaderRepeated);
    }

    [Fact]
    public void Pages_without_any_signal_are_treated_as_continuation_but_leave_the_decision_to_the_llm()
    {
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (120, "Rechnung"), (150, "Rechnungsnummer 1")),
            TextLayers.BodyPage(2));

        var analysis = _splitter.Analyze(layer, 2);

        analysis.Ranges.ShouldBe([new PageRange(1, 2)]);
        analysis.Confidence.ShouldBe(new HeuristicSplitterOptions().NoSignalCertainty);
        analysis.IsConfident.ShouldBeFalse();
    }

    [Fact]
    public void Long_files_are_never_confident_so_the_llm_checks_them()
    {
        var pages = Enumerable.Range(1, 8)
            .Select(n => TextLayers.Page(n, (60, "Acme Ltd"), (150, "Invoice number INV-1"), (800, $"Page {n} of 8")))
            .ToArray();

        var analysis = _splitter.Analyze(TextLayers.Layer(pages), 8);

        analysis.Ranges.ShouldBe([new PageRange(1, 8)]);
        analysis.Confidence.ShouldBe(1);
        analysis.IsConfident.ShouldBeFalse();
    }

    [Fact]
    public void Page_numbers_are_recognised_in_several_languages()
    {
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (120, "Lorem ipsum"), (800, "Stranica 1 od 2")),
            TextLayers.Page(2, (120, "Lorem ipsum"), (800, "Stranica 2 od 2")),
            TextLayers.Page(3, (120, "Lorem ipsum"), (800, "Pagina 1 di 1")),
            TextLayers.Page(4, (120, "Lorem ipsum"), (800, "Page 1 sur 3")),
            TextLayers.Page(5, (120, "Lorem ipsum"), (800, "Page 2 sur 3")),
            TextLayers.Page(6, (120, "Lorem ipsum"), (800, "Page 3 sur 3")));

        var analysis = _splitter.Analyze(layer, 6);

        analysis.Ranges.ShouldBe([new PageRange(1, 2), new PageRange(3, 3), new PageRange(4, 6)]);
        ShouldCoverAllPages(analysis.Ranges, 6);
        analysis.IsConfident.ShouldBeTrue();
    }

    [Fact]
    public async Task Analyze_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _splitter.SplitAsync(Tenant, null, NoImages, cts.Token));
    }
}
