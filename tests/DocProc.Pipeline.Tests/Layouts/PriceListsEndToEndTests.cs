using System.Globalization;
using System.Text.Json.Nodes;
using DocProc.Application.Routing;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Tests.Demo;
using DocProc.Pipeline.Tests.Pdf;
using DocProc.Pipeline.Tests.Routing;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Tests.Layouts;

/// <summary>
/// Price lists and daily specials end to end (docs/11 §5.8, §11, §23 P5) on the committed demo PDFs, with the price-list
/// templates v2 and auto-approval switched on by a ConfigAdmin (<c>:upgrade-base</c>): a layout reads every row with a
/// table report; the January list (no previous list) and February's (12 rows changed within 25 %, the printed count
/// "Anzahl Artikel: 412" checked) auto-approve; March's, with one article up 40 %, goes to review with that row named
/// for the grid. A specials list sorts as urgent with its due time at the end of its day, a second list of the same day
/// arrives with <c>REVISION_OF</c>, and a list printed for yesterday is blocked as expired. No LLM is configured.
/// </summary>
public sealed class PriceListsEndToEndTests
{
    private const string Sender = "preise@nordwind.example";

    /// <summary>06:40 in Berlin on 5 November 2026: the specials list without a date is valid today.</summary>
    private static readonly DateTimeOffset Morning = new(2026, 11, 5, 5, 40, 0, TimeSpan.Zero);

    private static string U(double points) => Math.Round(points * 1000 / DemoNordwindInvoices.PageWidth).ToString(CultureInfo.InvariantCulture);

    internal static string PriceListMapping() => $$"""
        { "locale": "de-DE",
          "fields": {
            "priceListNumber": { "rules": [ { "strategy": "label-right", "labels": ["Listen-Nr."], "page": 1 } ] },
            "validFrom": { "rules": [ { "strategy": "regex-anywhere", "pattern": "gültig ab\\s+(\\d{2}\\.\\d{2}\\.\\d{4})", "group": 1, "page": 1 } ] },
            "currency": { "rules": [ { "strategy": "regex-anywhere", "pattern": "Alle Preise in ([A-Z]{3})", "group": 1, "page": 1 } ] },
            "declaredLineCount": { "rules": [ { "strategy": "regex-anywhere", "pattern": "Anzahl Artikel:\\s*(\\d+)", "group": 1, "page": 1 } ] },
            "supplier.name": { "rules": [ { "strategy": "constant", "value": "{{DemoPriceList.SupplierName}}" } ] },
            "lines": { "strategy": "column-table", "page": "*", "bBox": [60, 280, 935, 940], "headerRow": { "y0": 280, "y1": 296 },
              "columns": [
                { "x0": {{U(36)}}, "x1": {{U(94)}}, "field": "supplierSku", "header": "Art.-Nr." },
                { "x0": {{U(94)}}, "x1": {{U(316)}}, "field": "description", "header": "Bezeichnung" },
                { "x0": {{U(316)}}, "x1": {{U(400)}}, "field": "packDescription", "header": "Gebinde" },
                { "x0": {{U(400)}}, "x1": {{U(442)}}, "field": "price", "header": "Preis" },
                { "x0": {{U(442)}}, "x1": {{U(498)}}, "header": "ab 10", "tierMinQuantity": 10 },
                { "x0": {{U(498)}}, "x1": {{U(556)}}, "header": "ab 50", "tierMinQuantity": 50 } ],
              "rowAnchor": "supplierSku", "groupHeader": { "target": "category" }, "tierColumns": { "headerPattern": "^ab\\s*(\\d+)" } } } }
        """;

    internal const string PriceListRule = """{ "all": [ { "text": "Preisliste", "page": 1, "region": "title" }, { "regex": "gültig ab\\s+\\d{2}\\.\\d{2}\\.\\d{4}", "page": 1 } ] }""";

