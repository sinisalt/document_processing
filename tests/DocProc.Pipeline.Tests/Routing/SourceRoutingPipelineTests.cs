using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Application.Routing;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Routing;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests.Routing;

/// <summary>
/// Source routing through the pipeline (docs/11 §7.2 to §7.4): the resolver at ingest, the Classify and Split skip of
/// a pinned type, Match as corroboration (confirm, conflict, replace), the unverified sender, ambiguity, the supplier's
/// review setting, and a tenant without <c>sourceRouting</c> that ingests as before.
/// </summary>
public sealed class SourceRoutingPipelineTests
{
    private const string XmlMime = "application/xml";
    private const string PdfMime = "application/pdf";
    private const string ContentVat = "DE812345678";
    private static readonly SenderAuthentication DmarcPass = new() { Dmarc = "pass", FromDomain = "nordwind.example" };

    [Fact]
    public async Task A_sender_rule_routes_the_document_at_ingest_and_matching_VAT_corroborates_it()
    {
        await using var host = await StartAsync();
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: ContentVat);
        var rule = await AddRuleAsync(host, supplier.Id, r => r.Sender = "rechnungsversand@nordwind.example");

        var ingestion = await IngestAsync(host, Xml(), new SourceMetadata(Sender: "Rechnung <rechnungsversand@nordwind.example>") { SenderAuth = DmarcPass, Mailbox = "invoices@tenant-a.local" });
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.SupplierId.ShouldBe(supplier.Id);
        document.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.SourceRule);
        document.SupplierSourceRuleId.ShouldBe(rule.Id);
        document.SupplierMatchConfidence.ShouldBe(SupplierSourceResolver.ExclusiveConfidence);
        document.Status.ShouldBe(DocumentStatus.AutoApproved);
        document.IngestionItem!.RouteJson.ShouldNotBeNull().ShouldContain(rule.Id.ToString());

        var events = await host.EventsAsync(document.Id);
        events.ShouldContain(e => e.EventType == RoutingEventTypes.SupplierIdentifiedBySource);
        events.Single(e => e.ToStatus == DocumentStatus.Matched).DetailsJson.ShouldNotBeNull().ShouldContain("\"corroborated\":true");
    }

    [Fact]
    public async Task Content_naming_another_supplier_keeps_an_Exclusive_supplier_and_blocks_with_SUPPLIER_CONFLICT()
    {
        await using var host = await StartAsync();
        var content = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: ContentVat);
        var routed = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Nordwind Großhandel GmbH");
        await AddRuleAsync(host, routed.Id, r => r.Recipient = "nordwind@tenant-a.local");

        var ingestion = await IngestAsync(host, Xml(), new SourceMetadata(Sender: "x@else.example") { Recipients = ["nordwind@tenant-a.local"] });
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.SupplierId.ShouldBe(routed.Id, "an Exclusive rule keeps its supplier");
        document.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.SourceRule);
        var conflict = document.Flags.Single(f => f.Code == FlagCodes.SupplierConflict);
        conflict.Severity.ShouldBe(FlagSeverity.Blocking);
        conflict.ToDomain().Params!["contentSupplierId"].ShouldBe(content.Id.ToString());
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
    }

    [Fact]
    public async Task VAT_evidence_replaces_a_default_supplier_and_undoes_the_type_its_rule_pinned()
    {
        // The keyword classifier decides the type again (no LLM is configured, so it is not asked).
        await using var host = await StartAsync(settings => settings["Pipeline:LlmClassificationThreshold"] = "0");
        var content = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Musterfirma GmbH", vatId: ContentVat);
        var routed = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Nordwind Großhandel GmbH");
        await AddRuleAsync(host, routed.Id, r =>
        {
            r.SenderDomain = "nordwind.example";
            r.Mode = SourceRuleMode.DefaultWhenNoContentMatch;
            r.PinTypeKey = "Statement";
        });

        var withVat = await IngestAsync(host, [new IngestFile("rechnung.pdf", PdfMime, TestPdfs.GermanInvoiceWithVat(ContentVat))], new SourceMetadata(Sender: "info@nordwind.example") { SenderAuth = DmarcPass });
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(withVat.Id);
        document.SupplierId.ShouldBe(content.Id, "VAT and IBAN win over a default");
        document.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.Vat);
        document.SupplierSourceRuleId.ShouldBeNull();
        document.Type.ShouldBe(DocumentTypeKey.Invoice, "the pinned Statement was the old supplier's and is classified again");
        document.TypeSource.ShouldBe(TypeSource.Rule);
        document.Flags.ShouldNotContain(f => f.Code == FlagCodes.SupplierConflict);
        (await host.EventsAsync(document.Id)).ShouldContain(e => e.EventType == RoutingEventTypes.SupplierReplacedByContent);
    }

    [Fact]
    public async Task A_pinned_type_skips_Split_and_Classify()
    {
        await using var host = await StartAsync();
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Nordwind Großhandel GmbH");
        await AddRuleAsync(host, supplier.Id, r =>
        {
            r.ChannelId = DevSeed.TenantAUploadChannel;
            r.FolderPrefix = "lieferanten/nordwind/";
            r.PinTypeKey = "Statement";
        });

        var ingestion = await IngestAsync(host, [new IngestFile("kontoauszug.pdf", PdfMime, TestPdfs.GermanInvoice())], new SourceMetadata { FolderPath = "lieferanten/nordwind/2026/" });
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Type.ShouldBe(DocumentTypeKey.Statement);
        document.TypeSource.ShouldBe(TypeSource.Supplier);
        document.PageCount.ShouldBe(2);
        var events = await host.EventsAsync(document.Id);
        events.Single(e => e.ToStatus == DocumentStatus.Split).DetailsJson.ShouldNotBeNull().ShouldContain("type pinned");
        events.Single(e => e.ToStatus == DocumentStatus.Classified).DetailsJson.ShouldNotBeNull().ShouldContain("\"kept\":true");
        (await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().CountAsync())).ShouldBe(0, "neither the splitter nor the classifier asked the LLM");
    }

    [Fact]
    public async Task An_unauthenticated_sender_skips_the_rule_raises_SUPPLIER_SOURCE_UNVERIFIED_and_never_auto_approves()
    {
        await using var host = await StartAsync();
        var content = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: ContentVat);
        var other = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Nordwind Großhandel GmbH");
        await AddRuleAsync(host, other.Id, r => r.Sender = "rechnungsversand@nordwind.example");

        var ingestion = await IngestAsync(host, Xml(), new SourceMetadata(Sender: "rechnungsversand@nordwind.example") { SenderAuth = new SenderAuthentication { Dmarc = "fail", FromDomain = "nordwind.example" } });
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.SupplierId.ShouldBe(content.Id, "the content match decides when no rule applies");
        document.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.Vat);
        document.Flags.ShouldContain(f => f.Code == FlagCodes.SupplierSourceUnverified && f.Severity == FlagSeverity.Warning);
        document.Status.ShouldBe(DocumentStatus.NeedsReview, "the e-invoice would auto-approve, but SUPPLIER_SOURCE_UNVERIFIED never does");
    }

    [Fact]
    public async Task Rules_of_two_suppliers_with_equal_standing_assign_nothing_and_raise_SUPPLIER_AMBIGUOUS()
    {
        await using var host = await StartAsync();
        var one = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Nordwind Großhandel GmbH");
        var two = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Südhof Feinkost");
        await AddRuleAsync(host, one.Id, r => r.Recipient = "a@tenant-a.local");
        await AddRuleAsync(host, two.Id, r => r.Recipient = "b@tenant-a.local");

        var ingestion = await IngestAsync(host, Xml(), new SourceMetadata(Sender: "x@else.example") { Recipients = ["a@tenant-a.local", "b@tenant-a.local"] });
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Flags.ShouldContain(f => f.Code == FlagCodes.SupplierAmbiguous);
        document.SupplierMatchMethod.ShouldNotBe(SupplierMatchMethod.SourceRule);
    }

    [Fact]
    public async Task A_supplier_with_review_mode_always_goes_to_review_and_its_single_allowed_type_is_pinned()
    {
        await using var host = await StartAsync();
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Fischhandel Müller GmbH", vatId: ContentVat);
        await host.WithDbAsync(async db =>
        {
            var row = await db.Suppliers.SingleAsync(s => s.Id == supplier.Id);
            row.SettingsJson = """{"allowedTypeKeys":["Invoice"],"review":{"mode":"always"}}""";
            await db.SaveChangesAsync();
        });
        await AddRuleAsync(host, supplier.Id, r => r.Recipient = "invoices@tenant-a.local");

        var ingestion = await IngestAsync(host, Xml(), new SourceMetadata { Mailbox = "invoices@tenant-a.local" });
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.SupplierId.ShouldBe(supplier.Id);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        (await host.EventsAsync(document.Id)).Single(e => e.EventType == RoutingEventTypes.SupplierIdentifiedBySource).DetailsJson.ShouldNotBeNull().ShouldContain("supplierSettings");
    }

    [Fact]
    public async Task A_split_part_inherits_its_items_route()
    {
        await using var host = await StartAsync(settings =>
        {
            settings["Pipeline:MaxParallelism"] = "1";
            settings["DocumentProcessing:Splitter:MaxConfidentPageCount"] = "1";
            settings["DocumentProcessing:Classifier:ConfidenceThreshold"] = "0";
            settings["Pipeline:LlmClassificationThreshold"] = "0";
        });
        await host.AddReplayProviderAsync();
        host.Llm.Enqueue("""{ "documents": [ { "startPage": 1, "endPage": 1 }, { "startPage": 2, "endPage": 2 } ] }""");
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Musterfirma GmbH");
        var rule = await AddRuleAsync(host, supplier.Id, r => r.Recipient = "invoices@tenant-a.local");

        var ingestion = await IngestAsync(host, [new IngestFile("scan.pdf", PdfMime, TestPdfs.GermanInvoice())], new SourceMetadata { Mailbox = "invoices@tenant-a.local" });
        await host.RunUntilIdleAsync();

        var documents = await host.WithDbAsync(db => db.Documents.AsNoTracking().Where(d => d.IngestionItem!.IngestionId == ingestion.Id).ToListAsync());
        documents.Count.ShouldBe(2, "the scan is split");
        documents.ShouldAllBe(d => d.SupplierId == supplier.Id && d.SupplierSourceRuleId == rule.Id);
    }

    [Fact]
    public async Task A_tenant_without_sourceRouting_ingests_as_before()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Nordwind Großhandel GmbH");
        await AddRuleAsync(host, supplier.Id, r => r.Recipient = "invoices@tenant-a.local");

        var ingestion = await IngestAsync(host, Xml(), new SourceMetadata { Mailbox = "invoices@tenant-a.local" });
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.SupplierMatchMethod.ShouldNotBe(SupplierMatchMethod.SourceRule);
        document.SupplierId.ShouldNotBe(supplier.Id);
        document.IngestionItem!.RouteJson.ShouldBeNull();
        document.Status.ShouldBe(DocumentStatus.AutoApproved);
    }

    internal static async Task<PipelineTestHost> StartAsync(Action<Dictionary<string, string?>>? configure = null)
    {
        var host = await PipelineTestHost.StartAsync(configure);
        await EnableRoutingAsync(host, PipelineTestHost.TenantA);
        return host;
    }

    internal static Task EnableRoutingAsync(PipelineTestHost host, Guid tenantId) => host.WithDbAsync(async db =>
    {
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.Settings.Features!.SourceRouting = true;
        await db.SaveChangesAsync();
    });

    internal static Task<SupplierSourceRule> AddRuleAsync(PipelineTestHost host, Guid supplierId, Action<SupplierSourceRule> conditions, Guid? tenantId = null) => host.WithDbAsync(async db =>
    {
        var rule = new SupplierSourceRule
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId ?? PipelineTestHost.TenantA,
            SupplierId = supplierId,
            ConditionHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            CreatedBy = "test",
            CreatedAt = host.Clock.GetUtcNow(),
            UpdatedAt = host.Clock.GetUtcNow(),
        };
        conditions(rule);
        db.SupplierSourceRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    });

    internal static async Task<Ingestion> IngestAsync(PipelineTestHost host, IReadOnlyList<IngestFile> files, SourceMetadata source, Guid? tenantId = null)
    {
        var tenant = tenantId ?? PipelineTestHost.TenantA;
        var blobs = host.Services.GetRequiredService<IBlobStore>();
        var stored = await UploadIntake.StoreAsync(blobs, tenant, [.. files.Select(f => new UploadFile(f.FileName, f.MimeType, ItemKind.Other, () => new MemoryStream(f.Bytes, writable: false)))], CancellationToken.None);
        var ingestion = UploadIntake.CreateIngestion(tenant, tenant == PipelineTestHost.TenantA ? DevSeed.TenantAUploadChannel : DevSeed.TenantBUploadChannel, source, stored, host.Clock.GetUtcNow());
        await host.WithDbAsync(async db =>
        {
            db.Ingestions.Add(ingestion);
            await db.SaveChangesAsync();
        });
        await host.PipelineQueue.EnqueueAsync(new ProcessIngestion(tenant, ingestion.Id), CancellationToken.None);
        return ingestion;
    }

    private static IReadOnlyList<IngestFile> Xml() => [new IngestFile("rechnung.xml", XmlMime, PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))];
}
