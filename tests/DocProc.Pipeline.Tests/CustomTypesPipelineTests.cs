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
/// A tenant's own type end to end (docs/11 §9.3, §9.4, §11; P5 with <c>customTypes</c>): classified by the tenant
/// keyword set built from its definition, extracted by <c>LlmDefinitionExtractor</c> against its compiled schema with a
/// recorded answer, checked by the P5 rule kinds with a definition flag, and auto-approved because
/// <c>review.requirePassedRules</c> names the rules that vouch for it; held when one of them fails or a
/// <c>blockOnWarnings</c> rule warns. Without an LLM provider (the compose default) it goes on as before P5: the empty
/// tree of the type for the reviewer.
/// </summary>
public sealed class CustomTypesPipelineTests
{
    private const string PdfMime = "application/pdf";

    internal const string ReturnSlipDefinition = """
        {"key":"return-slip","version":1,"name":{"en":"Return slip","de":"Retourenschein"},
         "description":"Goods returned to a supplier.",
         "roles":{"documentNumber":"returnNumber","documentDate":"returnDate","lineItems":"items"},
         "fields":[
           {"id":"fld_rn","key":"returnNumber","kind":"identifier","required":true,"indexed":true},
           {"id":"fld_rd","key":"returnDate","kind":"date","required":true},
           {"id":"fld_items","key":"items","kind":"array","anchor":"description","item":{"fields":[
             {"id":"fld_i_desc","key":"description","kind":"text","required":true},
             {"id":"fld_i_qty","key":"quantity","kind":"decimal"},
             {"id":"fld_i_reason","key":"reason","kind":"enum","options":[{"code":"Damaged"},{"code":"Wrong"},{"code":"Surplus"}]}]}}
         ],
         "flags":[{"code":"X_RETURN_IN_FUTURE","severity":"warning","message":{"en":"The return date lies after the end of the year.","de":"Das Rückgabedatum liegt nach dem Jahresende."}}],
         "rules":[
           {"id":"r_qty","kind":"compare","params":{"left":"items[*].quantity","op":"gt","value":0}},
           {"id":"r_rows","kind":"expression","params":{"predicate":"$count(items) > 0 and $count(items[$not($exists(reason))]) = 0"}},
           {"id":"r_late","kind":"compare","flag":"X_RETURN_IN_FUTURE","params":{"left":"returnDate","op":"le","value":"2026-12-31"}}
         ],
         "processing":{"review":{"autoApprove":true,"requirePassedRules":["r_qty","r_rows"],"blockOnWarnings":["r_late"]}},
         "classification":{"description":"Goods returned to a supplier, with the returned articles and the reason.","keywords":{"de":["Retourenschein"]},"labels":{"de":["Retouren-Nr."]}},
         "llm":{"guidance":"Reasons: beschädigt is Damaged, falsch geliefert is Wrong."}}
        """;

    private static string Answer(decimal secondQuantity = 5, string returnDate = "2026-10-27") => $$"""
        {
          "returnNumber": "RMA-4711", "returnDate": "{{returnDate}}",
          "items": [
            { "description": "Schraube M8", "quantity": 10, "reason": "Damaged" },
            { "description": "Mutter M8", "quantity": {{secondQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "reason": "Wrong" }
          ],
          "unreadableRegions": []
        }
        """;

