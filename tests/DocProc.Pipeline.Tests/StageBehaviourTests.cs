using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Llm;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Tests;

/// <summary>Stage rules that changed after review: flag ownership, deterministic e-invoice failures, duplicate scope and unreadable pages.</summary>
public class StageBehaviourTests
{
    private const string PdfMime = "application/pdf";
    private const string XmlMime = "application/xml";

    [Fact]
    public async Task A_document_that_failed_on_the_provider_loses_the_flag_when_the_stage_later_succeeds()
    {
        // No active provider at first: the classifier is forced below threshold, the LLM call has nowhere to go.
        await using var host = await PipelineTestHost.StartAsync(settings => settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0.99");
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var failed = await host.SingleDocumentAsync(ingestion.Id);
        failed.Status.ShouldBe(DocumentStatus.Failed);
        failed.Flags.ShouldContain(f => f.Code == FlagCodes.LlmProviderUnavailable && f.ResolvedAt == null);

        // The operator fixes the configuration and sends the document back through classification, as the API's reprocess does.
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        await host.WithDbAsync(async db =>
        {
            var row = await db.Documents.SingleAsync(d => d.Id == failed.Id);
            row.Status = DocumentStateMachine.Reprocess(row.Status, DocumentStatus.Split);
            row.FailureReason = null;
            await db.SaveChangesAsync();
        });
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, failed.Id, PipelineStage.Classify), CancellationToken.None);
        await host.RunUntilIdleAsync();

