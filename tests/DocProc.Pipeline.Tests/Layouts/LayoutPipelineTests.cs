using System.Text.Json.Nodes;
using DocProc.Application.Pipeline;
using DocProc.Application.Routing;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Layouts;
using DocProc.Pipeline.Support;
using DocProc.Pipeline.Tests.Demo;
using DocProc.Pipeline.Tests.Pdf;
using DocProc.Pipeline.Tests.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests.Layouts;

/// <summary>
/// Supplier layouts through the pipeline (docs/11 §8, §9.4, §11) on the committed Nordwind demo invoices: a routed
/// invoice identified by its layout's rule (<c>classify.identification</c>) and filled by its field mapping and
/// <c>column-table</c> (the golden outputs of all six invoices), the web-shop design held with <c>NEW_LAYOUT</c> instead of
/// processed with the old layout, the review warm-up, ambiguity, a pending Candidate, <c>match.layout</c> for a supplier
/// found by content, drift and degradation, and a tenant without <c>supplierLayouts</c> that behaves as before.
/// </summary>
public sealed class LayoutPipelineTests
{
    private const string PdfMime = "application/pdf";
    private const string Sender = "rechnung@nordwind.example";
    private static readonly SenderAuthentication DmarcPass = new() { Dmarc = "pass", FromDomain = "nordwind.example" };

