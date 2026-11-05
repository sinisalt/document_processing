using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Routing;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Layouts;
using DocProc.Pipeline.Tests.Demo;
using DocProc.Pipeline.Tests.Pdf;
using DocProc.Pipeline.Tests.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests.Layouts;

/// <summary>
/// The committed files of the P5 demos (docs/09 "Demo: price lists, specials and fallback rules") as the compose run
/// uses them, without an LLM: the specials layout of <c>fixtures/demo/layouts/nordwind-daily-specials.json</c> posted as
/// a Candidate is activated by confirming the first (undated) list against it, although that list prints no date (its
/// <c>validFrom</c> is the day received, which no layout can read); the dated copy is then identified and blocked as
/// expired. The Südhof and Bergquell lists are typed by a tenant fallback rule while Nordwind's April list, from a
/// supplier with its own layout, is decided by that layout. The January list with its printed count is drawn in the
/// design of the later lists.
/// </summary>
public sealed class PriceListDemoFixturesTests
{
    private const string PdfMime = "application/pdf";
    private const string Sender = "angebote@nordwind.example";
    private static readonly SenderAuthentication DmarcPass = new() { Dmarc = "pass", FromDomain = "nordwind.example" };

    /// <summary>06:40 in Berlin on 5 November 2026, the day of the compose run: the undated specials are valid today.</summary>
    private static readonly DateTimeOffset Morning = new(2026, 11, 5, 5, 40, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Upload(string fileName) => File.ReadAllBytes(DemoFixtureTests.UploadPath(fileName));

    private static async Task<PipelineTestHost> StartAsync(params string[] templates)
    {
        var host = await PipelineTestHost.StartAsync();
        host.Clock.SetUtcNow(Morning);
        await host.WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA, Ct);
            tenant.Settings.Features!.SourceRouting = true;
            tenant.Settings.Features.SupplierLayouts = true;
            tenant.Settings.Features.ConfigurableTypes = true;
            tenant.Settings.Features.ConfigurableSteps = true;
            tenant.Settings.LayoutReviewWarmup = 0;
            await db.SaveChangesAsync(Ct);
            var catalogue = new DocumentTypeCatalogue(db, TimeProvider.System);
            foreach (var template in templates)
            {
                var adopted = await catalogue.AdoptAsync(PipelineTestHost.TenantA, template, null, "tenant-a-admin", Ct);
                adopted.Status.ShouldBe(CatalogueStatus.Created, adopted.Detail);
                (await catalogue.UpgradeBaseAsync(PipelineTestHost.TenantA, adopted.Value!.Definition.Key, autoApprove: true, "tenant-a-admin", Ct)).Status.ShouldBe(CatalogueStatus.Ok);
            }
        });
        return host;
    }

    private static async Task<Document> SendAsync(PipelineTestHost host, string fileName)
    {
        var ingestion = await SourceRoutingPipelineTests.IngestAsync(host, [new IngestFile(fileName, PdfMime, Upload(fileName))], new SourceMetadata(Sender: Sender) { SenderAuth = DmarcPass });
        await host.RunUntilIdleAsync(TimeSpan.FromSeconds(90));
        return await host.SingleDocumentAsync(ingestion.Id);
    }

    private static Task<List<DocumentFlagRecord>> OpenFlagsAsync(PipelineTestHost host, Guid documentId) =>
        host.WithDbAsync(db => db.DocumentFlags.AsNoTracking().Where(f => f.DocumentId == documentId && f.ResolvedAt == null).ToListAsync(Ct));

    [Fact]
    public async Task The_posted_specials_layout_is_activated_by_an_undated_list_and_then_blocks_the_dated_copy_as_expired()
    {
        await using var host = await StartAsync("std.daily-specials");
        var nordwind = await host.AddSupplierAsync(PipelineTestHost.TenantA, DemoPriceList.SupplierName, vatId: DemoPriceList.SupplierVatId);
        await host.WithDbAsync(async db =>
        {
            var supplier = await db.Suppliers.SingleAsync(s => s.Id == nordwind.Id, Ct);
            supplier.SettingsJson = """{"layouts":{"required":"true"}}""";
            await db.SaveChangesAsync(Ct);
        });
        await SourceRoutingPipelineTests.AddRuleAsync(host, nordwind.Id, r =>
        {
            r.Sender = Sender;
            r.PinTypeKey = "daily-specials";
        });
        var body = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(DemoFixtureTests.RepositoryRoot(), "fixtures", "demo", "layouts", "nordwind-daily-specials.json"), Ct))!;
        body["documentTypeKey"]!.GetValue<string>().ShouldBe("daily-specials");
        var (_, candidate) = await NordwindLayouts.AddAsync(host, nordwind.Id, body["layoutKey"]!.GetValue<string>(), body["identification"]!.ToJsonString(), body["mapping"]!.ToJsonString(), SupplierLayoutVersionStatus.Candidate, warmup: 0, type: "daily-specials");

        // The first list waits for the Candidate (NEW_LAYOUT naming it), pre-filled from it, urgent until the end of the day.
        var first = await SendAsync(host, DemoPriceLists.Specials.FileName);
        first.Status.ShouldBe(DocumentStatus.NeedsReview);
        first.PendingLayoutVersionId.ShouldBe(candidate.Id);
        first.ValidFrom.ShouldBe(new DateOnly(2026, 11, 5), "no date printed: the defaults step takes the day received");
        first.LineCount.ShouldBe(DemoPriceLists.Specials.Rows.Count);
        (await host.WithDbAsync(db => db.ReviewTasks.AsNoTracking().SingleAsync(t => t.DocumentId == first.Id, Ct))).Priority.ShouldBe(ReviewPriority.Urgent);

        // A MappingAuthor confirms it against the pending layout: the replay passes although the layout reads no date on
        // this list (compose run: "4 fields replayed on 1 documents; lowest agreement 0 %", the Candidate stayed one).
        using (var scope = host.CreateTenantScope(PipelineTestHost.TenantA))
        {
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<DocProcDbContext>();
            var learning = new LayoutLearning(db, services.GetRequiredService<IContainerBlobStore>(), services.GetRequiredService<IEffectiveConfigResolver>(), host.Clock);
            var document = await db.Documents.Include(d => d.Flags).Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).SingleAsync(d => d.Id == first.Id, Ct);
            var result = await new LayoutLearningSave(learning, db, host.Clock).SaveAsync(document, new LearnLayoutCommand(LearnSaveAs.NewLayout, PendingLayoutVersionId: candidate.Id, MayActivate: true), "tenant-a-reviewer", Ct);
            result.Code.ShouldBeNull(result.Problem);
            result.Activation.ShouldNotBeNull().Activated.ShouldBeTrue(result.Activation.Refusal);
            var replayed = result.Activation.Replay.Documents.ShouldHaveSingleItem();
            replayed.Fields["validFrom"].ShouldBe("skipped", "the list prints no date: nothing for the layout to read");
            replayed.Fields["lines"].ShouldBe("hit");
            result.Activation.Replay.Fields.ShouldAllBe(f => f.Agreement == 1.0);
            await db.SaveChangesAsync(Ct);
        }

        (await host.WithDbAsync(db => db.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == candidate.Id, Ct))).Status.ShouldBe(SupplierLayoutVersionStatus.Active);

        // The copy printed for 4 November is identified by the now Active layout, which reads its date: blocked as expired.
        var dated = await SendAsync(host, DemoPriceLists.SpecialsDated.FileName);
        dated.SupplierLayoutVersionId.ShouldBe(candidate.Id);
        dated.ValidUntil.ShouldBe(new DateOnly(2026, 11, 4));
        var flags = await OpenFlagsAsync(host, dated.Id);
        flags.ShouldNotContain(f => f.Code == FlagCodes.NewLayout);
        flags.Where(f => f.Params != null && f.Params.GetValueOrDefault("ruleId") == "r_not_expired").ShouldNotBeEmpty();
        flags.Where(f => f.Params != null && f.Params.GetValueOrDefault("ruleId") == "r_not_expired").ShouldAllBe(f => f.Severity == FlagSeverity.Blocking);
        dated.Status.ShouldBe(DocumentStatus.NeedsReview);

        // A second undated list of the day: identified, its validity the day received again, which is no drift of the
        // layout (compose run: MAPPING_DRIFT "not the required validFrom" on every undated list), and REVISION_OF the first.
        var second = await SendAsync(host, DemoPriceLists.SpecialsSecond.FileName);
        second.SupplierLayoutVersionId.ShouldBe(candidate.Id);
        second.ValidFrom.ShouldBe(new DateOnly(2026, 11, 5));
        var secondFlags = await OpenFlagsAsync(host, second.Id);
        secondFlags.ShouldNotContain(f => f.Code == FlagCodes.MappingDrift, string.Join("; ", secondFlags.Select(f => f.Code + " " + f.Message)));
        secondFlags.Single(f => f.Code == FlagCodes.RevisionOf).Params!["documentId"].ShouldBe(first.Id.ToString());
        host.Llm.Requests.ShouldBeEmpty("no LLM is configured");
    }

    [Fact]
    public async Task The_Suedhof_and_Bergquell_lists_are_typed_by_the_tenant_rule_and_Nordwinds_April_list_by_its_layout()
    {
        await using var host = await StartAsync("std.price-list");
        var suedhof = await host.AddSupplierAsync(PipelineTestHost.TenantA, DemoPriceLists.Suedhof.Name, vatId: DemoPriceLists.Suedhof.VatId);
        var nordwind = await host.AddSupplierAsync(PipelineTestHost.TenantA, DemoPriceList.SupplierName, vatId: DemoPriceList.SupplierVatId);
        await SourceRoutingPipelineTests.AddRuleAsync(host, nordwind.Id, r =>
        {
            r.Sender = Sender;
            r.PinTypeKey = "price-list";
        });
        var (_, layout) = await NordwindLayouts.AddAsync(host, nordwind.Id, "nordwind-price-list", PriceListsEndToEndTests.PriceListRule, PriceListsEndToEndTests.PriceListMapping(), type: "price-list");
        var rule = await host.WithDbAsync(async db =>
        {
            var parsed = IdentificationRule.Parse("""{ "all": [ { "text": "Preisliste", "page": 1, "region": "title" } ] }""");
            var row = new TypeIdentificationRule { Id = Guid.CreateVersion7(), TenantId = PipelineTestHost.TenantA, DocumentTypeKey = "price-list", Name = "Preisliste im Titel", ActiveVersion = 1, CreatedAt = PipelineTestHost.Now, UpdatedAt = PipelineTestHost.Now };
            var version = new TypeIdentificationRuleVersion { Id = Guid.CreateVersion7(), TenantId = PipelineTestHost.TenantA, Version = 1, State = TypeIdentificationRuleVersionState.Active, RuleJson = parsed.ToJson(), RuleHash = parsed.Hash, ExcludedSupplierIdsJson = "[]", CreatedAt = PipelineTestHost.Now };
            row.Versions.Add(version);
            db.TypeIdentificationRules.Add(row);
            await db.SaveChangesAsync(Ct);
            return version;
        });

        foreach (var (list, supplierId) in new[] { (DemoPriceLists.SuedhofFebruary, (Guid?)suedhof.Id), (DemoPriceLists.UnknownSender, null) })
        {
            var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile(list.FileName, PdfMime, list.Pdf())], from: "tenant-a-admin@tenant-a.local");
            await host.RunUntilIdleAsync(TimeSpan.FromSeconds(90));
            var document = await host.SingleDocumentAsync(ingestion.Id);
            document.Type.Value.ShouldBe("price-list", list.FileName);
            document.TypeSource.ShouldBe(TypeSource.Identification, list.FileName);
            document.IdentificationRuleVersionId.ShouldBe(rule.Id, list.FileName);
            document.SupplierLayoutVersionId.ShouldBeNull(list.FileName);
            document.SupplierId.ShouldBe(supplierId, list.FileName);
        }

        var april = await SendAsync(host, DemoPriceLists.April.FileName);
        april.SupplierLayoutVersionId.ShouldBe(layout.Id, "a supplier with its own layouts is decided by them");
        april.IdentificationRuleVersionId.ShouldBeNull();
        april.LineCount.ShouldBe(DemoPriceList.RowCount);
        host.Llm.Requests.ShouldBeEmpty("neither the keyword rules' LLM fallback nor an extraction was asked");
        (await host.WithDbAsync(db => db.TypeIdentificationRules.AsNoTracking().SingleAsync(r => r.Id == rule.RuleId, Ct))).Matched.ShouldBe(2);

        // The April list from a sender no source rule knows: Classify knows no supplier, so the tenant rule types it; Match
        // then finds Nordwind by its VAT id and Nordwind's own layout identifies it. The layout decided, so the rule's pin
        // is cleared and its match is not counted (docs/11 §6.2, §8.5). A comment after the end keeps the copy's bytes apart
        // from the routed April list, which the intake would otherwise take as the same file again.
        var unrouted = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("April_unrouted.pdf", PdfMime, [.. Upload(DemoPriceLists.April.FileName), .. "\n% another copy\n"u8.ToArray()])], from: "tenant-a-admin@tenant-a.local");
        await host.RunUntilIdleAsync(TimeSpan.FromSeconds(90));
        var byContent = await host.SingleDocumentAsync(unrouted.Id);
        byContent.SupplierId.ShouldBe(nordwind.Id);
        byContent.SupplierLayoutVersionId.ShouldBe(layout.Id);
        byContent.IdentificationRuleVersionId.ShouldBeNull("Nordwind's layout, not the tenant rule, decided the type");
        (await host.EventsAsync(byContent.Id)).ShouldContain(e => e.EventType == TenantIdentification.IdentifiedEvent, "Classify did apply the rule first");
        (await host.WithDbAsync(db => db.TypeIdentificationRules.AsNoTracking().SingleAsync(r => r.Id == rule.RuleId, Ct))).Matched.ShouldBe(2, "the match the layout took over is taken back");
    }

    /// <summary>
    /// docs/09 "Demo: price lists, specials and fallback rules", step 5, with the supplier as step 2 sets it up: the
    /// January list arrives with NEW_LAYOUT and the New layout panel's five pre-filled values (the supplier's name, VAT id
    /// and IBAN from master data, Valid from and the printed article count from the type's labels).
    /// </summary>
    [Fact]
    public async Task The_January_list_is_pre_filled_with_five_values_when_Nordwind_has_its_IBAN()
    {
        await using var host = await StartAsync("std.price-list");
        var nordwind = await host.AddSupplierAsync(PipelineTestHost.TenantA, DemoPriceList.SupplierName, vatId: DemoPriceList.SupplierVatId, ibans: ["DE44 2005 0550 1234 5678 90"]);
        await host.WithDbAsync(async db =>
        {
            (await db.Suppliers.SingleAsync(s => s.Id == nordwind.Id, Ct)).SettingsJson = """{"layouts":{"required":true}}""";
            await db.SaveChangesAsync(Ct);
        });
        await SourceRoutingPipelineTests.AddRuleAsync(host, nordwind.Id, r =>
        {
            r.Sender = Sender;
            r.PinTypeKey = "price-list";
        });

        var january = await SendAsync(host, DemoPriceLists.January.FileName);

        (await OpenFlagsAsync(host, january.Id)).ShouldContain(f => f.Code == FlagCodes.NewLayout);
        var sources = JsonNode.Parse(january.AppliedConfigJson!)!["prefill"]!["sources"]!.AsObject();
        string.Join(",", sources.Select(s => s.Key).Order(StringComparer.Ordinal)).ShouldBe("declaredLineCount,supplier.iban,supplier.name,supplier.vatId,validFrom", string.Join(", ", sources.Select(s => $"{s.Key}={s.Value}")));
        sources["supplier.iban"]!.GetValue<string>().ShouldBe(DocProc.Mapping.Learning.PrefillSources.MasterData);
    }

    [Fact]
    public void The_January_list_of_the_demo_prints_its_count_in_the_design_of_the_later_lists()
    {
        var january = DemoPriceLists.January;
        january.Articles.ShouldBe(DemoPriceList.Articles, "the same 412 articles and prices as the P2 list");
        january.DeclaredCount.ShouldBe(DemoPriceList.RowCount);
        january.PageCount.ShouldBe(DemoPriceList.PageCount);
        File.Exists(DemoFixtureTests.UploadPath(january.FileName)).ShouldBeTrue();
        // April moves two prices against February, both within 25 %, so it passes against February while March waits.
        var changed = DemoPriceLists.April.Articles.Zip(DemoPriceLists.February.Articles).Where(p => p.First.Price != p.Second.Price).ToList();
        changed.Count.ShouldBe(DemoPriceLists.AprilChanges.Count);
        changed.ShouldAllBe(p => Math.Abs((p.First.Price / p.Second.Price) - 1m) <= 0.25m);
    }
}