    internal static string SpecialsMapping() => $$"""
        { "locale": "de-DE",
          "fields": {
            "validFrom": { "rules": [ { "strategy": "regex-anywhere", "pattern": "gültig am\\s+(\\d{2}\\.\\d{2}\\.\\d{4})", "group": 1, "page": 1 } ] },
            "currency": { "rules": [ { "strategy": "regex-anywhere", "pattern": "Alle Preise in ([A-Z]{3})", "group": 1, "page": 1 } ] },
            "supplier.name": { "rules": [ { "strategy": "constant", "value": "{{DemoPriceList.SupplierName}}" } ] },
            "lines": { "strategy": "column-table", "page": "*", "bBox": [60, 280, 935, 940], "headerRow": { "y0": 280, "y1": 296 },
              "columns": [
                { "x0": {{U(36)}}, "x1": {{U(94)}}, "field": "supplierSku", "header": "Art.-Nr." },
                { "x0": {{U(94)}}, "x1": {{U(316)}}, "field": "description", "header": "Bezeichnung" },
                { "x0": {{U(316)}}, "x1": {{U(400)}}, "field": "packDescription", "header": "Gebinde" },
                { "x0": {{U(400)}}, "x1": {{U(474)}}, "field": "price", "header": "Angebot" },
                { "x0": {{U(474)}}, "x1": {{U(556)}}, "field": "previousPrice", "header": "statt" } ],
              "rowAnchor": "supplierSku" } } }
        """;

    internal const string SpecialsRule = """{ "all": [ { "text": "Tagesangebote", "page": 1, "region": "title" } ] }""";