    private static async Task<(PipelineTestHost Host, Supplier Nordwind)> StartAsync(bool layouts = true, bool routed = true, Action<IServiceCollection>? services = null)
    {
        // No LLM is configured, so classification is the keyword rules' alone.
        var host = await PipelineTestHost.StartAsync(settings => settings["Pipeline:LlmClassificationThreshold"] = "0", services);
        await host.WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA);
            tenant.Settings.Features!.SourceRouting = true;
            tenant.Settings.Features!.SupplierLayouts = layouts;
            await db.SaveChangesAsync();
        });
        var nordwind = await host.AddSupplierAsync(PipelineTestHost.TenantA, DemoNordwindInvoices.SupplierName, vatId: DemoNordwindInvoices.SupplierVatId);
        if (routed)
        {
            await SourceRoutingPipelineTests.AddRuleAsync(host, nordwind.Id, r => r.Sender = Sender);
        }

        return (host, nordwind);
    }

    private static async Task<Document> ProcessAsync(PipelineTestHost host, DemoNordwindInvoices.Invoice invoice, bool routed = true, byte[]? redrawn = null)
    {
        var bytes = redrawn ?? await File.ReadAllBytesAsync(DemoFixtureTests.NordwindInvoicePath(invoice), TestContext.Current.CancellationToken);
        var source = routed ? new SourceMetadata(Sender: Sender) { SenderAuth = DmarcPass } : new SourceMetadata(Sender: "buchhaltung@else.example");
        var ingestion = await SourceRoutingPipelineTests.IngestAsync(host, [new IngestFile(invoice.FileName, PdfMime, bytes)], source);
        await host.RunUntilIdleAsync();
        return await host.SingleDocumentAsync(ingestion.Id);
    }

    /// <summary>
    /// A document no layout fills goes to the LLM as before: the scripted provider answers the extraction (its values
    /// are the stock answer, not this invoice's), so the document reaches review instead of waiting for a provider.
    /// </summary>
    private static async Task ScriptExtractionAsync(PipelineTestHost host, int answers = 1)
    {
        if (await host.WithDbAsync(db => db.LlmProviderConfigs.CountAsync()) >= 0)
        {
            await host.AddReplayProviderAsync();
        }

        for (var i = 0; i < answers; i++)
        {
            host.Llm.Enqueue(ScriptedAnswers.ExtractGermanInvoice);
        }
    }

    private static JsonObject Tree(Document document) => JsonNode.Parse(document.CanonicalJson.ShouldNotBeNull())!.AsObject();

    private static JsonNode? Value(JsonObject tree, string key) => tree[key]?["value"];

    /// <summary>The golden output of a layout: every header value, every line and the totals as printed on the demo invoice.</summary>
    private static void ShouldHoldTheInvoice(Document document, DemoNordwindInvoices.Invoice invoice)
    {
        var tree = Tree(document);
        Value(tree, "invoiceNumber")!["verbatim"]!.GetValue<string>().ShouldBe(invoice.Number);
        Value(tree, "issueDate")!.GetValue<string>().ShouldBe(invoice.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Value(tree, "currency")!.GetValue<string>().ShouldBe("EUR");
        Value(tree, "supplier")!["name"]!.GetValue<string>().ShouldBe(DemoNordwindInvoices.SupplierName);
        Value(tree, "supplier")!["vatId"]!.GetValue<string>().ShouldBe(DemoNordwindInvoices.SupplierVatId);
        Value(tree, "customer")!["name"]!.GetValue<string>().ShouldBe(DemoNordwindInvoices.CustomerName);
        Value(tree, "subtotalNet")!["amount"]!.GetValue<decimal>().ShouldBe(invoice.Net);
        Value(tree, "totalTax")!["amount"]!.GetValue<decimal>().ShouldBe(invoice.Tax);
        Value(tree, "totalGross")!["amount"]!.GetValue<decimal>().ShouldBe(invoice.Gross);
        tree["invoiceNumber"]!["source"]!["method"]!.GetValue<string>().ShouldBe(nameof(DocProc.Domain.Fields.SourceMethod.Profile));
        tree["invoiceNumber"]!["source"]!["page"]!.GetValue<int>().ShouldBe(1);
        tree["invoiceNumber"]!["status"]!.GetValue<string>().ShouldBe("Extracted");

        var lines = tree["lines"]!.AsArray();
        lines.Count.ShouldBe(invoice.Lines.Count);
        for (var i = 0; i < invoice.Lines.Count; i++)
        {
            var expected = invoice.Lines[i];
            var row = lines[i]!.AsObject();
            Value(row, "supplierSku")!.GetValue<string>().ShouldBe(expected.Sku);
            Value(row, "description")!.GetValue<string>().ShouldBe(expected.Description);
            Value(row, "quantity")!.GetValue<decimal>().ShouldBe(expected.Quantity);
            Value(row, "unitPrice")!["amount"]!.GetValue<decimal>().ShouldBe(expected.UnitPrice);
            Value(row, "lineNet")!["amount"]!.GetValue<decimal>().ShouldBe(expected.Net);
            row["lineNet"]!["source"]!["bBox"].ShouldNotBeNull("each row value carries its place on the page");
        }

        document.DocumentNumber.ShouldBe(invoice.Number);
        document.TotalGross.ShouldBe(invoice.Gross);
    }

    [Fact]
    public async Task A_routed_invoice_is_identified_by_its_layout_and_filled_by_it_without_the_classifiers_or_the_LLM()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        var (layout, version) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping());

        foreach (var invoice in DemoNordwindInvoices.Erp)
        {
            var document = await ProcessAsync(host, invoice);
            document.SupplierLayoutVersionId.ShouldBe(version.Id);
            document.Type.ShouldBe(DocumentTypeKey.Invoice);
            document.TypeSource.ShouldBe(TypeSource.Identification);
            document.TypeConfidence.ShouldBe(1.0);
            document.ExtractionMethod.ShouldBe(ExtractionMethod.Profile);
            ShouldHoldTheInvoice(document, invoice);
            document.Flags.Where(f => f.ResolvedAt is null).Select(f => f.Code).ShouldBeEmpty();
            document.Status.ShouldBe(DocumentStatus.AutoApproved, "warm-up 0, every check passes");

            var applied = JsonNode.Parse(document.AppliedConfigJson!)!["layout"]!;
            applied["layoutKey"]!.GetValue<string>().ShouldBe(NordwindLayouts.ErpKey);
            applied["hits"]!["invoiceNumber"]!.GetValue<string>().ShouldBe($"{NordwindLayouts.ErpKey}@1#invoiceNumber[0]");
            applied["identification"]!.AsArray().Count.ShouldBe(2);
            var events = await host.EventsAsync(document.Id);
            var identified = events.Single(e => e.EventType == LayoutEventTypes.Identified);
            identified.DetailsJson!.ShouldContain(LayoutIdentification.ClassifyStep);
            identified.DetailsJson!.ShouldContain("Rechnung");
        }

        (await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().CountAsync())).ShouldBe(0, "neither the classifier nor the extractor asked the LLM");
        var counters = await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id));
        counters.Identified.ShouldBe(3);
        counters.Applied.ShouldBe(3);
        counters.ConsecutiveFailures.ShouldBe(0);
        (await host.WithDbAsync(db => db.SupplierLayouts.AsNoTracking().SingleAsync(l => l.Id == layout.Id))).LastMatchedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_demo_layout_fixture_identifies_and_fills_every_ERP_invoice()
    {
        // fixtures/demo/layouts/nordwind-invoice-erp.json is the body docs/09 "Demo: routing and layouts" posts to
        // /suppliers/{id}/layouts; it must read every ERP invoice of the demo, the one dropped into the folder included.
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        var body = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(DemoFixtureTests.RepositoryRoot(), "fixtures", "demo", "layouts", "nordwind-invoice-erp.json"), TestContext.Current.CancellationToken))!;
        body["layoutKey"]!.GetValue<string>().ShouldBe(NordwindLayouts.ErpKey);
        var (_, version) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, body["identification"]!.ToJsonString(), body["mapping"]!.ToJsonString());

        foreach (var invoice in DemoNordwindInvoices.Erp.Append(DemoNordwindInvoices.More[0]))
        {
            var document = await ProcessAsync(host, invoice);
            document.SupplierLayoutVersionId.ShouldBe(version.Id, invoice.Number);
            ShouldHoldTheInvoice(document, invoice);
            document.Status.ShouldBe(DocumentStatus.AutoApproved, invoice.Number);
        }

        (await ProcessAsync(host, DemoNordwindInvoices.More[1])).Flags.ShouldContain(f => f.Code == FlagCodes.NewLayout, "the web-shop design is not this layout's");
    }

    [Fact]
    public async Task Both_designs_are_identified_by_their_own_layout_and_filled_with_their_golden_values()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        var (_, erp) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping());
        var (_, web) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.WebShopKey, NordwindLayouts.WebShopRule, NordwindLayouts.WebShopMapping());

        foreach (var invoice in DemoNordwindInvoices.All)
        {
            var document = await ProcessAsync(host, invoice);
            document.SupplierLayoutVersionId.ShouldBe(invoice.Design == DemoNordwindInvoices.Design.Erp ? erp.Id : web.Id, invoice.Number);
            ShouldHoldTheInvoice(document, invoice);
            document.Status.ShouldBe(DocumentStatus.AutoApproved, invoice.Number);
        }
    }

    [Fact]
    public async Task A_new_design_of_a_layout_managed_supplier_is_held_with_NEW_LAYOUT_and_not_processed_with_the_old_layout()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping());
        await ScriptExtractionAsync(host);

        var document = await ProcessAsync(host, DemoNordwindInvoices.WebShop[0]);

        document.SupplierLayoutVersionId.ShouldBeNull();
        document.ExtractionMethod.ShouldBe(ExtractionMethod.Llm, "the ERP layout's field rules never ran on the web-shop design");
        document.Type.ShouldBe(DocumentTypeKey.Invoice, "the keyword rules propose a type");
        document.TypeSource.ShouldBe(TypeSource.Proposed);
        var flag = document.Flags.Single(f => f.Code == FlagCodes.NewLayout);
        flag.Severity.ShouldBe(FlagSeverity.Blocking);
        var p = flag.ToDomain().Params!;
        p[DocumentFlagWriter.StepIdParam].ShouldBe(LayoutIdentification.ClassifyStep);
        p["supplierId"].ShouldBe(nordwind.Id.ToString());
        p["proposedType"].ShouldBe("Invoice");
        p["suggestions"].ShouldContain(NordwindLayouts.ErpKey, customMessage: "the old layout is suggested, never applied");
        p.ShouldNotContainKey("pendingLayoutVersionId");
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        (await host.EventsAsync(document.Id)).ShouldContain(e => e.EventType == LayoutEventTypes.NotIdentified);
    }

    [Fact]
    public async Task Layouts_required_holds_a_supplier_without_layouts_with_NEW_LAYOUT()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        await host.WithDbAsync(async db =>
        {
            var supplier = await db.Suppliers.SingleAsync(s => s.Id == nordwind.Id);
            supplier.SettingsJson = """{"layouts":{"required":"true"}}""";
            await db.SaveChangesAsync();
        });
        await ScriptExtractionAsync(host);

        var document = await ProcessAsync(host, DemoNordwindInvoices.Erp[0]);
        document.Flags.ShouldContain(f => f.Code == FlagCodes.NewLayout);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
    }

    [Fact]
    public async Task The_first_documents_under_a_new_version_go_to_review_for_its_warm_up_already_filled_and_each_is_counted_once()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        var (_, version) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping(), warmup: 2);

        var first = await ProcessAsync(host, DemoNordwindInvoices.Erp[0]);
        first.Status.ShouldBe(DocumentStatus.NeedsReview);
        ShouldHoldTheInvoice(first, DemoNordwindInvoices.Erp[0]);
        var warmup = first.Flags.Single(f => f.Code == FlagCodes.LayoutWarmup).ToDomain();
        warmup.Severity.ShouldBe(FlagSeverity.Info);
        warmup.Params!["ordinal"].ShouldBe("1");
        warmup.Params["warmup"].ShouldBe("2");

        var second = await ProcessAsync(host, DemoNordwindInvoices.Erp[1]);
        second.Status.ShouldBe(DocumentStatus.NeedsReview);
        second.Flags.Single(f => f.Code == FlagCodes.LayoutWarmup).ToDomain().Params!["ordinal"].ShouldBe("2");

        var third = await ProcessAsync(host, DemoNordwindInvoices.Erp[2]);
        third.Status.ShouldBe(DocumentStatus.AutoApproved, "past the warm-up");
        third.Flags.ShouldNotContain(f => f.Code == FlagCodes.LayoutWarmup);

        (await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id))).WarmupSeen.ShouldBe(2);
        var task = await host.WithDbAsync(db => db.ReviewTasks.AsNoTracking().SingleAsync(t => t.DocumentId == first.Id));
        task.Reason.ShouldContain(FlagCodes.LayoutWarmup);
    }

    [Fact]
    public async Task A_validation_dropped_as_a_stale_twin_gives_its_warm_up_place_back()
    {
        // The save that records the first document's warm-up place fails as a twin's would (another node saved the
        // document first): the attempt is dropped, and the place it took must not be lost with it (decision 2 of §24).
        var twin = new TwinSavesFirst();
        var (host, nordwind) = await StartAsync(services: s => s.ConfigureDbContext<DocProc.Infrastructure.Persistence.DocProcDbContext>(o => o.AddInterceptors(twin)));
        await using var _ = host;
        var (_, version) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping(), warmup: 2);

        var dropped = await ProcessAsync(host, DemoNordwindInvoices.Erp[0]);
        twin.Fired.ShouldBe(1);
        dropped.Status.ShouldBe(DocumentStatus.Extracted, "the attempt was dropped unsaved");
        (await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id))).WarmupSeen.ShouldBe(0, "the place rolled back with the attempt");
        (await host.EventsAsync(dropped.Id)).ShouldNotContain(e => e.EventType == LayoutEventTypes.Warmup);

        // The twin's run (here the message again) counts the document once, in the first place.
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, dropped.Id, PipelineStage.Validate), TestContext.Current.CancellationToken);
        await host.RunUntilIdleAsync();
        var first = await host.DocumentAsync(dropped.Id);
        first.Status.ShouldBe(DocumentStatus.NeedsReview);
        first.Flags.Single(f => f.Code == FlagCodes.LayoutWarmup).ToDomain().Params!["ordinal"].ShouldBe("1");

        var second = await ProcessAsync(host, DemoNordwindInvoices.Erp[1]);
        second.Status.ShouldBe(DocumentStatus.NeedsReview, "the second place is still free");
        second.Flags.Single(f => f.Code == FlagCodes.LayoutWarmup).ToDomain().Params!["ordinal"].ShouldBe("2");
        var third = await ProcessAsync(host, DemoNordwindInvoices.Erp[2]);
        third.Status.ShouldBe(DocumentStatus.AutoApproved, "past the warm-up of two");
        (await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id))).WarmupSeen.ShouldBe(2);
    }

    [Fact]
    public async Task An_extraction_dropped_as_a_stale_twin_takes_its_drift_count_back()
    {
        // The Extract save that records the first drifting document's layout_applied event fails as a twin's would: the
        // counters must roll back with it, or the twin's run counts the same document again (docs/11 §8.2).
        var twin = new TwinSavesFirst(LayoutEventTypes.Applied);
        var (host, nordwind) = await StartAsync(services: s => s.ConfigureDbContext<DocProc.Infrastructure.Persistence.DocProcDbContext>(o => o.AddInterceptors(twin)));
        await using var _ = host;
        var (_, version) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping(numberLabel: "Belegnummer"));

        var dropped = await ProcessAsync(host, DemoNordwindInvoices.Erp[0]);
        twin.Fired.ShouldBe(1);
        dropped.Status.ShouldBe(DocumentStatus.Matched, "the attempt was dropped unsaved");
        var rolledBack = await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id));
        rolledBack.Applied.ShouldBe(0, "the count rolled back with the attempt");
        rolledBack.ConsecutiveFailures.ShouldBe(0);
        (await host.EventsAsync(dropped.Id)).ShouldNotContain(e => e.EventType == LayoutEventTypes.Applied);

        // The twin's run (here the message again) counts the document once.
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, dropped.Id, PipelineStage.Extract), TestContext.Current.CancellationToken);
        await host.RunUntilIdleAsync();
        var counted = await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id));
        counted.Applied.ShouldBe(1);
        counted.ConsecutiveFailures.ShouldBe(1, "one drifting document, counted once");
        counted.Status.ShouldBe(SupplierLayoutVersionStatus.Active);
        (await host.EventsAsync(dropped.Id)).Count(e => e.EventType == LayoutEventTypes.Applied).ShouldBe(1);
        (await host.DocumentAsync(dropped.Id)).Flags.ShouldContain(f => f.Code == FlagCodes.MappingDrift);
    }

    /// <summary>Fails the first save that records an event of the kind (a warm-up place by default), as a stale twin's save fails.</summary>
    private sealed class TwinSavesFirst(string eventType = LayoutEventTypes.Warmup) : SaveChangesInterceptor
    {
        private int _fired;

        public int Fired => _fired;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context
                && context.ChangeTracker.Entries<DocumentEvent>().Any(e => e.State == EntityState.Added && e.Entity.EventType == eventType)
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                throw new DbUpdateConcurrencyException("Another node saved the document first.");
            }

            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Two_layouts_holding_with_equal_priority_and_specificity_are_ambiguous_and_none_is_applied()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping());
        await NordwindLayouts.AddAsync(host, nordwind.Id, "nordwind-invoice-twin", """{ "all": [ { "text": "Rechnung" }, { "text": "Nordwind" } ] }""", NordwindLayouts.ErpMapping());
        await ScriptExtractionAsync(host);

        var document = await ProcessAsync(host, DemoNordwindInvoices.Erp[0]);
        document.SupplierLayoutVersionId.ShouldBeNull();
        var flag = document.Flags.Single(f => f.Code == FlagCodes.LayoutAmbiguous);
        flag.Severity.ShouldBe(FlagSeverity.Blocking);
        flag.ToDomain().Params!["layouts"].ShouldContain("nordwind-invoice-twin");
        document.Status.ShouldBe(DocumentStatus.NeedsReview);

        // A higher priority decides.
        await host.WithDbAsync(async db =>
        {
            var erp = await db.SupplierLayouts.SingleAsync(l => l.LayoutKey == NordwindLayouts.ErpKey);
            erp.Priority = 200;
            await db.SaveChangesAsync();
        });
        var next = await ProcessAsync(host, DemoNordwindInvoices.Erp[1]);
        next.Flags.ShouldNotContain(f => f.Code == FlagCodes.LayoutAmbiguous);
        next.SupplierLayoutVersionId.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_Candidate_whose_rule_holds_is_never_applied_and_is_named_as_the_pending_layout()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping());
        var (_, pending) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.WebShopKey, NordwindLayouts.WebShopRule, NordwindLayouts.WebShopMapping(), SupplierLayoutVersionStatus.Candidate);
        await ScriptExtractionAsync(host);

        var document = await ProcessAsync(host, DemoNordwindInvoices.WebShop[1]);
        document.SupplierLayoutVersionId.ShouldBeNull();
        var flag = document.Flags.Single(f => f.Code == FlagCodes.NewLayout).ToDomain();
        flag.Params!["pendingLayoutVersionId"].ShouldBe(pending.Id.ToString());
        flag.Params["pendingLayoutKey"].ShouldBe(NordwindLayouts.WebShopKey);
        document.ExtractionMethod.ShouldNotBe(ExtractionMethod.Profile);
    }

    [Fact]
    public async Task A_supplier_found_by_content_has_its_layouts_resolved_in_Match_and_owns_its_flags_there()
    {
        var (host, nordwind) = await StartAsync(routed: false);
        await using var _ = host;
        var (_, erp) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping());

        var identified = await ProcessAsync(host, DemoNordwindInvoices.Erp[0], routed: false);
        identified.SupplierId.ShouldBe(nordwind.Id);
        identified.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.Vat);
        identified.SupplierLayoutVersionId.ShouldBe(erp.Id);
        identified.TypeSource.ShouldBe(TypeSource.Identification);
        ShouldHoldTheInvoice(identified, DemoNordwindInvoices.Erp[0]);
        (await host.EventsAsync(identified.Id)).Single(e => e.EventType == LayoutEventTypes.Identified).DetailsJson!.ShouldContain(LayoutIdentification.MatchStep);

        // The web shop prints the VAT id only on page 2; the matcher still finds Nordwind by it.
        await ScriptExtractionAsync(host);
        var held = await ProcessAsync(host, DemoNordwindInvoices.WebShop[2], routed: false);
        held.SupplierId.ShouldBe(nordwind.Id);
        var flag = held.Flags.Single(f => f.Code == FlagCodes.NewLayout).ToDomain();
        flag.Params![DocumentFlagWriter.StepIdParam].ShouldBe(LayoutIdentification.MatchStep);
        held.TypeSource.ShouldBe(TypeSource.Proposed);
        held.Status.ShouldBe(DocumentStatus.NeedsReview);
    }

    [Fact]
    public async Task A_layout_whose_field_rules_stop_matching_drifts_and_is_degraded_after_three_drifting_documents()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;

        // The supplier renamed its number label: the rule still holds, the required number is no longer found.
        var (_, version) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping(numberLabel: "Belegnummer"));

        foreach (var invoice in DemoNordwindInvoices.Erp)
        {
            var document = await ProcessAsync(host, invoice);
            document.SupplierLayoutVersionId.ShouldBe(version.Id);
            var drift = document.Flags.Single(f => f.Code == FlagCodes.MappingDrift).ToDomain();
            drift.Severity.ShouldBe(FlagSeverity.Warning);
            drift.Params!["missing"].ShouldContain("invoiceNumber");
            document.Status.ShouldBe(DocumentStatus.NeedsReview);
        }

        var degraded = await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id));
        degraded.Status.ShouldBe(SupplierLayoutVersionStatus.Degraded);
        degraded.ConsecutiveFailures.ShouldBe(3);

        // Degraded, the version still identifies the design, but its field mapping is not used and the gate holds it.
        // A redrawn copy (other bytes, so ingest does not drop it as the same file).
        var next = await ProcessAsync(host, DemoNordwindInvoices.Erp[0], redrawn: DemoNordwindInvoices.Erp[0].Pdf());
        next.SupplierLayoutVersionId.ShouldBe(version.Id);
        next.ExtractionMethod.ShouldNotBe(ExtractionMethod.Profile);
        next.Status.ShouldNotBe(DocumentStatus.AutoApproved);
    }

    [Fact]
    public async Task A_drifting_document_counts_once_however_often_it_is_extracted_again()
    {
        var (host, nordwind) = await StartAsync();
        await using var _ = host;
        var (_, version) = await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping(numberLabel: "Belegnummer"));
        var document = await ProcessAsync(host, DemoNordwindInvoices.Erp[0]);
        document.Flags.ShouldContain(f => f.Code == FlagCodes.MappingDrift);

        // Sent back from Extract twice (a reprocess, a bulk reprocess, an activation's reprocessWaiting).
        for (var run = 0; run < 2; run++)
        {
            await host.WithDbAsync(async db =>
            {
                var row = await db.Documents.SingleAsync(d => d.Id == document.Id);
                row.Status = DocumentStatus.Matched;
                await db.SaveChangesAsync();
            });
            await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, document.Id, PipelineStage.Extract), TestContext.Current.CancellationToken);
            await host.RunUntilIdleAsync();
        }

        var counted = await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id));
        counted.Status.ShouldBe(SupplierLayoutVersionStatus.Active, "one drifting document is not three in a row");
        counted.ConsecutiveFailures.ShouldBe(1);
        counted.Applied.ShouldBe(1);
        (await host.EventsAsync(document.Id)).Count(e => e.EventType == LayoutEventTypes.Applied).ShouldBe(1);
        (await host.DocumentAsync(document.Id)).Flags.ShouldContain(f => f.Code == FlagCodes.MappingDrift, "the flag is the document's on every run");
    }

    [Fact]
    public async Task A_supplier_guessed_from_its_mail_domain_gets_no_layout_resolution_in_Match()
    {
        // match.layout runs once VAT or IBAN names the supplier (docs/11 §7.4): Nordwind without a VAT id in its master
        // data, and without a source rule, is found by the sender's mail domain only (below 0.95), and gets no
        // NEW_LAYOUT, although it is layout-managed.
        var host = await PipelineTestHost.StartAsync(settings => settings["Pipeline:LlmClassificationThreshold"] = "0");
        await using var _ = host;
        await host.WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA);
            tenant.Settings.Features!.SourceRouting = true;
            tenant.Settings.Features!.SupplierLayouts = true;
            await db.SaveChangesAsync();
        });
        var nordwind = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Nordwind (Altdaten)", emailDomains: ["nordwind.example"]);
        await host.WithDbAsync(async db =>
        {
            var supplier = await db.Suppliers.SingleAsync(s => s.Id == nordwind.Id);
            supplier.SettingsJson = """{"layouts":{"required":"true"}}""";
            await db.SaveChangesAsync();
        });
        await ScriptExtractionAsync(host);

        var document = await ProcessAsync(host, DemoNordwindInvoices.WebShop[2]);

        document.SupplierId.ShouldBe(nordwind.Id);
        document.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.Domain);
        document.SupplierMatchConfidence.ShouldNotBeNull().ShouldBeLessThan(0.95);
        document.SupplierLayoutVersionId.ShouldBeNull();
        document.Flags.ShouldNotContain(f => f.Code == FlagCodes.NewLayout);
        document.TypeSource.ShouldNotBe(TypeSource.Proposed);
        (await host.EventsAsync(document.Id)).ShouldNotContain(e => e.EventType == LayoutEventTypes.NotIdentified);
    }

    [Fact]
    public async Task A_tenant_without_supplierLayouts_ignores_layouts()
    {
        var (host, nordwind) = await StartAsync(layouts: false);
        await using var _ = host;
        await NordwindLayouts.AddAsync(host, nordwind.Id, NordwindLayouts.ErpKey, NordwindLayouts.ErpRule, NordwindLayouts.ErpMapping());
        await host.WithDbAsync(async db =>
        {
            var supplier = await db.Suppliers.SingleAsync(s => s.Id == nordwind.Id);
            supplier.SettingsJson = """{"layouts":{"required":"true"}}""";
            await db.SaveChangesAsync();
        });
        await ScriptExtractionAsync(host);

        var document = await ProcessAsync(host, DemoNordwindInvoices.WebShop[0]);
        document.SupplierLayoutVersionId.ShouldBeNull();
        document.TypeSource.ShouldBe(TypeSource.Rule);
        document.Flags.ShouldNotContain(f => f.Code == FlagCodes.NewLayout || f.Code == FlagCodes.LayoutWarmup);
        (await host.EventsAsync(document.Id)).ShouldNotContain(e => e.EventType == LayoutEventTypes.Identified || e.EventType == LayoutEventTypes.NotIdentified);
    }
}
