using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Pdf;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Tests.Demo;

/// <summary>
/// The committed demo PDFs read by the real text-layer extraction and keyword rules (docs/09 "Demo data", "Demo: types
/// as data"): which of them the keywords classify, and which needs the (replayed) LLM classifier.
/// </summary>
public sealed class DemoPdfClassificationTests
{
    private static async Task<ClassificationOutcome> ClassifyAsync(string path)
    {
        var processor = new PdfPigPdfProcessor(Options.Create(new PdfProcessingOptions()));
        await using var stream = File.OpenRead(path);
        var layer = await processor.ExtractTextAsync(stream, TestContext.Current.CancellationToken);
        return new KeywordDocumentClassifier(Options.Create(new KeywordClassifierOptions())).Classify(layer);
    }

    private static string TenantA(string file) => Path.Combine(DemoFixtureTests.RepositoryRoot(), "fixtures", "demo", "tenant-a", file);

    [Fact]
    public async Task The_uploaded_credit_note_is_a_credit_note_by_its_keywords_alone()
    {
        // Its letterhead names the billing mailbox rechnung@getraenke-hoffmann.de, which is no "Rechnung" title.
        var outcome = await ClassifyAsync(DemoFixtureTests.CreditNotePath());

        outcome.Type.ShouldBe(DocumentTypeKey.CreditNote, $"confidence {outcome.Confidence}");
        outcome.Source.ShouldBe(TypeSource.Rule);
        outcome.Confidence.ShouldBeGreaterThanOrEqualTo(0.8, "above the LLM classification threshold, so the demo upload costs no classification call");
        outcome.Language.ShouldBe(DocumentLanguages.German);
    }

    [Fact]
    public async Task The_invoice_is_classified_by_keywords_and_the_delivery_slip_is_left_to_the_llm()
    {
        (await ClassifyAsync(TenantA(DemoPdfs.InvoiceFileName))).Type.ShouldBe(DocumentTypeKey.Invoice);
        (await ClassifyAsync(TenantA(DemoPdfs.DeliveryNoteFileName))).Type.ShouldBe(DocumentTypeKey.Unknown);
    }

    [Fact]
    public async Task The_price_list_is_left_to_the_llm_by_the_keywords_and_too_long_for_the_heuristic_splitter()
    {
        // docs/09 "Demo: manual mapping": no keyword rule knows a price list (the tenant keyword set comes in P5).
        var path = DemoFixtureTests.PriceListPath();
        (await ClassifyAsync(path)).Type.ShouldBe(DocumentTypeKey.Unknown);

        var processor = new PdfPigPdfProcessor(Options.Create(new PdfProcessingOptions()));
        await using var stream = File.OpenRead(path);
        var layer = await processor.ExtractTextAsync(stream, TestContext.Current.CancellationToken);
        layer.Pages.Count.ShouldBe(DemoPriceList.PageCount);
        layer.Pages.Count.ShouldBeGreaterThan(new DocProc.Infrastructure.Classification.HeuristicSplitterOptions().MaxConfidentPageCount, "so the split stage asks the (replayed) LLM splitter");
    }
}
