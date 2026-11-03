using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Outputs;
using DocProc.Application.Pipeline;
using DocProc.Infrastructure.Outputs;

namespace DocProc.Integration.Tests.Outputs;

/// <summary>
/// The platform output templates of docs/11 §13.9: the copy the migration froze equals what this build ships at
/// version 1, every template parses and fits its own target schema, <c>std.event</c> renders today's thin event, and
/// <c>generic-ap-json</c> writes a credit note's amounts negative.
/// </summary>
public sealed class OutputTemplateTests(RendererFixture fixture) : IClassFixture<RendererFixture>
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_shipped_templates_are_std_event_and_generic_ap_json_and_version_1_is_the_frozen_copy()
    {
        OutputTemplates.All.Select(t => t.Key).ShouldBe(["generic-ap-json", "std.event"]);
        var frozen = OutputTemplates.Frozen().ToDictionary(t => t.Key);
        foreach (var template in OutputTemplates.All.Where(t => t.Version == 1))
        {
            frozen[template.Key].ShouldBe(template, "a published template version never changes; ship version 2 instead");
        }

        OutputTemplates.All.ShouldAllBe(t => t.Engine == "jsonata" && t.ContentType == "application/json" && t.TargetSchemaJson != null);
        OutputTemplates.PlatformMappingId("std.event").ShouldNotBe(OutputTemplates.PlatformMappingId("generic-ap-json"));
    }

    [Theory]
    [InlineData("std.event")]
    [InlineData("generic-ap-json")]
    public async Task Every_template_parses_with_its_target_schema(string key)
    {
        var template = OutputTemplates.Find(key)!;

        var check = await fixture.Renderer.CheckAsync(template.Expression, JsonNode.Parse(template.TargetSchemaJson!)!.AsObject(), TestContext.Current.CancellationToken);

        check.Error.ShouldBeNull();
        check.ReferencedPaths.ShouldContain("meta.documentId");
    }

    [Fact]
    public async Task Std_event_renders_the_thin_document_event_of_the_outbox()
    {
        var tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var document = Guid.Parse("6c1f0000-0000-0000-0000-000000000001");
        var at = new DateTimeOffset(2026, 10, 29, 9, 12, 0, TimeSpan.Zero);
        var view = DocumentDataView.BuildWithoutDefinition(new DocumentDataContext(new DocumentDataMeta(document, 7, "Invoice", 1, "Approved")
        {
            TenantId = tenant,
            Event = "document.approved",
            EventAt = at,
        }));
        var template = OutputTemplates.Find("std.event")!;

        var result = await fixture.Renderer.RenderAsync(new RenderRequest(template.Expression, view, Today) { TargetSchema = JsonNode.Parse(template.TargetSchemaJson!)!.AsObject() }, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        var thin = JsonSerializer.SerializeToNode(new DocumentEventMessage(tenant, document, "document.approved", at), Web)!;
        result.Output!["tenantId"]!.GetValue<string>().ShouldBe(thin["tenantId"]!.GetValue<string>());
        result.Output["documentId"]!.GetValue<string>().ShouldBe(thin["documentId"]!.GetValue<string>());
        result.Output["eventType"]!.GetValue<string>().ShouldBe(thin["eventType"]!.GetValue<string>());
        DateTimeOffset.Parse(result.Output["at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(at);
        result.Output.AsObject().Select(p => p.Key).ShouldBe(["tenantId", "documentId", "eventType", "at"]);
    }

    [Fact]
    public async Task Generic_ap_json_writes_a_credit_note_negative_and_an_invoice_positive()
    {
        var template = OutputTemplates.Find("generic-ap-json")!;
        JsonObject View(string type, decimal gross) => new()
        {
            ["roles"] = new JsonObject
            {
                ["documentNumber"] = "GS-1",
                ["currency"] = "EUR",
                ["total"] = new JsonObject { ["amount"] = gross, ["currency"] = "EUR" },
                ["lines"] = new JsonArray(new JsonObject { ["sku"] = "4711", ["quantity"] = -2, ["net"] = new JsonObject { ["amount"] = gross } }),
            },
            ["meta"] = new JsonObject { ["documentId"] = Guid.NewGuid().ToString(), ["version"] = 3, ["typeKey"] = type, ["event"] = "document.approved" },
            ["supplier"] = null,
            ["entity"] = null,
        };

        foreach (var (type, gross, expected) in new[] { ("CreditNote", -119.00m, -119m), ("CreditNote", 119.00m, -119m), ("Invoice", 119.00m, 119m) })
        {
            var result = await fixture.Renderer.RenderAsync(new RenderRequest(template.Expression, View(type, gross), Today) { TargetSchema = JsonNode.Parse(template.TargetSchemaJson!)!.AsObject() }, TestContext.Current.CancellationToken);

            result.Succeeded.ShouldBeTrue(string.Join("; ", result.SchemaErrors.Select(e => e.Message)) + result.Error?.Message);
            result.Output!["header"]!["gross"]!.GetValue<decimal>().ShouldBe(expected);
            result.Output["rows"]![0]!["net"]!.GetValue<decimal>().ShouldBe(expected);
            result.Output["rows"]![0]!["quantity"]!.GetValue<decimal>().ShouldBe(2m);
            result.Output["header"]!.AsObject().ContainsKey("supplier").ShouldBeFalse("no supplier, no block");
        }
    }
}
