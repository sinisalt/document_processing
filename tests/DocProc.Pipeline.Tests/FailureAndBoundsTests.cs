using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Runner;
using DocProc.Pipeline.Support;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// Failure handling that must not overwrite or repeat work (refused LLM requests, a failure record for a stage the
/// document already left), and values from outside (model text, upload headers, archive entry names) that must fit
/// their columns, which SQL Server enforces and SQLite does not.
/// </summary>
public class FailureAndBoundsTests
{
    private static readonly PipelineOptions Options = new() { RetryBaseDelay = TimeSpan.FromSeconds(5), MaxAttempts = 5, MaxRetryDelay = TimeSpan.FromMinutes(1) };

    [Theory]
    [InlineData(LlmErrorKind.ContentRefused)]
    [InlineData(LlmErrorKind.InvalidRequest)]
    public void Refused_and_rejected_llm_requests_fail_at_once_without_the_provider_flag(LlmErrorKind kind)
    {
        var decision = FailurePolicy.Decide(new LlmException(kind, "no"), 1, Options).ShouldBeOfType<FailureDecision.Fail>();
        decision.Reason.ShouldBe($"LLM provider {kind}: no");
        decision.FlagCode.ShouldBeNull("the provider works; it answered this request");
    }

    [Fact]
    public async Task A_refused_extraction_is_sent_once_and_the_document_fails()
    {
        await using var host = await PipelineTestHost.StartAsync(settings =>
        {
            settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0.99";
            settings["DocumentProcessing:Splitter:ConfidenceThreshold"] = "0";
        });
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        for (var i = 0; i < host.Options.MaxAttempts; i++)
        {
            host.Llm.Enqueue(new LlmException(LlmErrorKind.ContentRefused, "refused"));
        }

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", "application/pdf", TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.Failed);
        document.FailureReason.ShouldBe("LLM provider ContentRefused: refused");
        host.Llm.Requests.Count(r => r.Stage == LlmStage.Extract).ShouldBe(1);
    }

    [Fact]
    public async Task A_failure_record_for_a_stage_the_document_already_left_keeps_its_status_and_flags()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", "application/xml", PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))], enqueue: false);
        var document = await host.AddReceivedDocumentAsync(ingestion);
        await SetStatusAsync(host, document.Id, DocumentStatus.Extracted);

        // A twin Classify message failed on another node after this one's Classify, Match and Extract succeeded.
        using (var scope = host.CreateTenantScope(PipelineTestHost.TenantA))
        {
            await scope.ServiceProvider.GetRequiredService<FailureRecorder>()
                .RecordFailureAsync(new ProcessDocument(PipelineTestHost.TenantA, document.Id, PipelineStage.Classify), "LLM provider Auth: key revoked", FlagCodes.LlmProviderUnavailable, CancellationToken.None);
        }

        var after = await host.DocumentAsync(document.Id);
        after.Status.ShouldBe(DocumentStatus.Extracted, "the twin's success stands");
        after.FailureReason.ShouldBeNull();
        after.Flags.ShouldNotContain(f => f.Code == FlagCodes.LlmProviderUnavailable);
        var recorded = (await host.EventsAsync(document.Id)).Last();
        recorded.EventType.ShouldBe(DocumentEventTypes.StageFailed, "the audit trail keeps the attempt");
        recorded.ToStatus.ShouldBe(DocumentStatus.Extracted);

        // The same record while the document still waits for classification fails it, with the flag.
        await SetStatusAsync(host, document.Id, DocumentStatus.Split);
        using (var scope = host.CreateTenantScope(PipelineTestHost.TenantA))
        {
            await scope.ServiceProvider.GetRequiredService<FailureRecorder>()
                .RecordFailureAsync(new ProcessDocument(PipelineTestHost.TenantA, document.Id, PipelineStage.Classify), "LLM provider Auth: key revoked", FlagCodes.LlmProviderUnavailable, CancellationToken.None);
        }

