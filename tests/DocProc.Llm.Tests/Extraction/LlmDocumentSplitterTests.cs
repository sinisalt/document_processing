using DocProc.Application.Abstractions;
using DocProc.Llm.Extraction;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;
using SkiaSharp;

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


    /// <summary>
    /// A stage 2 render of a scanned page: A4 at 200 dpi, rows of "words" (black bars) over a lightly speckled
    /// background, so the full-size PNG is as large as a real scan and not a trivially compressible blank.
    /// </summary>
    private static byte[] RenderedPage(int width = 1654, int height = 2339, int seed = 1)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var random = new Random(seed);
        using var speckle = new SKPaint { Color = new SKColor(200, 200, 200) };
        for (var i = 0; i < width * height / 40; i++)
        {
            canvas.DrawPoint(random.Next(width), random.Next(height), speckle);
        }

        using var ink = new SKPaint { Color = SKColors.Black };
        for (var y = 120; y < height - 120; y += 34)
        {
            for (var x = 120; x < width - 200; x += random.Next(40, 160))
            {
                canvas.DrawRect(x, y, random.Next(30, 120), 18, ink);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task Full_size_renders_are_downscaled_to_thumbnails_so_the_request_stays_small()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Split);
        var renders = Enumerable.Range(1, 4).Select(i => (ReadOnlyMemory<byte>)RenderedPage(seed: i)).ToList();
        var fullSize = renders.Sum(r => r.Length);
        fullSize.ShouldBeGreaterThan(1_000_000, "the renders must be as large as real scans for the downscaling to matter");

        await Splitter(gateway).SplitAsync(Inputs.TenantId, FourPages(), renders, CancellationToken.None);

        var images = gateway.Requests.Single().Document.ShouldNotBeNull().PageImages;
        images.Count.ShouldBe(4);
        foreach (var image in images)
        {
            image.Height.ShouldBe(768);
            image.Width.ShouldBe(543);
            PngDimensions.TryRead(image.Png.Span, out _, out _).ShouldBeTrue("thumbnails stay PNG so the adapters' media type is unchanged");
        }

        var sizes = string.Join(", ", images.Select(i => i.Png.Length));
        var thumbnailSize = images.Sum(i => i.Png.Length);
        thumbnailSize.ShouldBeLessThan(fullSize / 4, $"thumbnails {sizes} bytes against {fullSize} bytes of renders");
        thumbnailSize.ShouldBeLessThan(400_000, $"four A4 thumbnails are a few hundred kilobytes at most, got {sizes}");
        gateway.Requests.Single().Prompt.Task.ShouldContain("4 page thumbnails are attached in page order.");
    }

    [Fact]
    public void Downscaling_keeps_small_or_undecodable_images_as_they_are()
    {
        var small = RenderedPage(400, 300);
        PageThumbnails.Downscale(small, 768).ToArray().ShouldBe(small);
        PageThumbnails.Downscale(Inputs.PngBytes, 100).ToArray().ShouldBe(Inputs.PngBytes, "a header without image data cannot be decoded and is passed through");
        PageThumbnails.Downscale(RenderedPage(1000, 500), 200).ShouldSatisfyAllConditions(
            t => PngDimensions.TryRead(t.Span, out var w, out _).ShouldBeTrue(),
            t => { PngDimensions.TryRead(t.Span, out var w, out var h); (w, h).ShouldBe((200, 100)); });
        Should.Throw<ArgumentOutOfRangeException>(() => PageThumbnails.Downscale(small, 0));
    }

    [Fact]
    public async Task Pages_beyond_the_thumbnail_cap_are_decided_from_text_and_the_model_is_told()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Split);
        var pngs = Enumerable.Repeat<ReadOnlyMemory<byte>>(Inputs.PngBytes, 45).ToList();
        var layer = new TextLayerBuilder();
        for (var page = 1; page <= 45; page++)
        {
            layer.Page(page).Line("Seite", page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        await Splitter(gateway).SplitAsync(Inputs.TenantId, layer.Build(), pngs, CancellationToken.None);

        var request = gateway.Requests.Single();
        request.Document.ShouldNotBeNull().PageImages.Count.ShouldBe(40);
        request.Document.PageImages[^1].Page.ShouldBe(40);
        request.Prompt.Task.ShouldContain("Split this 45-page attachment");
        request.Prompt.Task.ShouldContain("Only the first 40 of 45 page thumbnails are attached, in page order; decide the remaining pages from their text alone.");
        request.Prompt.Task.ShouldContain("<page number=\"45\">\nSeite 45\n</page>");

        var none = new FakeGateway().Answer(SampleAnswers.Split);
        await Splitter(none, new LlmExtractionOptions { SplitMaxThumbnailPages = 0 }).SplitAsync(Inputs.TenantId, FourPages(), pngs.Take(4).ToList(), CancellationToken.None);
        none.Requests.Single().Document.ShouldBeNull();
        none.Requests.Single().Prompt.Task.ShouldContain("No page images are available");
    }

    [Fact]
    public async Task The_document_id_rides_on_the_request_when_the_caller_knows_it()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Split).Answer(SampleAnswers.Split);
        IDocumentSplitter splitter = Splitter(gateway);
        var pngs = Enumerable.Repeat<ReadOnlyMemory<byte>>(Inputs.PngBytes, 4).ToList();

        await splitter.SplitAsync(Inputs.TenantId, Inputs.DocumentId, FourPages(), pngs, CancellationToken.None);
        await splitter.SplitAsync(Inputs.TenantId, FourPages(), pngs, CancellationToken.None);

        gateway.Requests[0].DocumentId.ShouldBe(Inputs.DocumentId);
        gateway.Requests[1].DocumentId.ShouldBeNull();
    }
}