    private static async Task<PipelineTestHost> StartAsync(bool customTypes = true)
    {
        var host = await PipelineTestHost.StartAsync();
        await host.WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == PipelineTestHost.TenantA);
            tenant.Settings.Features!.ConfigurableTypes = true;
            tenant.Settings.Features.CustomTypes = customTypes;
            await db.SaveChangesAsync();
        });
        await host.AddSupplierAsync(PipelineTestHost.TenantA, "Musterfirma GmbH");
        return host;
    }

    internal static async Task CreateTypeAsync(PipelineTestHost host, string json)
    {
        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var catalogue = scope.ServiceProvider.GetRequiredService<DocumentTypeCatalogue>();
        var created = await catalogue.CreateAsync(PipelineTestHost.TenantA, json, "admin", CancellationToken.None);
        created.Status.ShouldBe(CatalogueStatus.Created, created.Detail + string.Join("; ", created.Problems?.Select(p => p.ToString()) ?? []));
        var key = created.Value!.Definition.Key;
        var published = await catalogue.PublishAsync(PipelineTestHost.TenantA, key, 1, null, "test", "admin", CancellationToken.None);
        published.Status.ShouldBe(CatalogueStatus.Ok, published.Detail + string.Join("; ", published.Report?.Problems.Select(p => p.ToString()) ?? []));
    }

    private static async Task<Document> RunAsync(PipelineTestHost host)
    {
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("retoure.pdf", PdfMime, TestPdfs.ReturnSlip())]);
        await host.RunUntilIdleAsync();
        return await host.SingleDocumentAsync(ingestion.Id);
    }

    [Fact]
    public async Task A_custom_type_is_classified_by_its_keywords_extracted_by_the_llm_and_auto_approved_by_its_named_rules()
    {
        await using var host = await StartAsync();
        await host.AddReplayProviderAsync();
        await CreateTypeAsync(host, ReturnSlipDefinition);
        host.Llm.Enqueue(Answer(), usage: new LlmUsage(500, 80));

        var document = await RunAsync(host);

        document.FailureReason.ShouldBeNull();
        document.Type.Value.ShouldBe("return-slip");
        document.TypeSource.ShouldBe(TypeSource.Rule, "the tenant keyword set holds the type's title");
        var request = host.Llm.Requests.ShouldHaveSingleItem("the keyword rules decided; only the extraction asked the LLM");
        request.Stage.ShouldBe(LlmStage.Extract);
        request.Prompt.Id.ShouldBe("extract-document");
        request.OutputSchema!.Name.ShouldBe("doc_return-slip");
        request.Prompt.Task.ShouldContain("beschädigt is Damaged");

        document.ExtractionMethod.ShouldBe(ExtractionMethod.Llm);
        document.CanonicalSchemaVersion.ShouldBe(DefinitionTree.SchemaVersion);
        document.DocumentNumber.ShouldBe("RMA-4711");
        document.LineCount.ShouldBe(2);
        var tree = JsonNode.Parse(document.CanonicalJson!)!.AsObject();
        tree["items"]![1]!["reason"]!["value"]!.GetValue<string>().ShouldBe("Wrong");
        tree["returnDate"]!["source"]!["method"]!.GetValue<string>().ShouldBe("Llm");
        JsonNode.Parse(document.AppliedConfigJson!)!.ToJsonString().ShouldContain("doc_return-slip");
        document.Costs.LlmInputTokens.ShouldBeGreaterThan(0);

        document.Flags.Where(f => f.ResolvedAt is null && f.Severity != FlagSeverity.Info).ShouldBeEmpty();
        document.Status.ShouldBe(DocumentStatus.AutoApproved);
    }

    [Fact]
    public async Task A_definition_flag_of_a_blockOnWarnings_rule_holds_the_document_in_review()
    {
        await using var host = await StartAsync();
        await host.AddReplayProviderAsync();
        await CreateTypeAsync(host, ReturnSlipDefinition);
        host.Llm.Enqueue(Answer(returnDate: "2027-01-15"));

        var document = await RunAsync(host);

        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        var flag = document.Flags.Single(f => f.Code == "X_RETURN_IN_FUTURE");
        flag.Severity.ShouldBe(FlagSeverity.Warning);
        flag.Message.ShouldBe("The return date lies after the end of the year.");
        flag.Params!["message.de"].ShouldBe("Das Rückgabedatum liegt nach dem Jahresende.");
        flag.Params["ruleId"].ShouldBe("r_late");
        flag.Field.ShouldBe("returnDate");
        (await GateAsync(host, document.Id)).ShouldContain("RULE_WARNING_BLOCKS");
    }

    [Fact]
    public async Task A_required_rule_that_fails_holds_the_document_in_review()
    {
        await using var host = await StartAsync();
        await host.AddReplayProviderAsync();
        await CreateTypeAsync(host, ReturnSlipDefinition);
        host.Llm.Enqueue(Answer(secondQuantity: 0));

        var document = await RunAsync(host);

        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        var flag = document.Flags.Single(f => f.Code == FlagCodes.RuleFailed);
        flag.Field.ShouldBe("items[1].quantity");
        flag.Params!["ruleId"].ShouldBe("r_qty");
        // A warning alone does not stop auto-approval; the gate does, because r_qty is a rule the type requires to pass.
        flag.Severity.ShouldBe(FlagSeverity.Warning);
        (await GateAsync(host, document.Id)).ShouldContain("REQUIRED_RULE_NOT_PASSED");
    }

    [Fact]
    public async Task Without_an_llm_provider_the_custom_type_goes_on_as_before_with_its_empty_tree()
    {
        await using var host = await StartAsync();
        await CreateTypeAsync(host, ReturnSlipDefinition);

        var document = await RunAsync(host);

        document.FailureReason.ShouldBeNull();
        document.Type.Value.ShouldBe("return-slip");
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        document.ExtractionMethod.ShouldBeNull();
        var tree = JsonNode.Parse(document.CanonicalJson!)!.AsObject();
        tree["returnNumber"]!["status"]!.GetValue<string>().ShouldBe("Missing");
        document.Flags.ShouldContain(f => f.Code == FlagCodes.UnsupportedType && f.Params!["reason"] == "definition");
        host.Llm.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_custom_types_the_keyword_set_is_the_built_in_one()
    {
        await using var host = await StartAsync(customTypes: false);

        var document = await RunAsync(host);

        document.Type.Value.ShouldNotBe("return-slip", "without customTypes the built-in keyword table classifies, which has no return slip");
    }

    /// <summary>The review gate's rule conditions the Validated event recorded (<c>gate</c>), as JSON text.</summary>
    private static async Task<string> GateAsync(PipelineTestHost host, Guid documentId)
    {
        var validated = (await host.EventsAsync(documentId)).Last(e => e.ToStatus == DocumentStatus.Validated);
        return JsonNode.Parse(validated.DetailsJson!)!["gate"]?.ToJsonString() ?? string.Empty;
    }
}