    private static async Task<(PipelineTestHost Host, Guid SupplierId)> StartAsync(string template, string layoutKey, string rule, string mapping, int warmup = 0)
    {
        var host = await PipelineTestHost.StartAsync();
        host.Clock.SetUtcNow(Morning);
        await host.WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA);
            tenant.Settings.Features!.SourceRouting = true;
            tenant.Settings.Features!.SupplierLayouts = true;
            tenant.Settings.Features!.ConfigurableTypes = true;
            // The type's own steps (defaults, the llm step's exclude) run with configurableSteps (docs/11 §10.1).
            tenant.Settings.Features!.ConfigurableSteps = true;
            await db.SaveChangesAsync();
            var catalogue = new DocumentTypeCatalogue(db, TimeProvider.System);
            var adopted = await catalogue.AdoptAsync(PipelineTestHost.TenantA, template, null, "tenant-a-admin", TestContext.Current.CancellationToken);
            adopted.Status.ShouldBe(CatalogueStatus.Created, adopted.Detail);
            adopted.Value!.Versions.Single().State.ShouldBe(DocumentTypeVersionState.Published);
            // A ConfigAdmin switches auto-approval on (docs/11 §5.8): adoption alone keeps it off.
            var upgraded = await catalogue.UpgradeBaseAsync(PipelineTestHost.TenantA, adopted.Value.Definition.Key, autoApprove: true, "tenant-a-admin", TestContext.Current.CancellationToken);
            upgraded.Status.ShouldBe(CatalogueStatus.Ok, upgraded.Detail);
        });
        var typeKey = template == "std.price-list" ? "price-list" : "daily-specials";
        var nordwind = await host.AddSupplierAsync(PipelineTestHost.TenantA, DemoPriceList.SupplierName, vatId: DemoPriceList.SupplierVatId);
        await SourceRoutingPipelineTests.AddRuleAsync(host, nordwind.Id, r => r.Sender = Sender);
        await NordwindLayouts.AddAsync(host, nordwind.Id, layoutKey, rule, mapping, warmup: warmup, type: typeKey);
        return (host, nordwind.Id);
    }

    private static async Task<Document> SendAsync(PipelineTestHost host, string fileName, byte[] bytes)
    {
        var ingestion = await SourceRoutingPipelineTests.IngestAsync(
            host,
            [new IngestFile(fileName, "application/pdf", bytes)],
            new SourceMetadata(Sender: Sender) { SenderAuth = new SenderAuthentication { Dmarc = "pass", FromDomain = "nordwind.example" } });
        await host.RunUntilIdleAsync(TimeSpan.FromSeconds(90));
        return await host.SingleDocumentAsync(ingestion.Id);
    }

    private static Task<List<DocumentFlagRecord>> OpenFlagsAsync(PipelineTestHost host, Guid documentId) =>
        host.WithDbAsync(db => db.DocumentFlags.AsNoTracking().Where(f => f.DocumentId == documentId && f.ResolvedAt == null).ToListAsync());

    private static byte[] Upload(string fileName) => File.ReadAllBytes(DemoFixtureTests.UploadPath(fileName));

    [Fact]
    public async Task A_type_adopted_at_version_1_follows_the_template_to_version_2_with_auto_approval_still_off()
    {
        // docs/11 §5.8: "adopted types move to it automatically, ... an automatic template upgrade never switches
        // review.autoApprove on". The start-up seeder does it (TypeCatalogueSeeder.EnsureAllAsync).
        await using var host = await PipelineTestHost.StartAsync();
        var v1 = TypeCatalogueSeedSql.FrozenTemplates().Single(t => t.Template.Key == "std.price-list").Template;
        var now = host.Clock.GetUtcNow();
        await host.WithDbAsync(async db =>
        {
            await TypeCatalogueSeeder.AddMissingPlatformTemplatesAsync(db, now);
            var definition = new DocumentTypeDefinition
            {
                Id = Guid.CreateVersion7(),
                TenantId = PipelineTestHost.TenantA,
                Key = "price-list",
                Name = new Dictionary<string, string>(v1.Name, StringComparer.Ordinal),
                Origin = DocProc.Application.Configuration.DefinitionOrigin.Adopted,
                BaseDefinitionId = TypeCatalogueRows.PlatformDefinitionId("std.price-list"),
                BaseVersion = 1,
                Status = DocumentTypeStatus.Active,
                PublishedVersion = 1,
                CreatedAt = now,
                CreatedBy = "test",
                UpdatedAt = now,
            };
            definition.Versions.Add(TypeCatalogueRows.PublishedVersion(Guid.CreateVersion7(), definition.Id, PipelineTestHost.TenantA, TypeCatalogueRows.Compile(TypeCatalogueRows.Adopted(v1, "price-list")), now, "test", "Adopted std.price-list v1"));
            db.DocumentTypeDefinitions.Add(definition);
            await db.SaveChangesAsync();

            (await TypeCatalogueSeeder.UpgradeAdoptedTypesAsync(db, now)).ShouldBe(1);
            await db.SaveChangesAsync();
            (await TypeCatalogueSeeder.UpgradeAdoptedTypesAsync(db, now)).ShouldBe(0, "a type on the newest version stays");
        });

        var moved = await host.WithDbAsync(db => db.DocumentTypeDefinitions.AsNoTracking().Include(d => d.Versions).SingleAsync(d => d.TenantId == PipelineTestHost.TenantA && d.Key == "price-list"));
        moved.PublishedVersion.ShouldBe(2);
        moved.BaseVersion.ShouldBe(2);
        moved.Versions.Single(v => v.Version == 1).State.ShouldBe(DocumentTypeVersionState.Deprecated, "documents pinned to it keep it");
        var published = DocProc.Definitions.DefinitionSerializer.Parse(moved.Versions.Single(v => v.Version == 2).DefinitionJson);
        published.Processing.Review.AutoApprove.ShouldBeFalse("an automatic upgrade never switches auto-approval on");
        published.Rules.Select(r => r.Id).ShouldContain("r_table");
        published.Base!.Version.ShouldBe(2);
    }

    [Fact]
    public void The_validate_stage_owns_the_table_flags_so_a_revalidation_clears_them()
    {
        // tableComplete raises TABLE_ROWS_REJECTED and TABLE_PAGE_NO_ROWS; a code the stage does not own would stay open
        // after a correction made the table complete (DocumentFlagWriter.Replace removes only owned codes).
        DocProc.Pipeline.Support.DocumentChecks.OwnedFlags.ShouldContain(FlagCodes.TableRowsRejected);
        DocProc.Pipeline.Support.DocumentChecks.OwnedFlags.ShouldContain(FlagCodes.TablePageNoRows);
        DocProc.Pipeline.Support.DocumentChecks.OwnedFlags.ShouldNotContain(FlagCodes.ExternalRejected, "a receiver's rejection outlives a revalidation");
    }

    [Fact]
    public async Task Next_months_list_auto_approves_against_the_previous_one_and_a_list_with_one_article_up_40_percent_goes_to_review_with_that_row()
    {
        var (host, _) = await StartAsync("std.price-list", "nordwind-price-list", PriceListRule, PriceListMapping());
        await using var _ = host;

        // January (the P2 list): every row read, nothing to compare with yet.
        var january = await SendAsync(host, DemoPriceList.FileName, File.ReadAllBytes(DemoFixtureTests.PriceListPath()));
        var januaryFlags = await OpenFlagsAsync(host, january.Id);
        january.Status.ShouldBe(DocumentStatus.AutoApproved, string.Join("; ", januaryFlags.Select(f => $"{f.Code} {f.Severity} {f.Message}")));
        january.LineCount.ShouldBe(DemoPriceList.RowCount);
        januaryFlags.ShouldContain(f => f.Code == FlagCodes.RuleFailed && f.Severity == FlagSeverity.Info && f.Params != null && f.Params.GetValueOrDefault("reason") == "noPrevious", "deltaToPrevious passes and says so");

        // February: 12 rows changed, all within 25 %; the printed count is read and equals the rows.
        var february = await SendAsync(host, DemoPriceLists.February.FileName, Upload(DemoPriceLists.February.FileName));
        var februaryFlags = await OpenFlagsAsync(host, february.Id);
        february.Status.ShouldBe(DocumentStatus.AutoApproved, string.Join("; ", februaryFlags.Select(f => $"{f.Code} {f.Severity} {f.Message}")));
        var tree = JsonNode.Parse(february.CanonicalJson!)!.AsObject();
        tree["declaredLineCount"]!["value"].ShouldNotBeNull(february.AppliedConfigJson + tree["declaredLineCount"]!.ToJsonString());
        tree["declaredLineCount"]!["value"]!.GetValue<int>().ShouldBe(DemoPriceList.RowCount);
        tree["lines"]!.AsArray().Count.ShouldBe(DemoPriceList.RowCount);
        februaryFlags.ShouldNotContain(f => f.Severity != FlagSeverity.Info, "a normal price change raises no warning");
        var changed = tree["lines"]!.AsArray().Select((r, i) => (Row: r!, Index: i))
            .Where(r => r.Row["price"]!["value"]!["amount"]!.GetValue<decimal>() != DemoPriceList.Articles[r.Index].Price).ToList();
        changed.Count.ShouldBe(DemoPriceLists.FebruaryChanges.Count);

        // March: one article up 40 % against February (the last approved list): review, with the row named for the grid.
        var march = await SendAsync(host, DemoPriceLists.March.FileName, Upload(DemoPriceLists.March.FileName));
        march.Status.ShouldBe(DocumentStatus.NeedsReview);
        var marchFlags = await OpenFlagsAsync(host, march.Id);
        var index = DemoPriceLists.MarchRaisedArticle - 1;
        var summary = marchFlags.Single(f => f.Params?.GetValueOrDefault("reason") == "deltaToPrevious");
        summary.Severity.ShouldBe(FlagSeverity.Warning);
        summary.Params!["ruleId"].ShouldBe("r_delta");
        summary.Params["rows"].ShouldBe(index.ToString(CultureInfo.InvariantCulture));
        summary.Params["previousDocumentId"].ShouldBe(february.Id.ToString());
        var row = marchFlags.Single(f => f.Params?.GetValueOrDefault("reason") == "deltaRow");
        row.Field.ShouldBe($"lines[{index.ToString(CultureInfo.InvariantCulture)}].price.amount");
        decimal.Parse(row.Params!["changePercent"], CultureInfo.InvariantCulture).ShouldBe(40m, 0.05m, "40 % on a price rounded to the cent");
        marchFlags.ShouldNotContain(f => f.Severity == FlagSeverity.Blocking, "a warning does not block a human approval");
        var task = await host.WithDbAsync(db => db.ReviewTasks.AsNoTracking().SingleAsync(t => t.DocumentId == march.Id));
        task.Kind.ShouldBe(ReviewTaskKind.Document);
        (await host.EventsAsync(march.Id)).ShouldContain(e => e.DetailsJson != null && e.DetailsJson.Contains("r_delta"), "the gate's hold names the rule (blockOnWarnings)");
        (await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().CountAsync())).ShouldBe(0, "no LLM was asked");
    }

    [Fact]
    public async Task A_specials_list_sorts_as_urgent_until_the_end_of_its_day_a_second_list_of_the_day_is_a_revision_and_yesterdays_is_expired()
    {
        // Warm-up 1: the first list under the layout goes to review, so its task can be seen.
        var (host, _) = await StartAsync("std.daily-specials", "nordwind-specials", SpecialsRule, SpecialsMapping(), warmup: 1);
        await using var _ = host;

        var first = await SendAsync(host, DemoPriceLists.Specials.FileName, Upload(DemoPriceLists.Specials.FileName));
        first.Status.ShouldBe(DocumentStatus.NeedsReview);
        first.ValidFrom.ShouldBe(new DateOnly(2026, 11, 5), first.AppliedConfigJson + " " + first.CanonicalJson?[..Math.Min(1500, first.CanonicalJson.Length)]);
        first.ValidUntil.ShouldBe(new DateOnly(2026, 11, 5));
        first.LineCount.ShouldBe(DemoPriceLists.Specials.Rows.Count);
        var tree = JsonNode.Parse(first.CanonicalJson!)!.AsObject();
        tree["listScope"]!["value"]!.GetValue<string>().ShouldBe("Changes");
        tree["listScope"]!["status"]!.GetValue<string>().ShouldBe("Inferred");
        tree["lines"]![3]!["previousPrice"]!["status"]!.GetValue<string>().ShouldBe("Missing", "a row without a printed old price");
        var task = await host.WithDbAsync(db => db.ReviewTasks.AsNoTracking().SingleAsync(t => t.DocumentId == first.Id));
        task.Priority.ShouldBe(ReviewPriority.Urgent);
        task.SlaDueAt.ShouldBe(new DateTimeOffset(2026, 11, 5, 22, 59, 59, TimeSpan.Zero), "23:59:59 in Berlin on its validUntil day");
        var firstFlags = await OpenFlagsAsync(host, first.Id);
        firstFlags.ShouldContain(f => f.Code == FlagCodes.LayoutWarmup);
        firstFlags.ShouldNotContain(f => f.Severity == FlagSeverity.Blocking, string.Join("; ", firstFlags.Select(f => $"{f.Code} {f.Message}")));

        // A second list on the same day: same supplier, validity, scope and (no) number, so a revision of the first.
        var second = await SendAsync(host, DemoPriceLists.SpecialsSecond.FileName, Upload(DemoPriceLists.SpecialsSecond.FileName));
        second.Status.ShouldBe(DocumentStatus.NeedsReview);
        var revision = (await OpenFlagsAsync(host, second.Id)).Single(f => f.Code == FlagCodes.RevisionOf);
        revision.Severity.ShouldBe(FlagSeverity.Warning);
        revision.Params!["documentId"].ShouldBe(first.Id.ToString());

        // Printed for yesterday: blocked as expired.
        var dated = await SendAsync(host, DemoPriceLists.SpecialsDated.FileName, Upload(DemoPriceLists.SpecialsDated.FileName));
        dated.Status.ShouldBe(DocumentStatus.NeedsReview);
        dated.ValidUntil.ShouldBe(new DateOnly(2026, 11, 4), "validUntil defaults to the printed validFrom");
        var expired = (await OpenFlagsAsync(host, dated.Id)).Where(f => f.Params?.GetValueOrDefault("ruleId") == "r_not_expired").ToList();
        expired.ShouldNotBeEmpty();
        expired.ShouldAllBe(f => f.Severity == FlagSeverity.Blocking);
        var datedTask = await host.WithDbAsync(db => db.ReviewTasks.AsNoTracking().SingleAsync(t => t.DocumentId == dated.Id));
        datedTask.SlaDueAt.ShouldBe(new DateTimeOffset(2026, 11, 4, 22, 59, 59, TimeSpan.Zero), "its day has ended; it still sorts by it");
    }
}
