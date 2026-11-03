using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Pdf;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Pipeline.Review;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The page operations of review (docs/11 §12.7) through the stages: a part a reviewer typed goes straight to Extract
/// with <c>TypeSource.Human</c> and is extracted from its own pages, an untyped part is classified, a joined document
/// is classified and extracted from all its pages, and the Match stage keeps a supplier a reviewer assigned.
/// </summary>
public class ReviewPartitioningTests
{
    private const string PdfMime = "application/pdf";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string OneDocument = """{ "documents": [ { "startPage": 1, "endPage": 3 } ] }""";

    private static void Settings(Dictionary<string, string?> settings)
    {
        // One worker, so scripted answers go in a fixed order; the LLM splitter decides the pipeline's own split; the
        // keyword classifier decides the type.
        settings["Pipeline:MaxParallelism"] = "1";
        settings["DocumentProcessing:Splitter:MaxConfidentPageCount"] = "1";
        settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0";
        settings["Pipeline:LlmClassificationThreshold"] = "0";
    }

    /// <summary>A three-page scan the pipeline kept as one document, waiting for review (the supplier is always reviewed).</summary>
    private static async Task<(Guid DocumentId, Guid SupplierId)> OneThreePageDocumentInReviewAsync(PipelineTestHost host)
    {
        await host.AddReplayProviderAsync();
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Musterfirma GmbH", alwaysReview: true);
        host.Llm.Enqueue(OneDocument);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("stapel.pdf", PdfMime, TestPdfs.SinglePageThenGermanInvoice())]);
        await host.RunUntilIdleAsync();
        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        document.PageCount.ShouldBe(3);
        document.SupplierId.ShouldBe(supplier.Id);
        return (document.Id, supplier.Id);
    }

    [Fact]
    public async Task A_typed_part_of_a_review_split_is_extracted_from_its_own_pages_without_classification_and_a_join_reads_all_pages_again()
    {
        await using var host = await PipelineTestHost.StartAsync(Settings);
        var (id, supplierId) = await OneThreePageDocumentInReviewAsync(host);
        var requestsBefore = host.Llm.Requests.Count;

        // The reviewer: page 1 is a document of its own (classify it), pages 2 and 3 are an invoice.
        IReadOnlyList<ReviewPartOutcome> outcomes;
        using (var scope = host.CreateTenantScope(PipelineTestHost.TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<DocProcDbContext>();
            var config = await scope.ServiceProvider.GetRequiredService<IEffectiveConfigResolver>().ResolveAsync(new EffectiveConfigRequest(PipelineTestHost.TenantA, DocumentTypeKey.Invoice), Ct);
            var document = await db.Documents.Include(d => d.Flags).SingleAsync(d => d.Id == id, Ct);
            outcomes = await ReviewPartitioning.SplitAsync(
                db,
                scope.ServiceProvider.GetRequiredService<IContainerBlobStore>(),
                scope.ServiceProvider.GetRequiredService<IOptions<PdfProcessingOptions>>().Value,
                document,
                [new ReviewSplitPart([1], null, null), new ReviewSplitPart([2, 3], DocumentTypeKey.Invoice, config.Type?.VersionId)],
                "anna",
                host.Clock.GetUtcNow(),
                Ct);
            await db.SaveChangesAsync(Ct);
        }

        outcomes.Select(o => (o.Status, o.Stage)).ShouldBe([(DocumentStatus.Split, PipelineStage.Classify), (DocumentStatus.Matched, PipelineStage.Extract)]);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        foreach (var outcome in outcomes)
        {
            await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, outcome.DocumentId, outcome.Stage), CancellationToken.None);
        }

        await host.RunUntilIdleAsync();

        var first = await host.DocumentAsync(id);
        var second = await host.DocumentAsync(outcomes[1].DocumentId);
        first.Status.ShouldBe(DocumentStatus.NeedsReview);
        first.Type.ShouldBe(DocumentTypeKey.Invoice, "page 1 was classified by its keywords");
        first.TypeSource.ShouldBe(TypeSource.Rule);
        second.Status.ShouldBe(DocumentStatus.NeedsReview);
        second.Type.ShouldBe(DocumentTypeKey.Invoice);
        second.TypeSource.ShouldBe(TypeSource.Human, "the reviewer's type is kept");
        second.SupplierId.ShouldBe(supplierId, "the part came with the attachment's supplier");
        second.DocumentTypeVersionId.ShouldNotBeNull();
        second.CanonicalJson.ShouldNotBeNullOrWhiteSpace();
        (second.PageStart, second.PageEnd, second.PageCount).ShouldBe((2, 3, 2));
        var secondEvents = await host.EventsAsync(second.Id);
        secondEvents.Count(e => e.ToStatus == DocumentStatus.Classified).ShouldBe(1);
        secondEvents.Single(e => e.ToStatus == DocumentStatus.Classified).Actor.ShouldBe("anna", "no classification ran for the typed part");
        secondEvents.ShouldContain(e => e.ToStatus == DocumentStatus.Extracted && e.Actor == "pipeline");

        var extracts = host.Llm.Requests.Skip(requestsBefore).Where(r => r.Stage == LlmStage.Extract).ToList();
        extracts.Count.ShouldBe(2);
        host.Llm.Requests.Skip(requestsBefore).ShouldNotContain(r => r.Stage == LlmStage.Classify || r.Stage == LlmStage.Split);
        var ofSecond = extracts.Single(r => r.DocumentId == second.Id).Document.ShouldNotBeNull();
        ofSecond.HasPdf.ShouldBeFalse("a part is read from its own pages");
        ofSecond.PageImages.Count.ShouldBe(2);
        ofSecond.PageTexts[0].ShouldContain("Seite 1/2");
        ofSecond.PageTexts[1].ShouldContain("Seite 2/2");
        extracts.Single(r => r.DocumentId == id).Document!.PageImages.Count.ShouldBe(1);

        // The reviewer joins them back: the document is classified again and read from all three pages.
        using (var scope = host.CreateTenantScope(PipelineTestHost.TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<DocProcDbContext>();
            var target = await db.Documents.Include(d => d.Flags).SingleAsync(d => d.Id == id, Ct);
            var other = await db.Documents.Include(d => d.Flags).SingleAsync(d => d.Id == second.Id, Ct);
            var joined = await ReviewPartitioning.JoinAsync(
                db,
                scope.ServiceProvider.GetRequiredService<IContainerBlobStore>(),
                scope.ServiceProvider.GetRequiredService<IOptions<PdfProcessingOptions>>().Value,
                target,
                [other],
                "anna",
                host.Clock.GetUtcNow(),
                Ct);
            await db.SaveChangesAsync(Ct);
            joined.Stage.ShouldBe(PipelineStage.Classify);
        }

        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, id, PipelineStage.Classify), CancellationToken.None);
        var requestsBeforeJoin = host.Llm.Requests.Count;
        await host.RunUntilIdleAsync();

        var whole = await host.DocumentAsync(id);
        whole.Status.ShouldBe(DocumentStatus.NeedsReview);
        (whole.PageStart, whole.PageEnd, whole.PageCount).ShouldBe((1, 3, 3));
        whole.PageImageKeys.Count.ShouldBe(3);
        var cancelled = await host.DocumentAsync(second.Id);
        cancelled.Status.ShouldBe(DocumentStatus.Cancelled);
        cancelled.JoinedIntoDocumentId.ShouldBe(id);
        var read = host.Llm.Requests.Skip(requestsBeforeJoin).Single(r => r.Stage == LlmStage.Extract).Document.ShouldNotBeNull();
        read.HasPdf.ShouldBeTrue("joined back into the whole attachment, the document is the whole file again");
        whole.NormalizedBlob.ShouldNotBeNull();
        read.PageImages.ShouldBeEmpty("a whole file goes to the model as the PDF, as on the first run");
        read.PageTexts.Count.ShouldBe(3);
        read.PageTexts[2].ShouldContain("Seite 2/2");
        whole.Flags.Where(f => f.ResolvedAt == null).ShouldNotContain(f => f.Code == FlagCodes.DuplicateSuspected, "a cancelled part is never a duplicate of the joined document");
        host.Llm.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task The_match_stage_keeps_a_supplier_a_reviewer_assigned_and_records_how_it_matched_otherwise()
    {
        await using var host = await PipelineTestHost.StartAsync(Settings);
        var (id, matched) = await OneThreePageDocumentInReviewAsync(host);
        (await host.DocumentAsync(id)).SupplierMatchMethod.ShouldBe(SupplierMatchMethod.Name, "the matcher found Musterfirma by its name");

        // The reviewer assigns another supplier, then the document is reprocessed from Classify.
        var assigned = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Nordwind Großhandel GmbH", alwaysReview: true);
        await host.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == id);
            document.SupplierId = assigned.Id;
            document.SupplierMatchMethod = SupplierMatchMethod.Human;
            document.SupplierMatchConfidence = 1.0;
            document.Status = DocumentStatus.Split;
            await db.SaveChangesAsync();
        });
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, id, PipelineStage.Classify), CancellationToken.None);
        await host.RunUntilIdleAsync();

        var after = await host.DocumentAsync(id);
        after.Status.ShouldBe(DocumentStatus.NeedsReview);
        after.SupplierId.ShouldBe(assigned.Id, "Match did not replace the reviewer's supplier with the one its signals find");
        after.SupplierId.ShouldNotBe(matched);
        after.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.Human);
        after.Flags.ShouldNotContain(f => f.Code == FlagCodes.SupplierUnmatched && f.ResolvedAt == null);
        (await host.EventsAsync(id)).Last(e => e.ToStatus == DocumentStatus.Matched).DetailsJson.ShouldNotBeNull().ShouldContain("\"kept\":true");

        // A supplier that was deleted meanwhile is matched again.
        await host.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == id);
            document.SupplierId = null;
            document.Status = DocumentStatus.Split;
            await db.SaveChangesAsync();
            db.Suppliers.Remove(await db.Suppliers.SingleAsync(s => s.Id == assigned.Id));
            await db.SaveChangesAsync();
        });
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, id, PipelineStage.Classify), CancellationToken.None);
        await host.RunUntilIdleAsync();
        var rematched = await host.DocumentAsync(id);
        rematched.SupplierId.ShouldBe(matched);
        rematched.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.Name);
    }
}
