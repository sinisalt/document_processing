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
        analysis.IsConfident.ShouldBeTrue();
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
