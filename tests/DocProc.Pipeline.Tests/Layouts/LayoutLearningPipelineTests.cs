using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Routing;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Mapping.Learning;
using DocProc.Mapping.Values;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Layouts;
using DocProc.Pipeline.Review;
using DocProc.Pipeline.Tests.Demo;
using DocProc.Pipeline.Tests.Pdf;
using DocProc.Pipeline.Tests.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests.Layouts;

/// <summary>
/// Learning layouts in review (docs/11 §8.3, §12.6, §12.10) on the committed Nordwind invoices, the P4 demo as a pipeline
/// test: a web-shop invoice held with <c>NEW_LAYOUT</c> is pre-filled (<c>layout-prefill</c>: master data, no old
/// positions), the reviewer's confirmed values are saved as <c>nordwind-invoice-webshop</c> by a <c>MappingAuthor</c>
/// (proposed rule, AnchorLearner mapping, identification test and field replay, Active with the default warm-up of 5),
/// the next web-shop invoice is identified and filled by the learned layout and goes to review as warm-up 1 of 5, the
/// tenant's warm-up is set to 0 and the following one auto-approves; ERP invoices keep their layout. The Candidate path
/// of a reviewer who is not a <c>MappingAuthor</c>: the next document waits for the pending layout, is pre-filled from
/// it, and the <c>MappingAuthor</c>s are notified; activation offers the waiting documents for reprocessing.
/// </summary>
public sealed class LayoutLearningPipelineTests
{
    private const string PdfMime = "application/pdf";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Sender = "rechnung@nordwind.example";
    private static readonly SenderAuthentication DmarcPass = new() { Dmarc = "pass", FromDomain = "nordwind.example" };

