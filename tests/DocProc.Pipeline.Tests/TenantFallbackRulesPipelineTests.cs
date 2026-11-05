using DocProc.Domain.Documents;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Pipeline.Layouts;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The tenant fallback identification rules in the pipeline (docs/11 §8.5, §9.4 step 2; P5): an Active tenant rule types
/// a document from an unknown sender (<c>TypeSource=Identification</c>, <c>IdentificationRuleVersionId</c> pinned, the
/// rule's <c>Matched</c> counted) without asking the keyword rules or the LLM; rules of two types that both hold decide
/// nothing; a supplier the rule excludes is not typed by it; a Draft or a disabled rule does not run; the split
/// prefilter keeps a multi-page price list whole when a tenant price-list rule holds on it. Without
/// <c>supplierLayouts</c> the rules do not run.
/// </summary>
public sealed class TenantFallbackRulesPipelineTests
{
    private const string PdfMime = "application/pdf";

    private static readonly byte[] PriceListPdf = TestPdfs.TextDocument("Preisliste", "gültig ab 01.12.2026", "Artikel Bezeichnung Preis", "4711 Bergkäse 18,90");

    private static async Task<PipelineTestHost> StartAsync(bool supplierLayouts = true)
    {
        var host = await PipelineTestHost.StartAsync();
        await host.WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA);
            tenant.Settings.Features!.ConfigurableTypes = true;
            tenant.Settings.Features.SupplierLayouts = supplierLayouts;
            await db.SaveChangesAsync();
        });
        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var adopted = await scope.ServiceProvider.GetRequiredService<DocumentTypeCatalogue>().AdoptAsync(PipelineTestHost.TenantA, "std.price-list", null, "admin", CancellationToken.None);
        adopted.Status.ShouldBe(CatalogueStatus.Created, adopted.Detail);
        await host.AddReplayProviderAsync();
        return host;
    }

    private static Task<TypeIdentificationRuleVersion> AddRuleAsync(PipelineTestHost host, string type, string ruleJson, TypeIdentificationRuleVersionState state = TypeIdentificationRuleVersionState.Active, IReadOnlyList<Guid>? excluded = null, string name = "Preisliste im Titel") =>
        host.WithDbAsync(async db =>
        {
            var parsed = IdentificationRule.Parse(ruleJson);
            var rule = new TypeIdentificationRule { Id = Guid.CreateVersion7(), TenantId = PipelineTestHost.TenantA, DocumentTypeKey = type, Name = name, ActiveVersion = state == TypeIdentificationRuleVersionState.Active ? 1 : null, CreatedAt = PipelineTestHost.Now, UpdatedAt = PipelineTestHost.Now };
            var version = new TypeIdentificationRuleVersion { Id = Guid.CreateVersion7(), TenantId = PipelineTestHost.TenantA, Version = 1, State = state, RuleJson = parsed.ToJson(), RuleHash = parsed.Hash, ExcludedSupplierIdsJson = System.Text.Json.JsonSerializer.Serialize(excluded ?? []), CreatedAt = PipelineTestHost.Now };
            rule.Versions.Add(version);
            db.TypeIdentificationRules.Add(rule);
            await db.SaveChangesAsync();
            return version;
        });

    private const string PriceListRule = """{ "all": [ { "text": "Preisliste", "page": 1, "region": "title" } ] }""";

    private static async Task<Document> RunAsync(PipelineTestHost host, byte[] pdf, string file = "liste.pdf")
    {
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile(file, PdfMime, pdf)], from: "unbekannt@example.test");
        await host.RunUntilIdleAsync();
        return await host.SingleDocumentAsync(ingestion.Id);
    }

    [Fact]
    public async Task A_tenant_rule_types_a_document_from_an_unknown_sender_without_keywords_or_the_llm()
    {
        await using var host = await StartAsync();
        var version = await AddRuleAsync(host, "price-list", PriceListRule);

        var document = await RunAsync(host, PriceListPdf);

        document.Type.Value.ShouldBe("price-list");
        document.TypeSource.ShouldBe(TypeSource.Identification);
        document.TypeConfidence.ShouldBe(1.0);
        document.IdentificationRuleVersionId.ShouldBe(version.Id);
        document.SupplierLayoutVersionId.ShouldBeNull();
        host.Llm.Requests.ShouldBeEmpty("the rule decided: neither the keyword rules' LLM fallback nor anything else was asked");
        var events = await host.EventsAsync(document.Id);
        var identified = events.Single(e => e.EventType == TenantIdentification.IdentifiedEvent);
        identified.DetailsJson.ShouldNotBeNull().ShouldContain("\"resolution\":\"identified\"");
        identified.DetailsJson.ShouldContain(version.Id.ToString());
        identified.DetailsJson.ShouldContain("Preisliste");
        (await host.WithDbAsync(db => db.TypeIdentificationRules.SingleAsync(r => r.Id == version.RuleId))).Matched.ShouldBe(1);
    }

    [Fact]
    public async Task A_document_classified_again_counts_once_and_takes_its_count_back_when_the_rule_no_longer_decides()
    {
        await using var host = await StartAsync();
        var version = await AddRuleAsync(host, "price-list", PriceListRule);
        var document = await RunAsync(host, PriceListPdf);
        Task<int> MatchedAsync() => host.WithDbAsync(async db => (await db.TypeIdentificationRules.AsNoTracking().SingleAsync(r => r.Id == version.RuleId)).Matched);
        (await MatchedAsync()).ShouldBe(1);

        // A reprocess from Classify: the rule decides again, and the document still counts once.
        await ReclassifyAsync(host, document.Id);
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id))).IdentificationRuleVersionId.ShouldBe(version.Id);
        (await MatchedAsync()).ShouldBe(1, "classified again by the same rule: its count is taken back and counted once more");

        // The rule is disabled: classified again, the document is no longer the rule's, and its count is taken back.
        await host.WithDbAsync(async db =>
        {
            (await db.TypeIdentificationRules.SingleAsync(r => r.Id == version.RuleId)).Status = TypeIdentificationRuleStatus.Disabled;
            await db.SaveChangesAsync();
        });
        host.Llm.Enqueue("""{ "documentType": "Other", "confidence": 0.5, "language": "de", "supplierNameHint": null }""");
        await ReclassifyAsync(host, document.Id);
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id))).IdentificationRuleVersionId.ShouldBeNull();
        (await MatchedAsync()).ShouldBe(0);
    }

    /// <summary>What <c>POST /documents/{id}:reprocess {fromStage: Classify}</c> does: back to Split, then the Classify stage again.</summary>
    private static async Task ReclassifyAsync(PipelineTestHost host, Guid documentId)
    {
        await host.WithDbAsync(async db =>
        {
            var row = await db.Documents.SingleAsync(d => d.Id == documentId);
            row.Status = DocumentStatus.Split;
            row.FailureReason = null;
            await db.SaveChangesAsync();
        });
        await host.PipelineQueue.EnqueueAsync(new DocProc.Application.Pipeline.ProcessDocument(PipelineTestHost.TenantA, documentId, DocProc.Application.Pipeline.PipelineStage.Classify), CancellationToken.None);
        await host.RunUntilIdleAsync();
    }

    [Fact]
    public async Task Rules_of_two_types_that_both_hold_decide_nothing()
    {
        await using var host = await StartAsync();
        await AddRuleAsync(host, "price-list", PriceListRule);
        await AddRuleAsync(host, "daily-specials", """{ "all": [ { "text": "Bergkäse" } ] }""", name: "Käse");

        host.Llm.Enqueue("""{ "documentType": "Other", "confidence": 0.5, "language": "de", "supplierNameHint": null }""");
        var document = await RunAsync(host, PriceListPdf);

        document.FailureReason.ShouldBeNull();
        document.TypeSource.ShouldNotBe(TypeSource.Identification);
        document.IdentificationRuleVersionId.ShouldBeNull();
        (await host.EventsAsync(document.Id)).Single(e => e.EventType == TenantIdentification.IdentifiedEvent).DetailsJson!.ShouldContain("\"resolution\":\"conflicting\"");
    }

    [Fact]
    public async Task Draft_disabled_and_excluded_rules_do_not_type_the_document()
    {
        await using var host = await StartAsync();
        await AddRuleAsync(host, "price-list", PriceListRule, state: TypeIdentificationRuleVersionState.Draft);
        var disabled = await AddRuleAsync(host, "price-list", PriceListRule, name: "Abgeschaltet");
        await host.WithDbAsync(async db =>
        {
            var rule = await db.TypeIdentificationRules.SingleAsync(r => r.Id == disabled.RuleId);
            rule.Status = TypeIdentificationRuleStatus.Disabled;
            await db.SaveChangesAsync();
        });

        host.Llm.Enqueue("""{ "documentType": "Other", "confidence": 0.5, "language": "de", "supplierNameHint": null }""");
        var document = await RunAsync(host, PriceListPdf);
        document.FailureReason.ShouldBeNull();
        document.IdentificationRuleVersionId.ShouldBeNull();
        document.TypeSource.ShouldNotBe(TypeSource.Identification);

        // A supplier the rule excludes: evaluated without it (the set's own test).
        var excludedSupplier = Guid.NewGuid();
        var rules = new[] { new TenantRuleCandidate(Guid.NewGuid(), Guid.NewGuid(), 1, "r", "price-list", 100, IdentificationRule.Parse(PriceListRule), [excludedSupplier]) };
        var words = DocumentWords.From(new DocProc.Domain.Text.TextLayer(DocProc.Domain.Text.TextLayerSource.PdfText, [new DocProc.Domain.Text.TextPage(1, 1000, 1000, [new DocProc.Domain.Text.PositionedWord("Preisliste", new DocProc.Domain.Fields.BoundingBox(60, 100, 200, 120))])]));
        TypeIdentificationRuleSet.Resolve(rules, words, null, excludedSupplier).Resolution.ShouldBe(TenantRuleResolution.None);
        TypeIdentificationRuleSet.Resolve(rules, words, null, Guid.NewGuid()).Resolution.ShouldBe(TenantRuleResolution.Identified);
        TypeIdentificationRuleSet.Resolve(rules, words, null, null).Resolution.ShouldBe(TenantRuleResolution.Identified);
    }

    [Fact]
    public async Task The_split_prefilter_keeps_a_multi_page_price_list_whole_when_a_tenant_rule_holds()
    {
        await using var host = await StartAsync();
        await AddRuleAsync(host, "price-list", PriceListRule);
        var pdf = TestPdfs.TextPages(
            ["Preisliste", "gültig ab 01.12.2026", "4711 Bergkäse 18,90"],
            ["Preisliste", "Seite 2", "4712 Weichkäse 12,50"],
            ["Preisliste", "Seite 3", "4713 Frischkäse 4,20"]);

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("liste.pdf", PdfMime, pdf)], from: "unbekannt@example.test");
        await host.RunUntilIdleAsync();

        var documents = await host.WithDbAsync(db => db.Documents.AsNoTracking().Where(d => d.IngestionItem!.IngestionId == ingestion.Id).ToListAsync());
        var document = documents.ShouldHaveSingleItem("the price list is never split");
        document.PageCount.ShouldBe(3);
        document.Type.Value.ShouldBe("price-list");
        (await host.EventsAsync(document.Id)).ShouldContain(e => e.ToStatus == DocumentStatus.Split && e.DetailsJson!.Contains("split prefilter: tenant rule") && e.DetailsJson.Contains("Preisliste im Titel"));
    }

    [Fact]
    public async Task Without_supplier_layouts_the_tenant_rules_do_not_run()
    {
        await using var host = await StartAsync(supplierLayouts: false);
        await AddRuleAsync(host, "price-list", PriceListRule);
        host.Llm.Enqueue("""{ "documentType": "Other", "confidence": 0.5, "language": "de", "supplierNameHint": null }""");

        var document = await RunAsync(host, PriceListPdf);

        document.FailureReason.ShouldBeNull();
        document.IdentificationRuleVersionId.ShouldBeNull();
        document.TypeSource.ShouldNotBe(TypeSource.Identification);
    }
}
