using System.Text.Json.Nodes;
using DocProc.Application.Pipeline;
using DocProc.Application.Routing;
using DocProc.Definitions.Pipelines;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Outputs.Lookups;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Support;
using DocProc.Pipeline.Tests.Delivery;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// P5 part A against the real stages (docs/11 §10, §16): supplier <c>pipelineOverrides</c> applied by the Extract and
/// Validate stages, nothing changed without <c>configurableSteps</c>, a refused pipeline, the <c>http-lookup</c> step
/// against a receiver on loopback (cached per request, failures as flags, the SSRF guard) and the LLM budgets.
/// </summary>
public sealed class ConfigurableStepsPipelineTests
{
    private const string PdfMime = "application/pdf";
    private const string XmlMime = "application/xml";
    private const string FischhandelVat = "DE812345678";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ overrides

    [Fact]
    public async Task A_supplier_override_switches_the_duplicate_check_off_and_records_the_pipeline()
    {
        await using var host = await PipelineTestHost.StartAsync();
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        await SetOverridesAsync(host, supplier.Id, """[{"op":"disable","step":"duplicate"}]""");

        var first = await IngestXmlAsync(host);
        var second = await IngestXmlAsync(host);

        first.Status.ShouldBe(DocumentStatus.AutoApproved);
        second.Status.ShouldBe(DocumentStatus.AutoApproved, "the same invoice number again, and the supplier switched the duplicate check off");
        second.Flags.ShouldNotContain(f => f.Code == FlagCodes.DuplicateSuspected);
        second.PipelineHash.ShouldNotBeNull();
        var snapshot = await host.WithDbAsync(db => db.PipelineSnapshots.AsNoTracking().SingleAsync(p => p.Hash == second.PipelineHash, Ct));
        snapshot.StepsJson.ShouldContain("\"duplicate\"");
        var events = await host.EventsAsync(second.Id);
        events.ShouldContain(e => e.EventType == StagePipeline.AppliedEvent && e.DetailsJson!.Contains("\"validate\""));
    }

    [Fact]
    public async Task Without_configurableSteps_the_overrides_change_nothing()
    {
        await using var host = await PipelineTestHost.StartAsync();
        await EnableAsync(host, configurableSteps: false);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        await SetOverridesAsync(host, supplier.Id, """[{"op":"disable","step":"duplicate"}]""");

        await IngestXmlAsync(host);
        var second = await IngestXmlAsync(host);

        second.Status.ShouldBe(DocumentStatus.NeedsReview);
        second.Flags.ShouldContain(f => f.Code == FlagCodes.DuplicateSuspected && f.ResolvedAt == null);
        second.PipelineHash.ShouldBeNull();
        (await host.WithDbAsync(db => db.PipelineSnapshots.AsNoTracking().CountAsync(Ct))).ShouldBe(0);
        (await host.EventsAsync(second.Id)).ShouldNotContain(e => e.EventType == StagePipeline.AppliedEvent);
    }

    [Fact]
    public async Task Overrides_stored_outside_the_API_that_touch_classify_identification_are_refused_and_the_type_s_steps_run()
    {
        await using var host = await PipelineTestHost.StartAsync();
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        await SetOverridesAsync(host, supplier.Id, """[{"op":"disable","step":"classify.identification"},{"op":"disable","step":"duplicate"}]""");

        await IngestXmlAsync(host);
        var second = await IngestXmlAsync(host);

        second.Status.ShouldBe(DocumentStatus.NeedsReview);
        second.Flags.ShouldContain(f => f.Code == FlagCodes.PipelineInvalid && f.ResolvedAt == null && f.Severity == FlagSeverity.Blocking);
        second.Flags.ShouldContain(f => f.Code == FlagCodes.DuplicateSuspected, "the refused layer's other operations do not apply either");
    }

