using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Tests;

/// <summary>The runner against the real stages, SQLite, file-system blobs and the scripted LLM provider (docs/03).</summary>
public class PipelineEndToEndTests
{
    private const string PdfMime = "application/pdf";
    private const string XmlMime = "application/xml";

    [Fact]
    public async Task Ubl_e_invoice_runs_the_structured_path_to_auto_approval_with_the_timeline_in_order()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: "DE812345678", emailDomains: ["fischhandel-mueller.example"]);
        var ingestion = await host.IngestAsync(
            PipelineTestHost.TenantA,
            [new IngestFile("rechnung.xml", XmlMime, PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))],
            from: "Buchhaltung <rechnung@fischhandel-mueller.example>");

        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.FailureReason.ShouldBeNull();
        document.Status.ShouldBe(DocumentStatus.AutoApproved);
        document.Type.ShouldBe(DocumentType.Invoice);
        document.TypeSource.ShouldBe(TypeSource.Structured);
        document.TypeConfidence.ShouldBe(1.0);
        document.ExtractionMethod.ShouldBe(ExtractionMethod.StructuredXml);
        document.CanonicalJson.ShouldNotBeNullOrWhiteSpace();
        document.DocumentNumber.ShouldBe("RE-2026-10412");
        document.DocumentNumberNormalized.ShouldBe("RE-2026-10412");
        document.TotalGross.ShouldBe(437.99m);
        document.Currency.ShouldBe("EUR");
        document.IssueDate.ShouldBe(new DateOnly(2026, 10, 27));
        document.PageCount.ShouldBe(0);
        document.EmbeddedXmlBlob.ShouldNotBeNull();
        document.SupplierId.ShouldBe(supplier.Id);
        document.SupplierMatchConfidence.ShouldBe(0.98);
        document.ApprovedBy.ShouldBe("pipeline");
        document.ApprovedAt.ShouldBe(PipelineTestHost.Now);
        document.Flags.Where(f => f.ResolvedAt is null).ShouldBeEmpty();
        document.IngestionItem!.Kind.ShouldBe(ItemKind.Xml);

        var invoice = CanonicalDocumentSerializer.DeserializeInvoice(document.CanonicalJson!);
        invoice.Supplier.Value!.Name.ShouldBe("Fischhandel Müller GmbH");
        invoice.Lines.Count.ShouldBe(3);

        var events = await host.EventsAsync(document.Id);
        events.Select(e => (e.EventType, e.ToStatus)).ShouldBe(
        [
            (DocumentEventTypes.Received, DocumentStatus.Received),
            (DocumentEventTypes.StageCompleted, DocumentStatus.Normalized),
            (DocumentEventTypes.StageCompleted, DocumentStatus.Split),
            (DocumentEventTypes.StageCompleted, DocumentStatus.Classified),
            (DocumentEventTypes.StageCompleted, DocumentStatus.Matched),
            (DocumentEventTypes.StageCompleted, DocumentStatus.Extracted),
            (DocumentEventTypes.StageCompleted, DocumentStatus.Validated),
            (DocumentEventTypes.AutoApproved, DocumentStatus.AutoApproved),
            (DocumentEventTypes.StageCompleted, DocumentStatus.AutoApproved),
        ]);
        events.ShouldAllBe(e => e.Actor == "pipeline");

        var outbox = await host.WithDbAsync(db => db.OutboxMessages.AsNoTracking().Where(m => m.DocumentId == document.Id).ToListAsync());
        var message = outbox.ShouldHaveSingleItem();
        message.EventType.ShouldBe(DocumentEventTypes.Approved);
        message.DeliveredAt.ShouldBeNull();
        message.PayloadJson.ShouldContain(document.Id.ToString());

        var stored = await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == ingestion.Id));
        stored.Status.ShouldBe(IngestionStatus.Unpacked);
        (await host.WithDbAsync(db => db.ReviewTasks.AsNoTracking().CountAsync())).ShouldBe(0);
        (await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().CountAsync())).ShouldBe(0, "the structured path never calls the gateway");
    }

    [Fact]
    public async Task Two_page_pdf_runs_the_llm_path_with_page_images_stored()
    {
        await using var host = await PipelineTestHost.StartAsync(settings =>
        {
            // Force the keyword classifier below threshold so the scripted LLM classification is used, and keep the
            // heuristic splitter confident so no split call is spent on a two-page invoice.
            settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0.99";
            settings["DocumentProcessing:Splitter:ConfidenceThreshold"] = "0";
        });
        await host.AddReplayProviderAsync();
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Musterfirma GmbH");
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice, usage: new LlmUsage(500, 20));
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice, usage: new LlmUsage(6_000, 800));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.FailureReason.ShouldBeNull();
        document.Status.ShouldBeOneOf(DocumentStatus.AutoApproved, DocumentStatus.NeedsReview);
        document.PageCount.ShouldBe(2);
        document.PageEnd.ShouldBe(2);
        document.PageImageKeys.Count.ShouldBe(2);
        foreach (var key in document.PageImageKeys)
        {
            (await host.DerivedBlobExistsAsync(key)).ShouldBeTrue(key);
        }

        document.TextLayerBlob.ShouldNotBeNull().Container.ShouldBe(BlobContainers.Derived);
        document.NormalizedBlob.ShouldNotBeNull().ContentType.ShouldBe(PdfMime);
        document.EmbeddedXmlBlob.ShouldBeNull();
        document.Type.ShouldBe(DocumentType.Invoice);
        document.TypeSource.ShouldBe(TypeSource.Llm);
        document.TypeConfidence.ShouldBe(0.9);
        document.SupplierId.ShouldBe(supplier.Id, "fuzzy name match on the first page");
        document.ExtractionMethod.ShouldBe(ExtractionMethod.Llm);
        document.DocumentNumber.ShouldBe(TestPdfs.InvoiceNumber);
        document.TotalGross.ShouldBe(1487.50m);
        document.Costs.LlmInputTokens.ShouldBe(6_000);
        document.Costs.LlmOutputTokens.ShouldBe(800);
        document.Costs.LlmProvider.ShouldBe(nameof(LlmProviderKind.Replay));
        document.Costs.KeyOwner.ShouldBe(KeyOwner.Platform);

        var events = await host.EventsAsync(document.Id);
        events.Select(e => e.ToStatus).ShouldContain(DocumentStatus.Validated);
        events.ShouldContain(e => e.EventType == DocumentEventTypes.StageCompleted && e.ToStatus == DocumentStatus.Classified && e.DetailsJson!.Contains("\"usedLlm\":true"));

        var usage = await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().Where(u => u.TenantId == PipelineTestHost.TenantA).ToListAsync());
        usage.Select(u => u.Stage).ShouldBe([LlmStage.Classify, LlmStage.Extract], ignoreOrder: true);
        usage.ShouldAllBe(u => u.DocumentId == document.Id && u.Error == null);
        host.Llm.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task Corrupt_pdf_fails_the_normalize_stage_without_a_retry()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("broken.pdf", PdfMime, TestPdfs.NotAPdf())]);

        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.Failed);
        document.FailureReason.ShouldNotBeNull().ShouldStartWith(nameof(DocumentProcessingFailure.CorruptPdf));

        var events = await host.EventsAsync(document.Id);
        events.Select(e => e.EventType).ShouldBe([DocumentEventTypes.Received, DocumentEventTypes.StageFailed]);
        var failed = events[1];
        failed.FromStatus.ShouldBe(DocumentStatus.Received);
        failed.ToStatus.ShouldBe(DocumentStatus.Failed);
        failed.DetailsJson.ShouldNotBeNull().ShouldContain("\"attempt\":1");
        failed.DetailsJson.ShouldContain("\"retry\":false");
    }

    [Fact]
    public async Task Same_bytes_twice_are_one_document_and_one_duplicate_item()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var xml = PipelineTestHost.Fixture("peppol-bis3-invoice.xml");
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("a.xml", XmlMime, xml), new IngestFile("b.xml", XmlMime, xml)]);
        await host.RunUntilIdleAsync();

        var items = await host.WithDbAsync(db => db.IngestionItems.AsNoTracking().Where(i => i.IngestionId == ingestion.Id).OrderBy(i => i.FileName).ToListAsync());
        items.Count.ShouldBe(2);
        items[0].Disposition.ShouldBe(ItemDisposition.Candidate);
        items[1].Disposition.ShouldBe(ItemDisposition.Duplicate);
        items[1].DuplicateOfItemId.ShouldBe(items[0].Id);
        items[1].DispositionReason.ShouldNotBeNull();
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync(d => d.IngestionItemId == items[0].Id))).ShouldBe(1);
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync(d => d.IngestionItemId == items[1].Id))).ShouldBe(0);

        // The same bytes in a later ingestion of the same tenant link back to the first item and create nothing.
        var again = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("c.xml", XmlMime, xml)]);
        await host.RunUntilIdleAsync();
        var later = await host.WithDbAsync(db => db.IngestionItems.AsNoTracking().SingleAsync(i => i.IngestionId == again.Id));
        later.Disposition.ShouldBe(ItemDisposition.Duplicate);
        later.DuplicateOfItemId.ShouldBe(items[0].Id);
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync())).ShouldBe(1);
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == again.Id))).Status.ShouldBe(IngestionStatus.Unpacked);
    }

    [Fact]
    public async Task Small_images_and_calendar_files_are_ignored_with_a_reason()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA,
        [
            new IngestFile("logo.png", "image/png", new byte[512]),
            new IngestFile("meeting.ics", "text/calendar", "BEGIN:VCALENDAR"u8.ToArray()),
        ]);
        await host.RunUntilIdleAsync();

        var items = await host.WithDbAsync(db => db.IngestionItems.AsNoTracking().Where(i => i.IngestionId == ingestion.Id).ToListAsync());
        items.ShouldAllBe(i => i.Disposition == ItemDisposition.Ignored && i.DispositionReason != null);
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync())).ShouldBe(0);
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == ingestion.Id))).Status.ShouldBe(IngestionStatus.Unpacked);
    }

    [Fact]
    public async Task Re_enqueuing_completed_stages_changes_nothing()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))]);
        await host.RunUntilIdleAsync();
        var before = await host.SingleDocumentAsync(ingestion.Id);
        before.FailureReason.ShouldBeNull();
        before.Status.ShouldBe(DocumentStatus.AutoApproved, "SUPPLIER_UNMATCHED is a warning (docs/02) and does not block auto-approval");
        before.Flags.ShouldContain(f => f.Code == FlagCodes.SupplierUnmatched);
        var eventsBefore = await host.EventsAsync(before.Id);

        foreach (var stage in new[] { PipelineStage.Normalize, PipelineStage.Classify, PipelineStage.Extract, PipelineStage.Validate, PipelineStage.Publish })
        {
            await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, before.Id, stage), CancellationToken.None);
        }

        await host.PipelineQueue.EnqueueAsync(new ProcessIngestion(PipelineTestHost.TenantA, ingestion.Id), CancellationToken.None);
        await host.RunUntilIdleAsync();

        var after = await host.SingleDocumentAsync(ingestion.Id);
        after.Status.ShouldBe(before.Status);
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
        after.Flags.Count.ShouldBe(before.Flags.Count);
        (await host.EventsAsync(before.Id)).Count.ShouldBe(eventsBefore.Count);
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync())).ShouldBe(1);
        (await host.WithDbAsync(db => db.ReviewTasks.AsNoTracking().CountAsync())).ShouldBe(0);
        (await host.WithDbAsync(db => db.OutboxMessages.AsNoTracking().CountAsync())).ShouldBe(1, "a second publish message for the same approval finds the outbox row");
    }

    [Fact]
    public async Task Retryable_llm_failure_is_retried_with_backoff_and_then_succeeds()
    {
        await using var host = await PipelineTestHost.StartAsync(settings => settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0.99");
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(new LlmException(LlmErrorKind.Transient, "502 from the provider"));
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice, usage: new LlmUsage(100, 10));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldNotBe(DocumentStatus.Failed);
        document.ExtractionMethod.ShouldBe(ExtractionMethod.Llm);
        document.FailureReason.ShouldBeNull();
        document.Flags.ShouldNotContain(f => f.Code == FlagCodes.LlmProviderUnavailable);

        var events = await host.EventsAsync(document.Id);
        var retry = events.Where(e => e.EventType == DocumentEventTypes.StageFailed).ShouldHaveSingleItem();
        retry.ToStatus.ShouldBe(DocumentStatus.Matched, "a retry leaves the status alone");
        retry.DetailsJson.ShouldNotBeNull().ShouldContain("\"retry\":true");
        retry.DetailsJson.ShouldContain("\"attempt\":1");
        retry.DetailsJson.ShouldContain("Transient");
        events.Last(e => e.ToStatus == DocumentStatus.Extracted).DetailsJson.ShouldNotBeNull().ShouldContain("\"method\":\"Llm\"");

        var usage = await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().Where(u => u.DocumentId == document.Id).ToListAsync());
        usage.Count(u => u.Stage == LlmStage.Extract && u.Error == LlmErrorKind.Transient).ShouldBe(1);
        usage.Count(u => u.Stage == LlmStage.Extract && u.Error == null).ShouldBe(1);
        host.Llm.Remaining.ShouldBe(0);
        host.Clock.GetUtcNow().ShouldBeGreaterThan(PipelineTestHost.Now, "the backoff waited on the fake clock, which the host advanced");
    }

    [Fact]
    public async Task Provider_that_keeps_failing_ends_failed_with_the_provider_flag_after_the_last_attempt()
    {
        await using var host = await PipelineTestHost.StartAsync(settings => settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0.99");
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(new LlmException(LlmErrorKind.RateLimited, "429", TimeSpan.FromMilliseconds(10)));
        host.Llm.Enqueue(new LlmException(LlmErrorKind.Transient, "503"));
        host.Llm.Enqueue(new LlmException(LlmErrorKind.Transient, "503 again"));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.Failed);
        document.FailureReason.ShouldNotBeNull().ShouldContain("503 again");
        document.Flags.ShouldContain(f => f.Code == FlagCodes.LlmProviderUnavailable && f.Severity == FlagSeverity.Blocking && f.ResolvedAt == null);
        (await host.EventsAsync(document.Id)).Count(e => e.EventType == DocumentEventTypes.StageFailed).ShouldBe(3, "two retries and the final failure");
        host.Llm.Remaining.ShouldBe(0);
        host.Clock.GetUtcNow().ShouldBeGreaterThan(PipelineTestHost.Now, "both backoffs waited on the fake clock, which the host advanced");
    }

    [Fact]
    public async Task Missing_provider_configuration_fails_at_once_with_the_provider_flag()
    {
        // No active provider: the dev seed's platform config is Disabled, so the gateway reports Unavailable.
        await using var host = await PipelineTestHost.StartAsync(settings => settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0.99");
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.Failed);
        document.FailureReason.ShouldNotBeNull().ShouldContain("Unavailable");
        document.Flags.ShouldContain(f => f.Code == FlagCodes.LlmProviderUnavailable);
        (await host.EventsAsync(document.Id)).Count(e => e.EventType == DocumentEventTypes.StageFailed).ShouldBe(1, "a blocked tenant is not retried");
    }

    [Fact]
    public async Task Duplicate_invoice_number_of_the_same_supplier_is_flagged_and_reviewed()
    {
        await using var host = await PipelineTestHost.StartAsync();
        await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: "DE812345678");
        var xml = PipelineTestHost.Fixture("peppol-bis3-invoice.xml");
        var first = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("first.xml", XmlMime, xml)]);
        await host.RunUntilIdleAsync();
        var original = await host.SingleDocumentAsync(first.Id);
        original.FailureReason.ShouldBeNull();
        original.Status.ShouldBe(DocumentStatus.AutoApproved);

        // Same number, different bytes (a re-send with a changed note), so it is not a byte-level duplicate.
        var resent = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(xml).Replace("Lieferung frei Haus.", "Lieferung frei Haus!", StringComparison.Ordinal));
        var second = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("second.xml", XmlMime, resent)]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(second.Id);
        document.FailureReason.ShouldBeNull();
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        document.Flags.ShouldContain(f => f.Code == FlagCodes.DuplicateSuspected && f.Severity == FlagSeverity.Blocking);
        var task = await host.WithDbAsync(db => db.ReviewTasks.AsNoTracking().SingleAsync(t => t.DocumentId == document.Id));
        task.Status.ShouldBe(ReviewTaskStatus.Open);
        task.Reason.ShouldContain(FlagCodes.DuplicateSuspected);
        (await host.EventsAsync(document.Id)).ShouldContain(e => e.EventType == DocumentEventTypes.ReviewRequested && e.ToStatus == DocumentStatus.NeedsReview);
        (await host.EventsAsync(document.Id)).ShouldContain(e => e.EventType == DocumentEventTypes.Flagged);
    }
}