        var rerun = await host.DocumentAsync(failed.Id);
        rerun.Status.ShouldNotBe(DocumentStatus.Failed);
        rerun.FailureReason.ShouldBeNull();
        rerun.Type.ShouldBe(DocumentType.Invoice);
        rerun.Flags.ShouldNotContain(f => f.Code == FlagCodes.LlmProviderUnavailable, "the stage that failed owns the flag and replaced it on success");
        host.Llm.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task An_e_invoice_that_detects_but_does_not_parse_fails_classification_without_a_retry()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var truncated = System.Text.Encoding.UTF8.GetBytes(
            "<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\" xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\">" +
            "<cbc:CustomizationID>urn:x</cbc:Customiz");
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("truncated.xml", XmlMime, truncated)]);

        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.Failed);
        document.FailureReason.ShouldNotBeNull().ShouldStartWith("The stored e-invoice XML could not be parsed: ");
        document.FailureReason.ShouldContain("malformed");

        var events = await host.EventsAsync(document.Id);
        var failed = events.Where(e => e.EventType == DocumentEventTypes.StageFailed).ShouldHaveSingleItem("deterministic: one attempt, no retry");
        failed.FromStatus.ShouldBe(DocumentStatus.Split, "stage 2 detected the flavour, stage 4 was the one that parsed");
        failed.ToStatus.ShouldBe(DocumentStatus.Failed);
        failed.DetailsJson.ShouldNotBeNull().ShouldContain("\"retry\":false");
    }

    [Fact]
    public async Task Unmatched_documents_with_the_same_number_are_not_duplicates_of_each_other()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var first = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("first.xml", XmlMime, PipelineTestHost.FixtureVariant("first"))]);
        await host.RunUntilIdleAsync();
        var second = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("second.xml", XmlMime, PipelineTestHost.FixtureVariant("second"))]);
        await host.RunUntilIdleAsync();

        foreach (var ingestion in new[] { first, second })
        {
            var document = await host.SingleDocumentAsync(ingestion.Id);
            document.FailureReason.ShouldBeNull();
            document.SupplierId.ShouldBeNull("no supplier of the tenant matches the fixture's sender");
            document.DocumentNumberNormalized.ShouldBe("RE-2026-10412");
            document.Flags.ShouldContain(f => f.Code == FlagCodes.SupplierUnmatched);
            document.Flags.ShouldNotContain(f => f.Code == FlagCodes.DuplicateSuspected, "two unmatched documents share no supplier");
            document.Status.ShouldBe(DocumentStatus.AutoApproved);
        }

        // Once the supplier exists, the next re-send of the same number is a suspected duplicate of the matched ones.
        await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: "DE812345678");
        var matchedFirst = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("third.xml", XmlMime, PipelineTestHost.FixtureVariant("third"))]);
        await host.RunUntilIdleAsync();
        var matchedSecond = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("fourth.xml", XmlMime, PipelineTestHost.FixtureVariant("fourth"))]);
        await host.RunUntilIdleAsync();

        (await host.SingleDocumentAsync(matchedFirst.Id)).Flags.ShouldNotContain(f => f.Code == FlagCodes.DuplicateSuspected, "the unmatched ones do not count against the first matched document");
        var duplicate = await host.SingleDocumentAsync(matchedSecond.Id);
        duplicate.SupplierId.ShouldNotBeNull();
        duplicate.Flags.ShouldContain(f => f.Code == FlagCodes.DuplicateSuspected && f.Severity == FlagSeverity.Blocking);
        duplicate.Status.ShouldBe(DocumentStatus.NeedsReview);
    }

    [Fact]
    public async Task An_image_only_pdf_carries_a_blocking_unreadable_pages_flag_and_goes_to_review()
    {
        await using var host = await PipelineTestHost.StartAsync();
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("scan.pdf", PdfMime, TestPdfs.ShapesOnlyPage())]);

        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.FailureReason.ShouldBeNull();
        document.PageCount.ShouldBe(1);
        document.Type.ShouldBe(DocumentType.Invoice);
        document.TypeSource.ShouldBe(TypeSource.Llm, "no text, so only the image classifier could decide");
        document.ExtractionMethod.ShouldBe(ExtractionMethod.Llm);
        var flag = document.Flags.Where(f => f.Code == FlagCodes.UnreadablePages && f.ResolvedAt == null).ShouldHaveSingleItem();
        flag.Severity.ShouldBe(FlagSeverity.Blocking);
        document.Status.ShouldBe(DocumentStatus.NeedsReview, "nobody but the LLM has read these pages until OCR lands");

        var task = await host.WithDbAsync(db => db.ReviewTasks.AsNoTracking().SingleAsync(t => t.DocumentId == document.Id));
        task.Reason.ShouldContain(FlagCodes.UnreadablePages);
        (await host.EventsAsync(document.Id)).ShouldContain(e => e.EventType == DocumentEventTypes.StageCompleted && e.ToStatus == DocumentStatus.Normalized && e.DetailsJson!.Contains("\"hasTextLayer\":false"));
        host.Llm.Remaining.ShouldBe(0);
    }

    /// <summary>ZUGFeRD 1.0: a known attachment name and a readable root, but no flavour the e-invoice parser supports.</summary>
    private const string Zugferd1 =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rsm:CrossIndustryDocument xmlns:rsm=\"urn:ferd:CrossIndustryDocument:invoice:1p0\">" +
        "<rsm:HeaderExchangedDocument><ID>RE-2026-0042</ID></rsm:HeaderExchangedDocument></rsm:CrossIndustryDocument>";

    /// <summary>A UBL invoice cut off mid-element: detected as UBL, but it does not parse.</summary>
    private const string TruncatedUbl =
        "<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\" xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\">" +
        "<cbc:CustomizationID>urn:x</cbc:Customiz";

    [Theory]
    [InlineData("ZUGFeRD-invoice.xml", Zugferd1, false)]
    [InlineData("factur-x.xml", TruncatedUbl, true)]
    public async Task A_pdf_whose_embedded_e_invoice_cannot_be_read_is_classified_and_extracted_from_its_pages(string fileName, string xml, bool detectable)
    {
        await using var host = await PipelineTestHost.StartAsync(settings =>
        {
            settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0.99";
            settings["DocumentProcessing:Splitter:ConfidenceThreshold"] = "0";
        });
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        var pdf = TestPdfs.GermanInvoiceWithAttachment(fileName, System.Text.Encoding.UTF8.GetBytes(xml));
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, pdf)]);

        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.FailureReason.ShouldBeNull();
        document.Status.ShouldNotBe(DocumentStatus.Failed);
        document.PageCount.ShouldBe(2);
        document.Type.ShouldBe(DocumentType.Invoice);
        document.TypeSource.ShouldBe(TypeSource.Llm);
        document.ExtractionMethod.ShouldBe(ExtractionMethod.Llm);
        document.DocumentNumber.ShouldBe(TestPdfs.InvoiceNumber);

        // Stage 2 keeps only XML the parser recognises; one it recognises but cannot parse is skipped by stage 4.
        var events = await host.EventsAsync(document.Id);
        if (detectable)
        {
            document.EmbeddedXmlBlob.ShouldNotBeNull();
            events.Single(e => e.ToStatus == DocumentStatus.Classified).DetailsJson.ShouldNotBeNull().ShouldContain("could not be parsed");
        }
        else
        {
            document.EmbeddedXmlBlob.ShouldBeNull();
            events.Single(e => e.ToStatus == DocumentStatus.Normalized).DetailsJson.ShouldNotBeNull().ShouldContain($"\"embeddedXmlIgnored\":\"{fileName}\"");
        }

        host.Llm.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task A_document_reclassified_into_a_type_without_extraction_loses_the_old_invoice_data()
    {
        await using var host = await PipelineTestHost.StartAsync(settings =>
        {
            settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0.99";
            settings["DocumentProcessing:Splitter:ConfidenceThreshold"] = "0";
        });
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice, usage: new LlmUsage(6_000, 800));
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();
        var invoice = await host.SingleDocumentAsync(ingestion.Id);
        invoice.TotalGross.ShouldBe(1487.50m);

        // A reviewer reprocesses from Classify, and this time the model says delivery note.
        host.Llm.Enqueue("""{ "documentType": "DeliveryNote", "confidence": 0.95, "language": "de", "supplierNameHint": null }""");
        await host.WithDbAsync(async db =>
        {
            var row = await db.Documents.SingleAsync(d => d.Id == invoice.Id);
            row.Status = DocumentStatus.Split;
            await db.SaveChangesAsync();
        });
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, invoice.Id, PipelineStage.Classify), CancellationToken.None);
        await host.RunUntilIdleAsync();

        var note = await host.DocumentAsync(invoice.Id);
        note.Type.ShouldBe(DocumentType.DeliveryNote);
        note.Flags.ShouldContain(f => f.Code == FlagCodes.UnsupportedType && f.ResolvedAt == null);
        note.CanonicalJson.ShouldBeNull();
        note.CanonicalSchemaVersion.ShouldBeNull();
        note.DocumentNumber.ShouldBeNull();
        note.DocumentNumberNormalized.ShouldBeNull();
        note.IssueDate.ShouldBeNull();
        note.Currency.ShouldBeNull();
        note.TotalGross.ShouldBeNull();
        note.ExtractionMethod.ShouldBeNull();
        note.Costs.LlmInputTokens.ShouldBe(6_000, "what was spent stays spent");
        host.Llm.Remaining.ShouldBe(0);
    }
}