        var failed = await host.DocumentAsync(document.Id);
        failed.Status.ShouldBe(DocumentStatus.Failed);
        failed.Flags.ShouldContain(f => f.Code == FlagCodes.LlmProviderUnavailable);
    }

    [Fact]
    public async Task Flag_messages_and_fields_built_from_model_output_are_cut_to_their_columns()
    {
        var flag = new DocumentFlag(FlagCodes.UnreadablePages, FlagSeverity.Warning, "Page 1, footer: " + new string('r', 1500), "lines[0]." + new string('f', 300));
        var record = DocumentFlagRecord.FromDomain(Guid.NewGuid(), Guid.NewGuid(), flag, DateTimeOffset.UnixEpoch);
        record.Message.Length.ShouldBe(DocumentFlagRecord.MaxMessageLength);
        record.Message.ShouldStartWith("Page 1, footer: rrr");
        record.Field.ShouldNotBeNull().Length.ShouldBe(DocumentFlagRecord.MaxFieldLength);

        // Parameters quote the same model output (the region's reason), so they are cut like the message.
        var withParams = flag with { Params = FlagParams.Of(("page", "1"), ("detail", new string('r', 1500))) };
        var withParamsRecord = DocumentFlagRecord.FromDomain(Guid.NewGuid(), Guid.NewGuid(), withParams, DateTimeOffset.UnixEpoch);
        withParamsRecord.Params.ShouldNotBeNull()["detail"].Length.ShouldBe(DocumentFlagRecord.MaxParamLength);
        withParamsRecord.Params["page"].ShouldBe("1");
        withParamsRecord.ToDomain().Params.ShouldNotBeNull()["page"].ShouldBe("1");
        record.Params.ShouldBeNull();

        var shortFlag = FlagCodes.Create(FlagCodes.TypeUnknown, "short", "type");
        DocumentFlagRecord.FromDomain(Guid.NewGuid(), Guid.NewGuid(), shortFlag, DateTimeOffset.UnixEpoch).ShouldSatisfyAllConditions(
            r => r.Message.ShouldBe("short"),
            r => r.Field.ShouldBe("type"));

        // The constants are the model's column sizes, so the cut is exactly what SQL Server accepts.
        await using var host = await PipelineTestHost.StartAsync();
        await host.WithDbAsync(db =>
        {
            var entity = db.Model.FindEntityType(typeof(DocumentFlagRecord)).ShouldNotBeNull();
            entity.FindProperty(nameof(DocumentFlagRecord.Message))!.GetMaxLength().ShouldBe(DocumentFlagRecord.MaxMessageLength);
            entity.FindProperty(nameof(DocumentFlagRecord.Field))!.GetMaxLength().ShouldBe(DocumentFlagRecord.MaxFieldLength);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_archive_entry_with_a_name_longer_than_the_column_keeps_its_extension_and_is_processed()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var longName = new string('n', 300) + ".xml";
        var zip = TestZips.With((longName, PipelineTestHost.Fixture("peppol-bis3-invoice.xml")));
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("batch.zip", "application/zip", zip)]);

        await host.RunUntilIdleAsync();

        var items = await host.ItemsAsync(ingestion.Id);
        var child = items.Single(i => i.ParentItemId is not null);
        child.FileName.Length.ShouldBe(UploadIntake.MaxFileNameLength);
        child.FileName.ShouldEndWith(".xml");
        child.Disposition.ShouldBe(ItemDisposition.Accepted);
        var stored = await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == ingestion.Id));
        stored.Status.ShouldBe(IngestionStatus.Unpacked);
        await host.WithDbAsync(async db => (await db.Documents.AsNoTracking().SingleAsync(d => d.IngestionItemId == child.Id)).Status.ShouldBe(DocumentStatus.AutoApproved));
    }

    [Fact]
    public async Task An_upload_with_an_over_long_media_type_stores_rows_that_fit_their_columns()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var blobs = host.Services.GetRequiredService<IBlobStore>();
        var bogus = "application/" + new string('x', 200);
        var bytes = PipelineTestHost.Fixture("peppol-bis3-invoice.xml");
        var upload = new UploadFile("rechnung.xml", bogus, ItemKind.Xml, () => new MemoryStream(bytes, writable: false));

        var stored = await UploadIntake.StoreAsync(blobs, PipelineTestHost.TenantA, [upload], CancellationToken.None);
        var ingestion = UploadIntake.CreateIngestion(PipelineTestHost.TenantA, DevSeed.TenantAUploadChannel, null, stored, PipelineTestHost.Now);

        ingestion.RawBlob.ContentType.ShouldBe("application/octet-stream");
        var item = ingestion.Items.ShouldHaveSingleItem();
        item.Blob.ContentType.ShouldBe("application/octet-stream");
        item.MimeType.Length.ShouldBe(UploadIntake.MaxContentTypeLength);
        await host.WithDbAsync(db =>
        {
            var reference = db.Model.FindEntityType(typeof(IngestionItem))!.FindComplexProperty(nameof(IngestionItem.Blob))!.ComplexType;
            reference.FindProperty(nameof(BlobReference.ContentType))!.GetMaxLength().ShouldBe(UploadIntake.MaxContentTypeLength);
            return Task.CompletedTask;
        });

        UploadIntake.FitFileName(new string('a', 400) + ".pdf").ShouldSatisfyAllConditions(
            n => n.Length.ShouldBe(UploadIntake.MaxFileNameLength),
            n => n.ShouldEndWith(".pdf"));
        UploadIntake.FitFileName("rechnung.pdf").ShouldBe("rechnung.pdf");
        PipelineBlobs.ToEntity(new BlobRef("raw", "k", new string('0', 64), 1, bogus)).ContentType.ShouldBe("application/octet-stream");
    }

    private static Task SetStatusAsync(PipelineTestHost host, Guid documentId, DocumentStatus status) =>
        host.WithDbAsync(async db =>
        {
            var row = await db.Documents.SingleAsync(d => d.Id == documentId);
            row.Status = status;
            await db.SaveChangesAsync();
        });
}
