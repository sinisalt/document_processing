using System.Text.Json.Nodes;
using DocProc.Application.Documents;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The pipeline over definition-driven types (docs/11 §5.6, §9.1, §11, §6.2): with <c>configurableTypes</c> a purchase
/// order is stored as the empty tree of its type version (schema version 2) for the review form; a fork of the invoice
/// with an added field asks the LLM for it in <c>x_custom</c>, stores it, indexes it and records the schema in
/// <c>AppliedConfigJson</c>; the review task takes its priority from <c>review.priority</c>. Without the switch every
/// document is processed as before.
/// </summary>
public sealed class DefinitionDrivenPipelineTests
{
    private const string PdfMime = "application/pdf";

    private const string ClassifyPurchaseOrder = """{ "documentType": "PurchaseOrder", "confidence": 0.9, "language": "de", "supplierNameHint": "Musterfirma GmbH" }""";

    private static Task<PipelineTestHost> StartAsync() => PipelineTestHost.StartAsync(settings =>
    {
        settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0.99";
        settings["DocumentProcessing:Splitter:ConfidenceThreshold"] = "0";
    });

    private static Task SetConfigurableAsync(PipelineTestHost host, bool on) => host.WithDbAsync(async db =>
    {
        var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA);
        tenant.Settings.Features!.ConfigurableTypes = on;
        await db.SaveChangesAsync();
    });

    /// <summary>Forks <paramref name="template"/> in place, lets <paramref name="edit"/> change the draft and publishes it.</summary>
    private static async Task ForkAsync(PipelineTestHost host, string template, Action<JsonObject> edit)
    {
        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var catalogue = scope.ServiceProvider.GetRequiredService<DocumentTypeCatalogue>();
        var copied = await catalogue.CopyAsync(PipelineTestHost.TenantA, template, null, "admin", CancellationToken.None);
        copied.Status.ShouldBe(CatalogueStatus.Ok, copied.Detail);
        var type = copied.Value!;
        var draft = type.Versions.Single(v => v.State == DocumentTypeVersionState.Draft);
        var json = JsonNode.Parse(draft.DefinitionJson)!.AsObject();
        edit(json);
        var saved = await catalogue.SaveDraftAsync(PipelineTestHost.TenantA, type.Definition.Key, draft.Version, json.ToJsonString(), draft.Revision, "admin", CancellationToken.None);
        saved.Status.ShouldBe(CatalogueStatus.Ok, saved.Detail);
        var published = await catalogue.PublishAsync(PipelineTestHost.TenantA, type.Definition.Key, draft.Version, null, "test", "admin", CancellationToken.None);
        published.Status.ShouldBe(CatalogueStatus.Ok, published.Detail + string.Join("; ", published.Report?.Problems.Select(p => p.Message) ?? []));
    }

    [Fact]
    public async Task Without_configurable_types_a_purchase_order_skips_extraction_as_before()
    {
        await using var host = await StartAsync();
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue(ClassifyPurchaseOrder);

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("bestellung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Type.ShouldBe(DocumentTypeKey.PurchaseOrder);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        document.CanonicalJson.ShouldBeNull();
        document.CanonicalSchemaVersion.ShouldNotBe(DefinitionTree.SchemaVersion);
        document.Flags.ShouldContain(f => f.Code == FlagCodes.UnsupportedType);
        (await host.WithDbAsync(db => db.DocumentFieldIndex.CountAsync())).ShouldBe(0);
        (await host.WithDbAsync(db => db.ReviewTasks.SingleAsync(t => t.DocumentId == document.Id))).Priority.ShouldBe(ReviewPriority.Normal);
    }

    [Fact]
    public async Task With_configurable_types_a_purchase_order_gets_the_tree_of_its_type_and_the_review_priority_of_its_definition()
    {
        await using var host = await StartAsync();
        await host.AddReplayProviderAsync();
        await SetConfigurableAsync(host, true);
        await ForkAsync(host, "std.purchase-order", json => json["processing"]!["review"]!["priority"] = "urgent");
        host.Llm.Enqueue(ClassifyPurchaseOrder);

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("bestellung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Type.ShouldBe(DocumentTypeKey.PurchaseOrder);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        document.CanonicalSchemaVersion.ShouldBe(DefinitionTree.SchemaVersion);
        var tree = JsonNode.Parse(document.CanonicalJson.ShouldNotBeNull())!.AsObject();
        tree["poNumber"]!["status"]!.GetValue<string>().ShouldBe("Missing");
        tree["lines"]!.AsArray().ShouldBeEmpty();
        document.DocumentTypeVersionId.ShouldNotBeNull();
        var flag = document.Flags.Single(f => f.Code == FlagCodes.UnsupportedType);
        flag.Params.ShouldNotBeNull()["reason"].ShouldBe("definition");
        var applied = JsonNode.Parse(document.AppliedConfigJson.ShouldNotBeNull())!;
        applied["typeKey"]!.GetValue<string>().ShouldBe("PurchaseOrder");
        applied["typeVersion"]!.GetValue<int>().ShouldBe(2);
        applied["typeVersionId"]!.GetValue<string>().ShouldBe(document.DocumentTypeVersionId.ToString());
        (await host.WithDbAsync(db => db.ReviewTasks.SingleAsync(t => t.DocumentId == document.Id))).Priority.ShouldBe(ReviewPriority.Urgent);
    }

    [Fact]
    public async Task Reprocessing_onto_the_published_version_keeps_what_the_reviewer_entered_and_pins_again()
    {
        await using var host = await StartAsync();
        await host.AddReplayProviderAsync();
        await SetConfigurableAsync(host, true);
        await ForkAsync(host, "std.purchase-order", _ => { });
        host.Llm.Enqueue(ClassifyPurchaseOrder);
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("bestellung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();
        var document = await host.SingleDocumentAsync(ingestion.Id);
        var pin = document.DocumentTypeVersionId.ShouldNotBeNull();

        // A reviewer's value, then what POST :reprocess with configMode current writes (from Extract): no pin.
        await host.WithDbAsync(async db =>
        {
            var row = await db.Documents.SingleAsync(d => d.Id == document.Id);
            var tree = JsonNode.Parse(row.CanonicalJson!)!.AsObject();
            tree["poNumber"] = JsonNode.Parse("""{"value":{"verbatim":"PO-2026-77","normalized":"PO-2026-77"},"status":"Corrected","confidence":1,"history":[]}""");
            row.CanonicalJson = tree.ToJsonString();
            row.Status = DocumentStatus.Matched;
            row.DocumentTypeVersionId = null;
            await db.SaveChangesAsync();
        });
        await host.PipelineQueue.EnqueueAsync(new Application.Pipeline.ProcessDocument(PipelineTestHost.TenantA, document.Id, Application.Pipeline.PipelineStage.Extract), CancellationToken.None);
        await host.RunUntilIdleAsync();

        var reprocessed = await host.DocumentAsync(document.Id);
        reprocessed.Status.ShouldBe(DocumentStatus.NeedsReview);
        var kept = JsonNode.Parse(reprocessed.CanonicalJson!)!.AsObject();
        kept["poNumber"]!["status"]!.GetValue<string>().ShouldBe("Corrected");
        kept["poNumber"]!["value"]!["verbatim"]!.GetValue<string>().ShouldBe("PO-2026-77");
        reprocessed.DocumentNumber.ShouldBe("PO-2026-77");
        reprocessed.DocumentTypeVersionId.ShouldBe(pin);

        // From Validate with configMode current: Validate pins the version it validates with.
        await host.WithDbAsync(async db =>
        {
            var row = await db.Documents.SingleAsync(d => d.Id == document.Id);
            row.Status = DocumentStatus.Extracted;
            row.DocumentTypeVersionId = null;
            await db.SaveChangesAsync();
        });
        await host.PipelineQueue.EnqueueAsync(new Application.Pipeline.ProcessDocument(PipelineTestHost.TenantA, document.Id, Application.Pipeline.PipelineStage.Validate), CancellationToken.None);
        await host.RunUntilIdleAsync();
        (await host.DocumentAsync(document.Id)).DocumentTypeVersionId.ShouldBe(pin);
    }

    [Fact]
    public async Task A_forked_invoice_asks_the_llm_for_its_added_field_and_stores_and_indexes_it()
    {
        await using var host = await StartAsync();
        await host.AddReplayProviderAsync();
        await host.AddSupplierAsync(PipelineTestHost.TenantA, "Musterfirma GmbH");
        await SetConfigurableAsync(host, true);
        await ForkAsync(host, "std.invoice", json => json["fields"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "fld_c_scr", "key": "supplierCustomerRef", "kind": "identifier", "indexed": true,
              "label": { "en": "Our customer no. at supplier" }, "description": "The account number the supplier assigned to us." }
            """)));
        var answer = JsonNode.Parse(ScriptedAnswers.ExtractGermanInvoice)!.AsObject();
        answer["x_custom"] = new JsonObject { ["supplierCustomerRef"] = "LS-2026-0917" };
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(answer.ToJsonString(), usage: new LlmUsage(100, 10));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.FailureReason.ShouldBeNull();
        document.ExtractionMethod.ShouldBe(ExtractionMethod.Llm);
        document.DocumentNumber.ShouldBe(TestPdfs.InvoiceNumber, "the invoice fields are read as always");
        document.CanonicalSchemaVersion.ShouldBe(DefinitionTree.SchemaVersion);
        var tree = JsonNode.Parse(document.CanonicalJson!)!.AsObject();
        tree["supplierCustomerRef"]!["value"]!["verbatim"]!.GetValue<string>().ShouldBe("LS-2026-0917");
        tree["supplierCustomerRef"]!["source"]!["method"]!.GetValue<string>().ShouldBe("Llm");

        var extract = host.Llm.Requests.Single(r => r.Stage == LlmStage.Extract);
        extract.OutputSchema!.Version.ShouldStartWith("1+x.");
        JsonNode.Parse(document.AppliedConfigJson!)!["llm"]!["schema"]!.GetValue<string>().ShouldBe($"invoice_extraction@{extract.OutputSchema.Version}");

        var index = await host.WithDbAsync(db => db.DocumentFieldIndex.Where(e => e.DocumentId == document.Id).OrderBy(e => e.FieldKey).Select(e => new { e.FieldKey, e.StringValue }).ToListAsync());
        index.Select(e => (e.FieldKey, e.StringValue)).ShouldBe([("invoiceNumber", TestPdfs.InvoiceNumber), ("supplierCustomerRef", "LS-2026-0917")]);

        // The coded invoice rules still ran on the tree (the fork keeps them): the document went through validation.
        (await host.EventsAsync(document.Id)).Select(e => e.ToStatus).ShouldContain(DocumentStatus.Validated);
    }

    [Fact]
    public async Task A_type_outside_the_invoice_family_is_never_auto_approved_even_when_its_definition_asks_for_it()
    {
        // docs/11 §11: until a type names its substitute for the totals in review.requirePassedRules (P5), only the
        // invoice family auto-approves; a purchase order nothing extracted stays in review whatever its definition says.
        await using var host = await StartAsync();
        await host.AddReplayProviderAsync();
        await SetConfigurableAsync(host, true);
        await ForkAsync(host, "std.purchase-order", json =>
        {
            json["processing"]!["review"]!["autoApprove"] = true;
            json["rules"] = JsonNode.Parse("""[{ "id": "r_conf", "kind": "confidence" }]""");
        });
        host.Llm.Enqueue(ClassifyPurchaseOrder);

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("bestellung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Type.ShouldBe(DocumentTypeKey.PurchaseOrder);
        document.CanonicalSchemaVersion.ShouldBe(DefinitionTree.SchemaVersion);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        document.ApprovedBy.ShouldBeNull();
        (await host.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.DocumentId == document.Id))).ShouldBe(0);
        var validated = await host.WithDbAsync(db => db.DocumentEvents.SingleAsync(e => e.DocumentId == document.Id && e.ToStatus == DocumentStatus.Validated));
        validated.DetailsJson.ShouldNotBeNull().ShouldContain("\"eligibleForAutoApproval\":false");
        (await host.WithDbAsync(db => db.ReviewTasks.SingleAsync(t => t.DocumentId == document.Id))).Reason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_forked_invoice_whose_required_custom_field_is_missing_is_not_auto_approved()
    {
        // std.invoice lists no validate.schema; a field the tenant marks required is still checked (docs/11 §11).
        await using var host = await StartAsync();
        await SetConfigurableAsync(host, true);
        await ForkAsync(host, "std.invoice", json => json["fields"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "fld_c_scr", "key": "supplierCustomerRef", "kind": "identifier", "required": true,
              "label": { "en": "Our customer no. at supplier" } }
            """)));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", "application/xml", PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.CanonicalSchemaVersion.ShouldBe(DefinitionTree.SchemaVersion);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        var flag = document.Flags.Single(f => f.Code == FlagCodes.FieldRequired);
        flag.Field.ShouldBe("supplierCustomerRef");
        flag.Severity.ShouldBe(FlagSeverity.Blocking);
        document.Flags.Where(f => f.Code == FlagCodes.FieldRequired).ShouldHaveSingleItem("the invoice's own required fields stay the coded rule's");
    }

    [Fact]
    public async Task An_adopted_invoice_sends_exactly_todays_request_even_with_configurable_types_on()
    {
        await using var host = await StartAsync();
        await host.AddReplayProviderAsync();
        await SetConfigurableAsync(host, true);
        host.Llm.Enqueue(ScriptedAnswers.ClassifyInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoice())]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        host.Llm.Requests.Single(r => r.Stage == LlmStage.Extract).OutputSchema.ShouldBe(Llm.Schema.CanonicalSchemas.InvoiceExtraction);
        document.CanonicalSchemaVersion.ShouldBe(1);
        JsonNode.Parse(document.AppliedConfigJson!)!["llm"]!["schema"]!.GetValue<string>().ShouldBe("invoice_extraction@1");
    }
}