    private static async Task<(PipelineTestHost Host, Supplier Nordwind)> StartAsync()
    {
        var host = await PipelineTestHost.StartAsync(settings => settings["Pipeline:LlmClassificationThreshold"] = "0");
        await host.WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA, Ct);
            tenant.Settings.Features!.SourceRouting = true;
            tenant.Settings.Features!.SupplierLayouts = true;
            await db.SaveChangesAsync(Ct);
        });
        var nordwind = await host.AddSupplierAsync(PipelineTestHost.TenantA, DemoNordwindInvoices.SupplierName, vatId: DemoNordwindInvoices.SupplierVatId);
        await SourceRoutingPipelineTests.AddRuleAsync(host, nordwind.Id, r => r.Sender = Sender);
        return (host, nordwind);
    }

    private static async Task<Document> ProcessAsync(PipelineTestHost host, DemoNordwindInvoices.Invoice invoice)
    {
        var bytes = await File.ReadAllBytesAsync(DemoFixtureTests.NordwindInvoicePath(invoice), Ct);
        var ingestion = await SourceRoutingPipelineTests.IngestAsync(host, [new IngestFile(invoice.FileName, PdfMime, bytes)], new SourceMetadata(Sender: Sender) { SenderAuth = DmarcPass });
        await host.RunUntilIdleAsync();
        return await host.SingleDocumentAsync(ingestion.Id);
    }

    private static JsonNode? Value(JsonObject tree, string key) => tree[key]?["value"];

    /// <summary>
    /// The reviewer confirms every value of the web-shop invoice on the page: the tree a reviewer's corrections with
    /// their places leave (each value Human with the box it was taken from), here written from the demo layout's mapping.
    /// </summary>
    private static async Task ConfirmValuesAsync(PipelineTestHost host, Guid documentId)
    {
        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var db = scope.ServiceProvider.GetRequiredService<DocProcDbContext>();
        var configs = scope.ServiceProvider.GetRequiredService<IEffectiveConfigResolver>();
        var document = await db.Documents.Include(d => d.IngestionItem).SingleAsync(d => d.Id == documentId, Ct);
        var type = (await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, document.Type, document.SupplierId))).Type!;
        var words = DocumentWords.From(await PageSources.ReadTextLayerAsync(scope.ServiceProvider.GetRequiredService<IContainerBlobStore>(), document, Ct));
        var descriptors = type.Compiled!.Descriptors;
        var extraction = LayoutEngine.Run(LayoutMapping.Parse(NordwindLayouts.WebShopMapping()), words, "review@0", p => LayoutTreeWriter.KindOf(descriptors, p), p => LayoutTreeWriter.RowFieldsOf(descriptors, p), ValueLocale.German);
        var (tree, _) = LayoutTreeWriter.Build(descriptors, extraction, [], new Dictionary<string, string>(), "EUR", typedRecord: true, document.Type);
        document.CanonicalJson = new ExtractedDocument(tree).ToJson();
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<LearnLayoutResult> SaveAsync(PipelineTestHost host, Guid documentId, bool mappingAuthor, Action<Document>? approve = null)
    {
        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<DocProcDbContext>();
        var learning = new LayoutLearning(db, services.GetRequiredService<IContainerBlobStore>(), services.GetRequiredService<IEffectiveConfigResolver>(), host.Clock);
        var document = await db.Documents.Include(d => d.Flags).Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).SingleAsync(d => d.Id == documentId, Ct);
        approve?.Invoke(document);
        var result = await new LayoutLearningSave(learning, db, host.Clock).SaveAsync(document, new LearnLayoutCommand(LearnSaveAs.NewLayout, LayoutKey: NordwindLayouts.WebShopKey, MayActivate: mappingAuthor), "tenant-a-reviewer", Ct);
        if (result.Saved)
        {
            await db.SaveChangesAsync(Ct);
        }

        return result;
    }

    [Fact]
    public async Task A_new_design_is_prefilled_learned_by_a_MappingAuthor_and_the_next_documents_warm_up_then_auto_approve()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        var (_, erp) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping());
        foreach (var invoice in DemoNordwindInvoices.Erp.Take(2))
        {
            (await ProcessAsync(host, invoice)).Status.ShouldBe(DocumentStatus.AutoApproved);
        }

        // 1. A web-shop invoice: NEW_LAYOUT, pre-filled from master data (no LLM configured), never by the ERP layout.
        var first = await ProcessAsync(host, DemoNordwindInvoices.WebShop[0]);
        first.Flags.ShouldContain(f => f.Code == FlagCodes.NewLayout && f.ResolvedAt == null);
        first.SupplierLayoutVersionId.ShouldBeNull();
        first.Status.ShouldBe(DocumentStatus.NeedsReview, "pre-filled, it waits for a reviewer, not for an LLM provider");
        var prefilled = JsonNode.Parse(first.CanonicalJson.ShouldNotBeNull())!.AsObject();
        Value(prefilled, "supplier")!["vatId"]!.GetValue<string>().Replace(" ", string.Empty, StringComparison.Ordinal).ShouldBe(DemoNordwindInvoices.SupplierVatId);
        prefilled["supplier"]!["status"]!.GetValue<string>().ShouldBe("Inferred");
        var applied = JsonNode.Parse(first.AppliedConfigJson!)!;
        applied["prefill"]!["sources"]!["supplier.vatId"]!.GetValue<string>().ShouldBeOneOf(PrefillSources.MasterData, PrefillSources.OtherLayout);
        applied["prefill"]!["sources"]!["supplier.name"]!.GetValue<string>().ShouldBeOneOf(PrefillSources.MasterData, PrefillSources.OtherLayout);
        applied["layout"].ShouldBeNull("no layout filled it");

        // 2. The reviewer confirms the values and saves the design as a layout; a MappingAuthor's save is Active at once.
        await ConfirmValuesAsync(host, first.Id);
        var saved = await SaveAsync(host, first.Id, mappingAuthor: true);
        saved.Code.ShouldBeNull(saved.Problem);
        saved.Draft!.Skipped.ShouldBeEmpty(string.Join(" | ", saved.Draft.Skipped.Select(x => x.Path + ": " + x.Reason)) + " FIELDS " + string.Join(",", saved.Draft.Fields.Select(f => f.Path)));
        saved.Draft!.RuleProposed.ShouldBeTrue();
        saved.Draft.Rule.Describe().ShouldContain("RECHNUNG / INVOICE");
        saved.Draft.Fields.Single(f => f.Path == "invoiceNumber").Strategies[0].ShouldBe(LayoutStrategies.LabelRight);
        saved.Draft.Fields.Single(f => f.Path == "lines").Verified.ShouldBeTrue();
        saved.Activation!.Activated.ShouldBeTrue(saved.Activation.Refusal);
        saved.Activation.Replay.IdentificationPassed.ShouldBeTrue("the rule holds on its document and on none of the ERP documents");
        saved.Activation.Replay.Documents.Select(d => d.DocumentId).ShouldBe([first.Id], "the replay set of a NEW_LAYOUT version 1 is its own document");
        saved.Activation.Replay.Fields.ShouldAllBe(f => f.Agreement == 1.0);
        var version = await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().Include(v => v.Layout).SingleAsync(v => v.Id == saved.Version!.Id, Ct));
        version.Status.ShouldBe(SupplierLayoutVersionStatus.Active);
        version.Origin.ShouldBe(SupplierLayoutOrigin.NewLayoutReview);
        version.ReviewWarmup.ShouldBe(TenantSettings.DefaultLayoutReviewWarmup, "decision 2: warm-up 5 by default, also for a NEW_LAYOUT version 1");
        version.ReplayJson.ShouldNotBeNull().ShouldContain("identificationPassed");
        version.Layout!.ActiveVersion.ShouldBe(1);
        (await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == erp.Id, Ct))).Status.ShouldBe(SupplierLayoutVersionStatus.Active, "the old design's layout stays Active");

        // 3. The next web-shop invoice is identified and filled by the learned layout, and goes to review as warm-up 1 of 5.
        var second = await ProcessAsync(host, DemoNordwindInvoices.WebShop[1]);
        second.SupplierLayoutVersionId.ShouldBe(version.Id);
        second.TypeSource.ShouldBe(TypeSource.Identification);
        second.ExtractionMethod.ShouldBe(ExtractionMethod.Profile);
        var tree = JsonNode.Parse(second.CanonicalJson!)!.AsObject();
        Value(tree, "invoiceNumber")!["verbatim"]!.GetValue<string>().ShouldBe(DemoNordwindInvoices.WebShop[1].Number);
        Value(tree, "totalGross")!["amount"]!.GetValue<decimal>().ShouldBe(DemoNordwindInvoices.WebShop[1].Gross);
        tree["lines"]!.AsArray().Count.ShouldBe(DemoNordwindInvoices.WebShop[1].Lines.Count);
        second.Status.ShouldBe(DocumentStatus.NeedsReview);
        var warmup = second.Flags.Single(f => f.Code == FlagCodes.LayoutWarmup).ToDomain().Params!;
        (warmup["ordinal"], warmup["warmup"]).ShouldBe(("1", "5"));

        // 4. The demo tenant's warm-up is set to 0: the version's warm-up from the tenant policy follows, the next auto-approves.
        await host.WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA, Ct);
            await LayoutWarmups.ApplyTenantWarmupAsync(db, PipelineTestHost.TenantA, tenant.Settings.EffectiveLayoutReviewWarmup(), 0, Ct);
            tenant.Settings.LayoutReviewWarmup = 0;
            await db.SaveChangesAsync(Ct);
        });
        var third = await ProcessAsync(host, DemoNordwindInvoices.WebShop[2]);
        third.SupplierLayoutVersionId.ShouldBe(version.Id);
        third.Status.ShouldBe(DocumentStatus.AutoApproved, string.Join(" | ", third.Flags.Where(f => f.ResolvedAt == null).Select(f => f.Code + ":" + f.Message)) + " TASK " + (await host.WithDbAsync(db => db.ReviewTasks.AsNoTracking().Where(t => t.DocumentId == third.Id).Select(t => t.Reason).FirstOrDefaultAsync(Ct))));
        (await ProcessAsync(host, DemoNordwindInvoices.Erp[2])).Status.ShouldBe(DocumentStatus.AutoApproved, "invoices in the old design still are");
    }

    [Fact]
    public async Task A_reviewers_save_is_a_Candidate_the_next_document_waits_for_it_prefilled_and_activation_offers_it_for_reprocessing()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping());
        var first = await ProcessAsync(host, DemoNordwindInvoices.WebShop[0]);
        await ConfirmValuesAsync(host, first.Id);

        var saved = await SaveAsync(host, first.Id, mappingAuthor: false);
        saved.Code.ShouldBeNull(saved.Problem);
        saved.Activation.ShouldBeNull("a reviewer without MappingAuthor proposes");
        saved.Version!.Status.ShouldBe(SupplierLayoutVersionStatus.Candidate);

        var second = await ProcessAsync(host, DemoNordwindInvoices.WebShop[1]);
        second.SupplierLayoutVersionId.ShouldBeNull("a Candidate is never applied");
        second.PendingLayoutVersionId.ShouldBe(saved.Version.Id);
        var flag = second.Flags.Single(f => f.Code == FlagCodes.NewLayout).ToDomain();
        flag.Params!["pendingLayoutVersionId"].ShouldBe(saved.Version.Id.ToString());
        var tree = JsonNode.Parse(second.CanonicalJson!)!.AsObject();
        Value(tree, "invoiceNumber").ShouldNotBeNull(second.AppliedConfigJson + " " + saved.Version.MappingJson);
        Value(tree, "invoiceNumber")!["verbatim"]!.GetValue<string>().ShouldBe(DemoNordwindInvoices.WebShop[1].Number, "pre-filled from the pending layout as a basis");
        tree["invoiceNumber"]!["status"]!.GetValue<string>().ShouldBe("Inferred");
        JsonNode.Parse(second.AppliedConfigJson!)!["prefill"]!["sources"]!["invoiceNumber"]!.GetValue<string>().ShouldBe(PrefillSources.PendingLayout);

        var notification = await host.WithDbAsync(db => db.Notifications.AsNoTracking().SingleAsync(Ct));
        notification.Audience.ShouldBe(LayoutLearning.NotificationAudience);
        notification.Count.ShouldBe(2, "the saved document and the one waiting");
        notification.ResolvedAt.ShouldBeNull();

        using (var scope = host.CreateTenantScope(PipelineTestHost.TenantA))
        {
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<DocProcDbContext>();
            var learning = new LayoutLearning(db, services.GetRequiredService<IContainerBlobStore>(), services.GetRequiredService<IEffectiveConfigResolver>(), host.Clock);
            var layout = await db.SupplierLayouts.Include(l => l.Versions).SingleAsync(l => l.LayoutKey == NordwindLayouts.WebShopKey, Ct);
            var outcome = await learning.ActivateAsync(layout, layout.Versions.Single(), "tenant-a-admin", null, Ct);
            outcome.Activated.ShouldBeTrue(outcome.Refusal);
            outcome.Waiting.ShouldContain(second.Id);
            await db.SaveChangesAsync(Ct);
        }

        (await host.WithDbAsync(db => db.Notifications.AsNoTracking().SingleAsync(Ct))).ResolvedAt.ShouldNotBeNull("activation resolves the notification");
    }

    [Fact]
    public async Task A_rule_that_does_not_hold_on_its_document_or_overlaps_another_layout_is_refused_or_not_activated()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping());
        var erpDocument = await ProcessAsync(host, DemoNordwindInvoices.Erp[0]);
        erpDocument.Status.ShouldBe(DocumentStatus.AutoApproved);
        var first = await ProcessAsync(host, DemoNordwindInvoices.WebShop[0]);
        await ConfirmValuesAsync(host, first.Id);

        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<DocProcDbContext>();
        var learning = new LayoutLearning(db, services.GetRequiredService<IContainerBlobStore>(), services.GetRequiredService<IEffectiveConfigResolver>(), host.Clock);
        var save = new LayoutLearningSave(learning, db, host.Clock);
        var document = await db.Documents.Include(d => d.Flags).Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).SingleAsync(d => d.Id == first.Id, Ct);

        var notHolding = await save.SaveAsync(document, new LearnLayoutCommand(LearnSaveAs.NewLayout, IdentificationJson: NordwindLayouts.ErpRule, MayActivate: true), "a", Ct);
        notHolding.Code.ShouldBe(LearnCodes.RuleDoesNotHold);
        notHolding.Failed.ShouldNotBeEmpty();

        // "Nordwind" alone holds on both designs: saved, but the identification test keeps it a Candidate.
        var overlapping = await save.SaveAsync(document, new LearnLayoutCommand(LearnSaveAs.NewLayout, LayoutKey: "nordwind-anything", IdentificationJson: """{ "all": [ { "text": "Nordwind" } ] }""", MayActivate: true), "a", Ct);
        overlapping.Code.ShouldBeNull(overlapping.Problem);
        overlapping.Activation!.Activated.ShouldBeFalse();
        overlapping.Activation.Replay.Overlaps.Select(o => o.DocumentId).ShouldContain(erpDocument.Id);
        overlapping.Version!.Status.ShouldBe(SupplierLayoutVersionStatus.Candidate);
    }
}
