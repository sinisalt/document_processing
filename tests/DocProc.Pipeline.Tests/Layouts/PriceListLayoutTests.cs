using System.Globalization;
using System.Text.Json.Nodes;
using DocProc.Application.Routing;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Layouts;
using DocProc.Pipeline.Tests.Demo;
using DocProc.Pipeline.Tests.Pdf;
using DocProc.Pipeline.Tests.Routing;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Tests.Layouts;

/// <summary>
/// A price-list layout on the committed 14-page Nordwind price list (docs/11 §5.8, §8.6): the split prefilter keeps the
/// routed item whole because the layout holds on it and price lists are never split, <c>classify.identification</c>
/// types it, and the layout's one <c>column-table</c> reads all 412 rows over 14 pages (repeated headers, group headings,
/// continuation lines across page breaks, tier columns) with the table report stored as the layout's; no LLM is asked.
/// </summary>
public sealed class PriceListLayoutTests
{
    private const string Sender = "preise@nordwind.example";

    private static string U(double points) => Math.Round(points * 1000 / DemoNordwindInvoices.PageWidth).ToString(CultureInfo.InvariantCulture);

    private static string Mapping() => $$"""
        { "locale": "de-DE",
          "fields": {
            "priceListNumber": { "rules": [ { "strategy": "label-right", "labels": ["Listen-Nr."], "page": 1 } ] },
            "validFrom": { "rules": [ { "strategy": "regex-anywhere", "pattern": "gültig ab\\s+(\\d{2}\\.\\d{2}\\.\\d{4})", "group": 1, "page": 1 } ] },
            "currency": { "rules": [ { "strategy": "regex-anywhere", "pattern": "Alle Preise in ([A-Z]{3})", "group": 1, "page": 1 } ] },
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

    [Fact]
    public async Task The_split_prefilter_keeps_a_price_list_whole_and_its_layout_reads_all_412_rows_with_a_table_report()
    {
        await using var host = await PipelineTestHost.StartAsync();
        await host.WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA);
            tenant.Settings.Features!.SourceRouting = true;
            tenant.Settings.Features!.SupplierLayouts = true;
            tenant.Settings.Features!.ConfigurableTypes = true;
            await db.SaveChangesAsync();
            var adopted = await new DocumentTypeCatalogue(db, TimeProvider.System).AdoptAsync(PipelineTestHost.TenantA, "std.price-list", null, "tenant-a-admin", TestContext.Current.CancellationToken);
            adopted.Status.ShouldBe(CatalogueStatus.Created, adopted.Detail);
        });
        var typeKey = await host.WithDbAsync(db => db.DocumentTypeDefinitions.AsNoTracking().Where(d => d.TenantId == PipelineTestHost.TenantA && d.Key.StartsWith("price")).Select(d => d.Key).SingleAsync());
        var nordwind = await host.AddSupplierAsync(PipelineTestHost.TenantA, DemoPriceList.SupplierName, vatId: DemoPriceList.SupplierVatId);
        await SourceRoutingPipelineTests.AddRuleAsync(host, nordwind.Id, r => r.Sender = Sender);
        var (_, version) = await NordwindLayouts.AddAsync(
            host,
            nordwind.Id,
            "nordwind-price-list",
            """{ "all": [ { "text": "Preisliste", "page": 1, "region": "title" }, { "regex": "gültig ab\\s+\\d{2}\\.\\d{2}\\.\\d{4}", "page": 1 } ] }""",
            Mapping(),
            type: typeKey);

        var bytes = await File.ReadAllBytesAsync(DemoFixtureTests.PriceListPath(), TestContext.Current.CancellationToken);
        var ingestion = await SourceRoutingPipelineTests.IngestAsync(
            host,
            [new IngestFile(DemoPriceList.FileName, "application/pdf", bytes)],
            new SourceMetadata(Sender: Sender) { SenderAuth = new SenderAuthentication { Dmarc = "pass", FromDomain = "nordwind.example" } });
        await host.RunUntilIdleAsync(TimeSpan.FromSeconds(90));

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.PageCount.ShouldBe(DemoPriceList.PageCount, "the split prefilter kept the 14 pages together");
        var events = await host.EventsAsync(document.Id);
        events.Single(e => e.ToStatus == DocumentStatus.Split).DetailsJson!.ShouldContain("split prefilter");
        document.Type.Value.ShouldBe(typeKey);
        document.TypeSource.ShouldBe(TypeSource.Identification);
        document.SupplierLayoutVersionId.ShouldBe(version.Id);
        document.ExtractionMethod.ShouldBe(ExtractionMethod.Profile);
        (await host.WithDbAsync(db => db.LlmUsage.AsNoTracking().CountAsync())).ShouldBe(0, "neither the splitter, the classifier nor the extractor asked the LLM");

        var tree = JsonNode.Parse(document.CanonicalJson!)!.AsObject();
        tree["validFrom"]!["value"]!.GetValue<string>().ShouldBe(DemoPriceList.ValidFrom);
        tree["priceListNumber"]!["value"]!["verbatim"]!.GetValue<string>().ShouldBe("PL-2027-01");
        var rows = tree["lines"]!.AsArray();
        var reportJson = await host.WithDbAsync(db => db.DocumentTableReports.AsNoTracking().Where(r => r.DocumentId == document.Id).Select(r => r.ReportJson).SingleOrDefaultAsync());
        rows.Count.ShouldBe(DemoPriceList.RowCount, reportJson);
        for (var i = 0; i < DemoPriceList.RowCount; i++)
        {
            var expected = DemoPriceList.Articles[i];
            var row = rows[i]!.AsObject();
            row["supplierSku"]!["value"]!["verbatim"]!.GetValue<string>().ShouldBe(expected.Sku, $"row {i + 1}");
            row["description"]!["value"]!.GetValue<string>().ShouldBe(DemoPriceList.FullDescription(expected), $"row {i + 1}");
            row["price"]!["value"]!["amount"]!.GetValue<decimal>().ShouldBe(expected.Price, $"row {i + 1}");
            row["category"]!["value"]!.GetValue<string>().ShouldBe(expected.Group, $"row {i + 1}");
            var tiers = row["tiers"]!.AsArray();
            tiers.Count.ShouldBe((expected.Tier10 is null ? 0 : 1) + (expected.Tier50 is null ? 0 : 1), $"row {i + 1}");
        }

        document.LineCount.ShouldBe(DemoPriceList.RowCount);
        var report = await host.WithDbAsync(db => db.DocumentTableReports.AsNoTracking().SingleAsync(r => r.DocumentId == document.Id));
        report.Origin.ShouldBe(TableReportOrigin.Layout);
        report.StepId.ShouldBe(LayoutExtractor.StepId);
        var stored = JsonNode.Parse(report.ReportJson)!;
        stored["rows"]!.GetValue<int>().ShouldBe(DemoPriceList.RowCount);
        stored["rejectedRows"]!.GetValue<int>().ShouldBe(0);
        stored["perPage"]!.AsArray().Count.ShouldBe(DemoPriceList.PageCount);
        document.Status.ShouldBe(DocumentStatus.NeedsReview, "std.price-list v1 is always reviewed");
    }
}
