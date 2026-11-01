using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The parts of a multi-document attachment (docs/03 "Split"): each part is extracted from its own pages, a reprocess
/// from Normalize keeps its range instead of splitting the file again, and whether the pages have a text layer is
/// decided for every part from its own pages.
/// </summary>
public class SplitPartTests
{
    private const string PdfMime = "application/pdf";
    private const string SplitInTwo = """{ "documents": [ { "startPage": 1, "endPage": 1 }, { "startPage": 2, "endPage": 2 } ] }""";

    private static void SplitByLlm(Dictionary<string, string?> settings)
    {
        // One worker, so the scripted answers are taken in a fixed order; the heuristics may not split a two-page
        // file on their own, so the LLM splitter decides; the keyword classifier decides the type.
        settings["Pipeline:MaxParallelism"] = "1";
        settings["DocumentProcessing:Splitter:MaxConfidentPageCount"] = "1";
        settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0";
        settings["Pipeline:LlmClassificationThreshold"] = "0";
    }

    [Fact]
    public async Task Each_part_of_a_split_attachment_is_extracted_from_its_own_page_not_from_the_whole_pdf()
    {
        await using var host = await PipelineTestHost.StartAsync(SplitByLlm);
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(SplitInTwo);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("stapel.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var documents = await DocumentsAsync(host, ingestion.Id);
        documents.Select(d => (d.PageStart, d.PageEnd, d.PageCount)).ShouldBe([(1, 1, 1), (2, 2, 1)]);
        documents.ShouldAllBe(d => d.FailureReason == null && d.NormalizedBlob == null && d.PageImageKeys.Count == 1);

        var extracts = host.Llm.Requests.Where(r => r.Stage == LlmStage.Extract).ToList();
        extracts.Count.ShouldBe(2);
        foreach (var request in extracts)
        {
            var document = documents.Single(d => d.Id == request.DocumentId);
            var input = request.Document.ShouldNotBeNull();
            input.HasPdf.ShouldBeFalse("the original PDF holds both parts; a part is sent as its own pages");
            input.PageImages.Count.ShouldBe(1);
            input.PageTexts.ShouldHaveSingleItem().ShouldContain($"Seite {document.PageStart}/2");
            request.Prompt.Task.ShouldNotContain("Attached as a PDF");
        }

        host.Llm.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task Reprocessing_a_part_from_normalize_keeps_its_range_and_creates_no_further_documents()
    {
        await using var host = await PipelineTestHost.StartAsync(SplitByLlm);
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue("""{ "documents": [ { "startPage": 1, "endPage": 1 }, { "startPage": 2, "endPage": 3 } ] }""");
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("stapel.pdf", PdfMime, TestPdfs.SinglePageThenGermanInvoice())]);
        await host.RunUntilIdleAsync();
        var before = await DocumentsAsync(host, ingestion.Id);
        before.Select(d => (d.PageStart, d.PageEnd, d.PageCount)).ShouldBe([(1, 1, 1), (2, 3, 2)]);

        // What POST /documents/{id}:reprocess with fromStage=Normalize does, for either part: back to Received, then
        // stage 2 again. The two-page part would go to the LLM splitter again (MaxConfidentPageCount=1), and no split
        // answer is scripted for it.
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        foreach (var document in before)
        {
            await ResetAsync(host, document.Id, DocumentStatus.Received);
            await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, document.Id, PipelineStage.Normalize), CancellationToken.None);
            await host.RunUntilIdleAsync();
        }

        var after = await DocumentsAsync(host, ingestion.Id);
        after.Select(d => d.Id).ShouldBe(before.Select(d => d.Id), "no part is created a second time");
        after.Select(d => (d.PageStart, d.PageEnd, d.PageCount)).ShouldBe([(1, 1, 1), (2, 3, 2)]);
        after.ShouldAllBe(d => d.FailureReason == null && d.Status != DocumentStatus.Received && d.NormalizedBlob == null);
        after.Select(d => d.PageImageKeys.Count).ShouldBe([1, 2]);

        var longer = after[1];
        (await host.EventsAsync(longer.Id)).Last(e => e.ToStatus == DocumentStatus.Split).DetailsJson.ShouldNotBeNull().ShouldContain("already split");
        var extract = host.Llm.Requests.Last(r => r.Stage == LlmStage.Extract);
        extract.DocumentId.ShouldBe(longer.Id);
        var input = extract.Document.ShouldNotBeNull();
        input.HasPdf.ShouldBeFalse();
        input.PageImages.Count.ShouldBe(2);
        input.PageTexts.Count.ShouldBe(2);
        input.PageTexts[0].ShouldContain("Seite 1/2");
        input.PageTexts[1].ShouldContain("Seite 2/2");
        host.Llm.Requests.Count(r => r.Stage == LlmStage.Split).ShouldBe(1, "the reprocess did not split the file again");
        host.Llm.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task Every_part_of_a_split_scan_without_a_text_layer_keeps_the_blocking_unreadable_pages_flag()
    {
        await using var host = await PipelineTestHost.StartAsync(settings =>
        {
            settings["Pipeline:MaxParallelism"] = "1";
            settings["DocumentProcessing:Splitter:MaxConfidentPageCount"] = "1";
        });
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(SplitInTwo);
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("scan.pdf", PdfMime, TestPdfs.ShapesOnlyPages(2))]);
        await host.RunUntilIdleAsync();

        var documents = await DocumentsAsync(host, ingestion.Id);
        documents.Count.ShouldBe(2);
        foreach (var document in documents)
        {
            document.FailureReason.ShouldBeNull();
            document.Flags.ShouldContain(f => f.Code == FlagCodes.UnreadablePages && f.Severity == FlagSeverity.Blocking && f.ResolvedAt == null);
            document.Status.ShouldBe(DocumentStatus.NeedsReview, "nobody but the LLM has read the pages of either part");
        }

        host.Llm.Remaining.ShouldBe(0);
    }

    /// <summary>
    /// The reported regression: an image-only scan with a two-word scanner footer on every page. Stage 2 counts a page
    /// as text only above DocumentProcessing:Pdf:TextLayerMinimumWordsPerPage words and flags the file; a part is
    /// decided by the same rule, so the footer words do not lift the flag from every part, on the first run or after
    /// a reprocess from Normalize. Only the LLM has read these pages.
    /// </summary>
    [Fact]
    public async Task Every_part_of_a_split_scan_with_only_a_short_footer_per_page_keeps_the_unreadable_pages_flag_on_the_first_run_and_after_a_reprocess()
    {
        await using var host = await PipelineTestHost.StartAsync(settings =>
        {
            settings["Pipeline:MaxParallelism"] = "1";
            settings["DocumentProcessing:Splitter:MaxConfidentPageCount"] = "1";
        });
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(SplitInTwo);
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("scan.pdf", PdfMime, TestPdfs.ShapesOnlyPagesWithScannerFooter(2))]);
        await host.RunUntilIdleAsync();

        var documents = await DocumentsAsync(host, ingestion.Id);
        AssertEveryPartFlaggedUnreadable(documents);
        host.Llm.Remaining.ShouldBe(0);

        foreach (var document in documents)
        {
            host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
            host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
            await ResetAsync(host, document.Id, DocumentStatus.Received);
            await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, document.Id, PipelineStage.Normalize), CancellationToken.None);
            await host.RunUntilIdleAsync();
        }

        AssertEveryPartFlaggedUnreadable(await DocumentsAsync(host, ingestion.Id));
        host.Llm.Remaining.ShouldBe(0);
    }

    private static void AssertEveryPartFlaggedUnreadable(List<Document> documents)
    {
        documents.Count.ShouldBe(2);
        foreach (var document in documents)
        {
            document.FailureReason.ShouldBeNull();
            document.PageCount.ShouldBe(1);
            document.Flags.ShouldContain(f => f.Code == FlagCodes.UnreadablePages && f.Severity == FlagSeverity.Blocking && f.ResolvedAt == null, $"part {document.PageStart}-{document.PageEnd}");
            document.Status.ShouldBe(DocumentStatus.NeedsReview, "nobody but the LLM has read the pages of either part");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_a_mixed_scan_only_the_part_without_text_carries_the_unreadable_pages_flag_on_the_first_run_and_after_a_reprocess(bool imageOnlyLast)
    {
        // Stage 2 sees "some text in the file" and sets no flag; the image-only part must still get it, on the first
        // run (stage 3 decides from the part's own text) just as on a reprocess from Normalize.
        await using var host = await PipelineTestHost.StartAsync(SplitByLlm);
        await host.AddReplayProviderAsync();
        var (pdf, split) = imageOnlyLast
            ? (TestPdfs.GermanInvoiceThenShapesOnlyPage(), """{ "documents": [ { "startPage": 1, "endPage": 2 }, { "startPage": 3, "endPage": 3 } ] }""")
            : (TestPdfs.ShapesOnlyPageThenGermanInvoice(), """{ "documents": [ { "startPage": 1, "endPage": 1 }, { "startPage": 2, "endPage": 3 } ] }""");
        host.Llm.Enqueue(split);
        EnqueuePartAnswers(host, text: true, image: true);

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("stapel.pdf", PdfMime, pdf)]);
        await host.RunUntilIdleAsync();

        var documents = await DocumentsAsync(host, ingestion.Id);
        AssertOnlyImagePartFlagged(documents, imageOnlyLast);
        host.Llm.Remaining.ShouldBe(0);

        // A reprocess from Normalize decides from the part's own text layer too, and comes to the same answer.
        foreach (var document in documents)
        {
            EnqueuePartAnswers(host, text: document.PageCount == 2, image: document.PageCount == 1);
            await ResetAsync(host, document.Id, DocumentStatus.Received);
            await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, document.Id, PipelineStage.Normalize), CancellationToken.None);
            await host.RunUntilIdleAsync();
        }

        AssertOnlyImagePartFlagged(await DocumentsAsync(host, ingestion.Id), imageOnlyLast);
        host.Llm.Remaining.ShouldBe(0);
    }

    /// <summary>
    /// The keyword rules classify the invoice part; the image-only part has no words, so the LLM classifies it. The
    /// queue interleaves the two parts stage by stage, and both extract answers are the same, so the order holds.
    /// </summary>
    private static void EnqueuePartAnswers(PipelineTestHost host, bool text, bool image)
    {
        if (image)
        {
            host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
            host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        }

        if (text)
        {
            host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        }
    }

    private static void AssertOnlyImagePartFlagged(List<Document> documents, bool imageOnlyLast)
    {
        documents.Count.ShouldBe(2);
        var image = imageOnlyLast ? documents[1] : documents[0];
        var text = imageOnlyLast ? documents[0] : documents[1];
        image.PageCount.ShouldBe(1);
        text.PageCount.ShouldBe(2);
        documents.Select(d => d.FailureReason).ShouldAllBe(r => r == null);
        image.Flags.ShouldContain(f => f.Code == FlagCodes.UnreadablePages && f.Severity == FlagSeverity.Blocking && f.ResolvedAt == null);
        image.Status.ShouldBe(DocumentStatus.NeedsReview, "nobody but the LLM has read the image-only part");
        text.Flags.ShouldNotContain(f => f.Code == FlagCodes.UnreadablePages && f.ResolvedAt == null);
    }

    private static Task<List<Document>> DocumentsAsync(PipelineTestHost host, Guid ingestionId) =>
        host.WithDbAsync(async db => (await db.Documents.AsNoTracking()
                .Include(d => d.Flags)
                .Where(d => d.IngestionItem!.IngestionId == ingestionId)
                .ToListAsync())
            .OrderBy(d => d.PageStart)
            .ToList());

    private static Task ResetAsync(PipelineTestHost host, Guid documentId, DocumentStatus status) =>
        host.WithDbAsync(async db =>
        {
            var row = await db.Documents.SingleAsync(d => d.Id == documentId);
            row.Status = status;
            row.FailureReason = null;
            await db.SaveChangesAsync();
        });
}
