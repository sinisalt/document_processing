using DocProc.Application.Abstractions;
using DocProc.Llm.Extraction;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests.Extraction;

public class LlmDocumentSplitterTests
{
    private static readonly PromptCatalogue Prompts = PromptCatalogue.Load();

    private static LlmDocumentSplitter Splitter(FakeGateway gateway, LlmExtractionOptions? options = null) =>
        new(gateway, Prompts, options ?? new LlmExtractionOptions());

    private static Domain.Text.TextLayer FourPages() => new TextLayerBuilder()
        .Page(1).Line("Rechnung", "Nr.", "RE-1001", "Seite", "1", "von", "2")
        .Page(2).Line("Seite", "2", "von", "2", "Übertrag")
        .Page(3).Line("Lieferschein", "LS-77")
        .Page(4).Line("Seite", "2")
        .Build();

    [Fact]
    public async Task Sends_thumbnails_and_the_first_lines_of_each_page_and_maps_the_ranges()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Split);
        var pngs = Enumerable.Repeat<ReadOnlyMemory<byte>>(Inputs.PngBytes, 4).ToList();

        var ranges = await Splitter(gateway, new LlmExtractionOptions { SplitTextCharsPerPage = 17 }).SplitAsync(Inputs.TenantId, FourPages(), pngs, CancellationToken.None);

        ranges.ShouldBe([new PageRange(1, 2), new PageRange(3, 4)]);
        var request = gateway.Requests.ShouldHaveSingleItem();
        request.Stage.ShouldBe(LlmStage.Split);
        request.OutputSchema.ShouldBe(CanonicalSchemas.Split);
        request.Prompt.CacheKey.ShouldBe(Prompts.SplitDocument.CacheKey);
        request.Document.ShouldNotBeNull().PageImages.Count.ShouldBe(4);
        request.Prompt.Task.ShouldContain("Split this 4-page attachment");
        request.Prompt.Task.ShouldContain("4 page thumbnails are attached");
        request.Prompt.Task.ShouldContain("<page number=\"1\">\nRechnung Nr. RE-1\n</page>");
        request.Prompt.Task.ShouldContain("<page number=\"3\">\nLieferschein LS-7\n</page>");
    }

    [Fact]
    public async Task Single_page_attachments_are_not_sent_to_the_model()
    {
        var gateway = new FakeGateway();

        var ranges = await Splitter(gateway).SplitAsync(Inputs.TenantId, null, [Inputs.PngBytes], CancellationToken.None);

        ranges.ShouldBe([new PageRange(1, 1)]);
        gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Works_from_text_alone_and_repairs_a_sloppy_answer()
    {
        var gateway = new FakeGateway().Answer("""{ "documents": [ { "startPage": 3, "endPage": 9, "documentType": null, "reason": null }, { "startPage": 1, "endPage": 1, "documentType": "Invoice", "reason": null } ] }""");

        var ranges = await Splitter(gateway).SplitAsync(Inputs.TenantId, FourPages(), [], CancellationToken.None);

        ranges.ShouldBe([new PageRange(1, 2), new PageRange(3, 4)]);
        gateway.Requests.Single().Document.ShouldBeNull();
        gateway.Requests.Single().Prompt.Task.ShouldContain("No page images are available");
    }

    [Fact]
    public void Normaliser_fills_gaps_cuts_overlaps_and_falls_back_to_one_document()
    {
        SplitRangeNormalizer.Normalize([new PageRange(1, 2), new PageRange(4, 5)], 6).ShouldBe([new PageRange(1, 3), new PageRange(4, 6)]);
        SplitRangeNormalizer.Normalize([new PageRange(1, 3), new PageRange(2, 4)], 4).ShouldBe([new PageRange(1, 3), new PageRange(4, 4)]);
        SplitRangeNormalizer.Normalize([new PageRange(2, 3)], 3).ShouldBe([new PageRange(1, 3)]);
        SplitRangeNormalizer.Normalize([new PageRange(1, 4), new PageRange(2, 3)], 4).ShouldBe([new PageRange(1, 4)]);
        SplitRangeNormalizer.Normalize([new PageRange(5, 2)], 3).ShouldBe([new PageRange(1, 3)]);
        SplitRangeNormalizer.Normalize([], 3).ShouldBe([new PageRange(1, 3)]);
        SplitRangeNormalizer.Normalize([new PageRange(1, 1)], 0).ShouldBeEmpty();
    }
}