    [Fact]
    public async Task A_supplier_override_switches_the_llm_off_and_the_invoice_goes_to_review_unfilled()
    {
        await using var host = await StartLlmHostAsync();
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Musterfirma GmbH");
        await SetOverridesAsync(host, supplier.Id, """[{"op":"disable","step":"llm"}]""");
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice, usage: new LlmUsage(500, 20));
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice, usage: new LlmUsage(6_000, 800));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.FailureReason.ShouldBeNull();
        document.SupplierId.ShouldBe(supplier.Id);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        document.ExtractionMethod.ShouldBeNull("nothing extracted it: the type's empty tree waits for the reviewer");
        document.CanonicalJson.ShouldNotBeNull();
        document.DocumentNumber.ShouldBeNull();
        (await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().CountAsync(u => u.Stage == LlmStage.Extract, Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task A_supplier_the_source_named_switches_the_llm_classifier_off()
    {
        await using var host = await StartLlmHostAsync(forceLlmClassification: true);
        await EnableAsync(host);
        var supplier = await RoutedSupplierAsync(host, """[{"op":"disable","step":"classify.llm"}]""");
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice, usage: new LlmUsage(6_000, 800));

        var ingestion = await Routing.SourceRoutingPipelineTests.IngestAsync(host, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())], RoutedSource);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.SupplierId.ShouldBe(supplier.Id);
        document.Type.ShouldBe(DocumentTypeKey.Invoice);
        document.TypeSource.ShouldBe(TypeSource.Rule, "the keyword rules decided; the LLM classifier was not asked");
        var stages = await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().Select(u => u.Stage).ToListAsync(Ct));
        stages.ShouldBe([LlmStage.Extract]);
    }

    [Fact]
    public async Task A_supplier_the_source_named_cuts_its_files_per_page()
    {
        await using var host = await StartLlmHostAsync();
        await EnableAsync(host);
        await RoutedSupplierAsync(host, """[{"op":"param","step":"split.heuristic","params":{"mode":"perPage"}},{"op":"disable","step":"llm"}]""");

        var ingestion = await Routing.SourceRoutingPipelineTests.IngestAsync(host, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())], RoutedSource);
        await host.RunUntilIdleAsync();

        var documents = await host.WithDbAsync(db => db.Documents.AsNoTracking().Where(d => d.IngestionItem!.IngestionId == ingestion.Id).ToListAsync(Ct));
        documents.Count.ShouldBe(2);
        documents.ShouldAllBe(d => d.PageCount == 1);
    }

    [Fact]
    public async Task Split_and_classify_take_a_supplier_s_overrides_only_when_the_source_named_it()
    {
        await using var host = await PipelineTestHost.StartAsync();
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        await SetOverridesAsync(host, supplier.Id, """[{"op":"param","step":"split.heuristic","params":{"mode":"perPage"}},{"op":"disable","step":"classify.llm"}]""");

        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var pipelines = scope.ServiceProvider.GetRequiredService<StagePipeline>();

        // What Match found in an earlier run by the VAT id (a :reprocess from Normalize or Classify keeps it): Split and
        // Classify run before Match, so this supplier's overrides do not apply there (docs/11 §10.1); Extract takes them.
        var byContent = new Document { Id = Guid.CreateVersion7(), TenantId = PipelineTestHost.TenantA, SupplierId = supplier.Id, SupplierMatchMethod = SupplierMatchMethod.Vat };
        (await pipelines.ResolveAsync(byContent, null, PipelinePhases.Split, Ct)).Phase(PipelinePhases.Split).Single(s => s.Kind == "split.heuristic").Text("mode").ShouldNotBe("perPage");
        (await pipelines.ResolveAsync(byContent, null, PipelinePhases.Classify, Ct)).Phase(PipelinePhases.Classify).Single(s => s.Kind == "classify.llm").Enabled.ShouldBeTrue();
        (await pipelines.ResolveAsync(byContent, null, PipelinePhases.Extract, Ct)).Steps.ShouldContain(s => s.Source == PipelineSources.Supplier, "from Match on the supplier is known");

        // The same supplier named by the source: its overrides apply from Split on.
        var routed = new Document { Id = Guid.CreateVersion7(), TenantId = PipelineTestHost.TenantA, SupplierId = supplier.Id, SupplierMatchMethod = SupplierMatchMethod.SourceRule };
        (await pipelines.ResolveAsync(routed, null, PipelinePhases.Split, Ct)).Phase(PipelinePhases.Split).Single(s => s.Kind == "split.heuristic").Text("mode").ShouldBe("perPage");
        (await pipelines.ResolveAsync(routed, null, PipelinePhases.Classify, Ct)).Phase(PipelinePhases.Classify).Single(s => s.Kind == "classify.llm").Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_supplier_s_autoApproveMinConfidence_is_the_confidence_threshold_of_its_documents()
    {
        await using var host = await StartLlmHostAsync(forceLlmClassification: true);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Musterfirma GmbH");
        await host.WithDbAsync(async db =>
        {
            var group = new SupplierGroup { Id = Guid.CreateVersion7(), TenantId = PipelineTestHost.TenantA, Name = "Strict", SettingsJson = """{"review":{"autoApproveMinConfidence":0.999}}""", CreatedAt = PipelineTestHost.Now, UpdatedAt = PipelineTestHost.Now };
            db.SupplierGroups.Add(group);
            (await db.Suppliers.SingleAsync(s => s.Id == supplier.Id, Ct)).GroupId = group.Id;
            await db.SaveChangesAsync(Ct);
        });
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice, usage: new LlmUsage(500, 20));
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice, usage: new LlmUsage(6_000, 800));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.FailureReason.ShouldBeNull();
        document.SupplierId.ShouldBe(supplier.Id);
        var validated = (await host.EventsAsync(document.Id)).Single(e => e.ToStatus == DocumentStatus.Validated);
        validated.DetailsJson.ShouldNotBeNull().ShouldContain("\"threshold\":0.999", Case.Sensitive, "the group's threshold, not the tenant's 0.8");
        document.Flags.ShouldContain(f => f.Code == FlagCodes.LowConfidence && f.ResolvedAt == null, "values the LLM read below 0.999 keep the document out of auto-approval");
        document.Status.ShouldBe(DocumentStatus.NeedsReview);

        // The supplier's own setting wins over its group's; without either the tenant's threshold applies.
        await host.WithDbAsync(async db =>
        {
            var row = await db.Suppliers.SingleAsync(s => s.Id == supplier.Id, Ct);
            row.SettingsJson = """{"review":{"autoApproveMinConfidence":0.5}}""";
            await db.SaveChangesAsync(Ct);
            var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA, Ct);
            var stored = await db.Documents.SingleAsync(d => d.Id == document.Id, Ct);
            (await DocumentChecks.ConfidenceThresholdAsync(db, stored, tenant, Ct)).ShouldBe(0.5);
            row.SettingsJson = null;
            row.GroupId = null;
            await db.SaveChangesAsync(Ct);
            (await DocumentChecks.ConfidenceThresholdAsync(db, stored, tenant, Ct)).ShouldBe(tenant.Settings.AutoApproveConfidenceThreshold);
        });
    }

    // ------------------------------------------------------------------ http-lookup

    [Fact]
    public async Task An_http_lookup_writes_the_answer_as_Inferred_and_a_reprocess_reuses_the_stored_answer()
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(200, """{"vendorId":"V-4711"}""") };
        await using var host = await StartLookupHostAsync(receiver);
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        var destination = await AddDestinationAsync(host, receiver.BaseUrl + "/vendors?vat={{output.vat}}");
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, """{"originalInvoiceReference": response.vendorId}"""));

        var document = await IngestXmlAsync(host);

        var request = receiver.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("GET");
        request.PathAndQuery.ShouldBe($"/vendors?vat={FischhandelVat}");
        request.Header("X-DocProc-Lookup-Step").ShouldBe("erpVendor");
        document.Status.ShouldBe(DocumentStatus.AutoApproved, "a lookup's value is Inferred with a confidence above the threshold");
        var field = (JsonNode.Parse(document.CanonicalJson!) as JsonObject)!["originalInvoiceReference"]!;
        field["status"]!.GetValue<string>().ShouldBe(nameof(FieldStatus.Inferred));
        field["source"]!["method"]!.GetValue<string>().ShouldBe(nameof(SourceMethod.Lookup));
        field["value"]!["verbatim"]!.GetValue<string>().ShouldBe("V-4711");
        document.AppliedConfigJson.ShouldNotBeNull().ShouldContain("\"lookups\"");
        var stored = await host.WithDbAsync(db => db.LookupResults.AsNoTracking().SingleAsync(r => r.DocumentId == document.Id, Ct));
        stored.Status.ShouldBe(LookupCallStatus.Succeeded);
        stored.AssignmentsJson.ShouldNotBeNull().ShouldContain("V-4711");

        // A reprocess from Extract sends the same request: the stored answer is used, the receiver is not called again.
        await ReprocessFromExtractAsync(host, document.Id);
        receiver.Requests.Count.ShouldBe(1);
        var again = await host.DocumentAsync(document.Id);
        again.CanonicalJson.ShouldNotBeNull().ShouldContain("V-4711");
        again.AppliedConfigJson.ShouldNotBeNull().ShouldContain("\"cached\":true");
    }

    [Fact]
    public async Task A_reprocess_after_the_response_mapping_changed_maps_the_stored_answer_again_without_a_second_call()
    {
        // docs/11 §10.2 (P5 fix 3): the answer is stored, not only what the first mapping made of it, so a fixed
        // `response` expression applies to a reprocess that sends the same request, and the receiver is called once.
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(200, """{"vendorId":"V-4711","vendorNo":"K-0042"}""") };
        await using var host = await StartLookupHostAsync(receiver);
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        var destination = await AddDestinationAsync(host, receiver.BaseUrl + "/vendors?vat={{output.vat}}");
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, """{"originalInvoiceReference": response.vendorId}"""));

        var document = await IngestXmlAsync(host);
        receiver.Requests.ShouldHaveSingleItem();
        document.CanonicalJson.ShouldNotBeNull().ShouldContain("V-4711");

        // The tenant fixes the mapping: the vendor number, not the id. The same request again: the stored answer is
        // mapped by the new expression, and the receiver is not called.
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, """{"originalInvoiceReference": response.vendorNo}"""));
        await ReprocessFromExtractAsync(host, document.Id);
        receiver.Requests.Count.ShouldBe(1, "the same request is not sent again");
        var again = await host.DocumentAsync(document.Id);
        var field = (JsonNode.Parse(again.CanonicalJson!) as JsonObject)!["originalInvoiceReference"]!;
        field["value"]!["verbatim"]!.GetValue<string>().ShouldBe("K-0042", "the new mapping ran on the stored answer");
        again.AppliedConfigJson.ShouldNotBeNull().ShouldContain("\"cached\":true");
        var stored = await host.WithDbAsync(db => db.LookupResults.AsNoTracking().SingleAsync(r => r.DocumentId == document.Id, Ct));
        stored.ResponseJson.ShouldNotBeNull().ShouldContain("K-0042");
        stored.AssignmentsJson.ShouldNotBeNull().ShouldContain("K-0042", customMessage: "the row keeps what the last mapping gave");
        stored.Attempts.ShouldBe(1, "the attempts of the call, not of the re-mapping");

        // A mapping that fails on the stored answer is LOOKUP_FAILED (mapping), still without a call; fixed again, it maps.
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, """"not an object""""));
        await ReprocessFromExtractAsync(host, document.Id);
        receiver.Requests.Count.ShouldBe(1);
        (await host.DocumentAsync(document.Id)).Flags.ShouldContain(f => f.Code == FlagCodes.LookupFailed && f.ResolvedAt == null && f.Params!["reason"] == "mapping");
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, """{"originalInvoiceReference": response.vendorId}"""));
        await ReprocessFromExtractAsync(host, document.Id);
        receiver.Requests.Count.ShouldBe(1);
        (await host.DocumentAsync(document.Id)).CanonicalJson.ShouldNotBeNull().ShouldContain("V-4711");
    }

    [Theory]
    [InlineData("\uFEFF{\"vendorId\":\"V-4711\"}", "V-4711", "{\"vendorId\":\"V-4711\"}")]
    [InlineData("<html>not json</html>", "none", "null")]
    public async Task A_lookup_keeps_the_answer_as_its_mapping_reads_it(string body, string expected, string kept)
    {
        // An answer with a byte order mark is kept without it (and maps as JSON); one that is not JSON is kept as null,
        // which the mapping sees as a null response, on the call and on every reprocess.
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(200, body) };
        await using var host = await StartLookupHostAsync(receiver);
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        var destination = await AddDestinationAsync(host, receiver.BaseUrl + "/vendors?vat={{output.vat}}");
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, """{"originalInvoiceReference": response = null ? "none" : response.vendorId}"""));

        var document = await IngestXmlAsync(host);
        await ReprocessFromExtractAsync(host, document.Id);
        receiver.Requests.Count.ShouldBe(1);
        var field = (JsonNode.Parse((await host.DocumentAsync(document.Id)).CanonicalJson!) as JsonObject)!["originalInvoiceReference"]!;
        field["value"]!["verbatim"]!.GetValue<string>().ShouldBe(expected);
        (await host.WithDbAsync(db => db.LookupResults.AsNoTracking().SingleAsync(r => r.DocumentId == document.Id, Ct))).ResponseJson.ShouldBe(kept);
    }

    [Fact]
    public async Task A_lookup_row_stored_without_its_answer_sends_the_request_once_more()
    {
        // A row stored before the answers were kept holds only the assignments: it cannot be mapped again, so the
        // request is sent once and the answer kept from then on.
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(200, """{"vendorId":"V-4711"}""") };
        await using var host = await StartLookupHostAsync(receiver);
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        var destination = await AddDestinationAsync(host, receiver.BaseUrl + "/vendors?vat={{output.vat}}");
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, """{"originalInvoiceReference": response.vendorId}"""));
        var document = await IngestXmlAsync(host);
        await host.WithDbAsync(async db =>
        {
            var row = await db.LookupResults.SingleAsync(r => r.DocumentId == document.Id, Ct);
            row.ResponseJson = null;
            await db.SaveChangesAsync(Ct);
        });

        await ReprocessFromExtractAsync(host, document.Id);
        receiver.Requests.Count.ShouldBe(2);
        (await host.WithDbAsync(db => db.LookupResults.AsNoTracking().SingleAsync(r => r.DocumentId == document.Id, Ct))).ResponseJson.ShouldNotBeNull();
        await ReprocessFromExtractAsync(host, document.Id);
        receiver.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_test_call_reaches_the_destination_every_time_and_stores_nothing()
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(200, """{"vendorId":"V-0815"}""") };
        await using var host = await StartLookupHostAsync(receiver);
        await EnableAsync(host);
        var destination = await AddDestinationAsync(host, receiver.BaseUrl + "/vendors?vat={{output.vat}}");
        // A document without lookups: the test call of the processing UI (POST /pipelines/lookups:test) runs on it.
        var document = await IngestXmlAsync(host);
        var step = new ResolvedStep("erpVendor", StepKindCatalogue.HttpLookup, PipelinePhases.Extract, new JsonObject
        {
            ["destinationId"] = destination.ToString("D"),
            ["request"] = """{"vat": "DE811177936"}""",
            ["response"] = """{"originalInvoiceReference": response.vendorId}""",
        }, Enabled: true, PipelineSources.Supplier, Fixed: false, Runs: true);

        for (var call = 1; call <= 2; call++)
        {
            using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
            var lookups = scope.ServiceProvider.GetRequiredService<HttpLookupService>();
            var outcome = await lookups.RunAsync(new LookupCall(PipelineTestHost.TenantA, document.Id, document.Version, document.Type.Value, step, new JsonObject(), new DateOnly(2026, 11, 5)) { DryRun = true }, Ct);
            outcome.Succeeded.ShouldBeTrue(outcome.Message);
            outcome.Cached.ShouldBeFalse("a test call never answers from the stored results");
            outcome.Assignments["originalInvoiceReference"]!.GetValue<string>().ShouldBe("V-0815");
            receiver.Requests.Count.ShouldBe(call);
        }

        receiver.Requests[0].PathAndQuery.ShouldBe("/vendors?vat=DE811177936");
        (await host.WithDbAsync(db => db.LookupResults.AsNoTracking().CountAsync(r => r.DocumentId == document.Id, Ct))).ShouldBe(0, "nothing is stored");
        (await host.DocumentAsync(document.Id)).CanonicalJson.ShouldNotBeNull().ShouldNotContain("V-0815", customMessage: "nothing is written to the document");
    }

    [Fact]
    public async Task A_failing_lookup_is_retried_within_its_bound_then_raises_LOOKUP_FAILED_and_the_document_goes_on()
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(503, "{}") };
        await using var host = await StartLookupHostAsync(receiver);
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        var destination = await AddDestinationAsync(host, receiver.BaseUrl + "/vendors?vat={{output.vat}}");
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, """{"originalInvoiceReference": response.vendorId}""", retries: 2));

        var document = await IngestXmlAsync(host);

        receiver.Requests.Count.ShouldBe(3, "one call and two retries");
        document.FailureReason.ShouldBeNull();
        var flag = document.Flags.Where(f => f.Code == FlagCodes.LookupFailed).ShouldHaveSingleItem();
        flag.Severity.ShouldBe(FlagSeverity.Warning);
        flag.Params.ShouldNotBeNull()["reason"].ShouldBe("status");
        document.Status.ShouldBe(DocumentStatus.AutoApproved, "onFailure warn: the document goes on as it is (block sends it to review)");
        (await host.WithDbAsync(db => db.LookupResults.AsNoTracking().SingleAsync(r => r.DocumentId == document.Id, Ct))).Status.ShouldBe(LookupCallStatus.Failed);
    }

    [Fact]
    public async Task A_lookup_to_a_loopback_address_is_refused_by_the_guard_without_a_request()
    {
        using var receiver = new FakeReceiver();
        await using var host = await PipelineTestHost.StartAsync();
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        var destination = await AddDestinationAsync(host, receiver.BaseUrl + "/vendors?vat={{output.vat}}");
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, """{"originalInvoiceReference": response.vendorId}""", onFailure: "block"));

        var document = await IngestXmlAsync(host);

        receiver.Requests.ShouldBeEmpty();
        var flag = document.Flags.Where(f => f.Code == FlagCodes.LookupFailed).ShouldHaveSingleItem();
        flag.Severity.ShouldBe(FlagSeverity.Blocking, "onFailure: block");
        flag.Params.ShouldNotBeNull()["reason"].ShouldBe("blocked");
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
    }

    [Fact]
    public async Task A_lookup_answer_too_large_or_not_mappable_is_a_flag_not_a_crash()
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(200, "{\"big\":\"" + new string('x', HttpLookupService.MaxResponseBytes + 10) + "\"}") };
        await using var host = await StartLookupHostAsync(receiver);
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: FischhandelVat);
        var destination = await AddDestinationAsync(host, receiver.BaseUrl + "/vendors?vat={{output.vat}}");
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, """{"originalInvoiceReference": response.vendorId}"""));

        var document = await IngestXmlAsync(host);

        document.FailureReason.ShouldBeNull();
        document.Flags.Where(f => f.Code == FlagCodes.LookupFailed).ShouldHaveSingleItem().Params.ShouldNotBeNull()["reason"].ShouldBe("tooLarge");

        receiver.Default = new ReceiverAnswer(200, """{"vendorId":"V-1"}""");
        await SetOverridesAsync(host, supplier.Id, Lookup(destination, "\"not an object\""));
        var second = await IngestXmlAsync(host, number: "RE-2");
        second.Flags.Where(f => f.Code == FlagCodes.LookupFailed).ShouldHaveSingleItem().Params.ShouldNotBeNull()["reason"].ShouldBe("mapping");
    }

    // ------------------------------------------------------------------ LLM budgets

    [Fact]
    public async Task A_budget_of_zero_keeps_every_llm_stage_from_calling_and_the_document_goes_to_review()
    {
        await using var host = await StartLlmHostAsync(forceLlmClassification: true);
        await AddBudgetAsync(host, LlmBudgetPeriod.Day, 0m);
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice, usage: new LlmUsage(500, 20));
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice, usage: new LlmUsage(6_000, 800));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.FailureReason.ShouldBeNull();
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        (await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().CountAsync(Ct))).ShouldBe(0, "no call was made");
        var budgetFlags = document.Flags.Where(f => f.Code == FlagCodes.LlmBudgetExceeded && f.ResolvedAt == null).ToList();
        document.Type.ShouldBe(DocumentTypeKey.Invoice, $"type {document.Type} ({document.TypeSource}, {document.TypeConfidence})");
        budgetFlags.Select(f => f.Params!["stepId"]).Order().ShouldBe([StepKindCatalogue.ClassifyLlm, StepKindCatalogue.Llm]);
        budgetFlags.ShouldAllBe(f => f.Severity == FlagSeverity.Blocking && f.Params!["period"] == "day");
        document.ExtractionMethod.ShouldBeNull();
    }

    [Fact]
    public async Task A_stage_budget_stops_only_its_stage_and_a_budget_with_room_lets_calls_through()
    {
        await using var host = await StartLlmHostAsync(forceLlmClassification: true);
        await AddBudgetAsync(host, LlmBudgetPeriod.Day, 0m, LlmStage.Extract);
        await AddBudgetAsync(host, LlmBudgetPeriod.Month, 100m);
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice, usage: new LlmUsage(500, 20));
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice, usage: new LlmUsage(6_000, 800));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        var usage = await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().Select(u => u.Stage).ToListAsync(Ct));
        usage.ShouldBe([LlmStage.Classify]);
        document.TypeSource.ShouldBe(TypeSource.Llm);
        document.Flags.Where(f => f.Code == FlagCodes.LlmBudgetExceeded).ShouldHaveSingleItem().Params!["stepId"].ShouldBe(StepKindCatalogue.Llm);
    }

    [Fact]
    public async Task The_llm_step_s_maxCostPerDocument_skips_the_call_once_the_document_reached_it()
    {
        await using var host = await StartLlmHostAsync();
        await EnableAsync(host);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Musterfirma GmbH");
        await SetOverridesAsync(host, supplier.Id, """[{"op":"param","step":"llm","params":{"maxCostPerDocument":0}}]""");
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice, usage: new LlmUsage(500, 20));
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice, usage: new LlmUsage(6_000, 800));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        var flag = document.Flags.Where(f => f.Code == FlagCodes.LlmBudgetExceeded).ShouldHaveSingleItem();
        flag.Params!["period"].ShouldBe("document");
        (await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().CountAsync(u => u.Stage == LlmStage.Extract, Ct))).ShouldBe(0);
    }

    // ------------------------------------------------------------------ helpers

    private static Task<PipelineTestHost> StartLlmHostAsync(bool forceLlmClassification = false) =>
        StartWithReplayAsync(settings =>
        {
            if (forceLlmClassification)
            {
                settings["Pipeline:LlmClassificationThreshold"] = "0.999";
            }

            settings["DocumentProcessing:Splitter:ConfidenceThreshold"] = "0";
        });

    private static async Task<PipelineTestHost> StartWithReplayAsync(Action<Dictionary<string, string?>> configure)
    {
        var host = await PipelineTestHost.StartAsync(configure);
        await host.AddReplayProviderAsync();
        return host;
    }

    private static Task<PipelineTestHost> StartLookupHostAsync(FakeReceiver receiver) =>
        PipelineTestHost.StartAsync(services: collection => collection.AddSingleton(new DeliveryEndpointPolicy([receiver.Host])));

    private static Task EnableAsync(PipelineTestHost host, bool configurableSteps = true) => host.WithDbAsync(async db =>
    {
        var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA, Ct);
        tenant.Settings.Features!.ConfigurableTypes = true;
        tenant.Settings.Features!.ConfigurableSteps = configurableSteps;
        await db.SaveChangesAsync(Ct);
    });

    private static readonly SourceMetadata RoutedSource = new(Sender: "Rechnung <rechnung@musterfirma.example>") { SenderAuth = new SenderAuthentication { Dmarc = "pass", FromDomain = "musterfirma.example" } };

    private static async Task<Supplier> RoutedSupplierAsync(PipelineTestHost host, string overrides)
    {
        await Routing.SourceRoutingPipelineTests.EnableRoutingAsync(host, PipelineTestHost.TenantA);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Musterfirma GmbH");
        await Routing.SourceRoutingPipelineTests.AddRuleAsync(host, supplier.Id, r => r.Sender = "rechnung@musterfirma.example");
        await SetOverridesAsync(host, supplier.Id, overrides);
        return supplier;
    }

    private static Task SetOverridesAsync(PipelineTestHost host, Guid supplierId, string overrides) => host.WithDbAsync(async db =>
    {
        var supplier = await db.Suppliers.SingleAsync(s => s.Id == supplierId, Ct);
        supplier.SettingsJson = $$"""{"pipelineOverrides":{{overrides}}}""";
        await db.SaveChangesAsync(Ct);
    });

    private static Task AddBudgetAsync(PipelineTestHost host, LlmBudgetPeriod period, decimal limit, LlmStage? stage = null) => host.WithDbAsync(async db =>
    {
        db.LlmBudgets.Add(new LlmBudgetRecord { Id = Guid.CreateVersion7(), TenantId = PipelineTestHost.TenantA, Period = period, Stage = stage, LimitUsd = limit, CreatedAt = PipelineTestHost.Now, UpdatedAt = PipelineTestHost.Now });
        await db.SaveChangesAsync(Ct);
    });

    private static Task<Guid> AddDestinationAsync(PipelineTestHost host, string urlTemplate) => host.WithDbAsync(async db =>
    {
        var destination = new OutputDestination
        {
            Id = Guid.CreateVersion7(),
            TenantId = PipelineTestHost.TenantA,
            Name = "erp-vendors",
            Transport = OutputTransports.Http,
            ConfigJson = new JsonObject { ["urlTemplate"] = urlTemplate, ["auth"] = new JsonObject { ["type"] = "none" } }.ToJsonString(),
            CreatedBy = "test",
            CreatedAt = PipelineTestHost.Now,
            UpdatedAt = PipelineTestHost.Now,
        };
        db.OutputDestinations.Add(destination);
        await db.SaveChangesAsync(Ct);
        HttpLookupService.ResetBreakers();
        return destination.Id;
    });

    private static string Lookup(Guid destinationId, string response, int retries = 1, string onFailure = "warn") =>
        new JsonArray(new JsonObject
        {
            ["op"] = "insertAfter",
            ["step"] = "map",
            ["with"] = new JsonObject
            {
                ["id"] = "erpVendor",
                ["kind"] = "http-lookup",
                ["params"] = new JsonObject
                {
                    ["destinationId"] = destinationId.ToString("D"),
                    ["request"] = """{"vat": supplier.vatId}""",
                    ["response"] = response,
                    ["retries"] = retries,
                    ["timeoutMs"] = 2000,
                    ["onFailure"] = onFailure,
                },
            },
        }).ToJsonString();

    /// <summary>An e-invoice of the Fischhandel fixture with its own bytes (a new ingestion) and the given invoice number.</summary>
    private static async Task<Document> IngestXmlAsync(PipelineTestHost host, string number = "RE-2026-10412")
    {
        var marker = Guid.NewGuid().ToString("N");
        var bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(PipelineTestHost.FixtureVariant(marker))
            .Replace("<cbc:ID>RE-2026-10412</cbc:ID>", $"<cbc:ID>{number}</cbc:ID>", StringComparison.Ordinal));
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile($"{marker}.xml", XmlMime, bytes)]);
        await host.RunUntilIdleAsync();
        return await host.SingleDocumentAsync(ingestion.Id);
    }

    private static async Task ReprocessFromExtractAsync(PipelineTestHost host, Guid documentId)
    {
        await host.WithDbAsync(async db =>
        {
            var row = await db.Documents.SingleAsync(d => d.Id == documentId, Ct);
            row.Status = DocumentStatus.Matched; // as a reprocess from Extract leaves it
            await db.SaveChangesAsync(Ct);
        });
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, documentId, PipelineStage.Extract), Ct);
        await host.RunUntilIdleAsync();
    }
}
