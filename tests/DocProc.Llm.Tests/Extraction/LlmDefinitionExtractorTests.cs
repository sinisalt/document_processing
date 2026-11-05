using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Definitions;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Llm.Extraction;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests.Extraction;

/// <summary>
/// <c>LlmDefinitionExtractor</c> and <c>DefinitionMapper</c> (docs/11 §9.3 "Other types"): a tenant's own type is asked
/// for with its compiled schema (<c>doc_&lt;key&gt;</c>) and the generic prompt, and the answer becomes the type's tree:
/// envelopes by kind with LLM provenance located in the text layer, rows of tables, groups, lists, money with the
/// currency of its <c>currencyFrom</c> field, nothing for a null answer or a value that does not fit its kind.
/// </summary>
public sealed class LlmDefinitionExtractorTests
{
    private static readonly PromptCatalogue Prompts = PromptCatalogue.Load();

    /// <summary>The return slip of docs/11 §5.4, with a money field, a group, a list and an enum.</summary>
    internal static TypeDefinition ReturnSlip() => DefinitionSerializer.Parse("""
        {"key":"return-slip","version":1,"base":{"template":"std.generic","version":1,"mode":"copy"},
         "name":{"en":"Return slip","de":"Retourenschein"},
         "roles":{"documentNumber":"returnNumber","documentDate":"returnDate","supplierParty":"supplier","lineItems":"items"},
         "fields":[
           {"id":"fld_rn","key":"returnNumber","kind":"identifier","required":true,"description":"The RMA number."},
           {"id":"fld_rd","key":"returnDate","kind":"date","required":true},
           {"id":"fld_cur","key":"currency","kind":"currency"},
           {"id":"fld_credit","key":"creditAmount","kind":"money","currencyFrom":"currency"},
           {"id":"fld_sup","key":"supplier","kind":"party"},
           {"id":"fld_refs","key":"deliveryNotes","kind":"identifier","list":true},
           {"id":"fld_pick","key":"pickup","kind":"object","fields":[
             {"id":"fld_pick_date","key":"date","kind":"date"},
             {"id":"fld_pick_carrier","key":"carrier","kind":"string"}]},
           {"id":"fld_note","key":"internalNote","kind":"text","readOnly":true},
           {"id":"fld_items","key":"items","kind":"array","anchor":"description","item":{"fields":[
             {"id":"fld_i_sku","key":"supplierSku","kind":"identifier"},
             {"id":"fld_i_desc","key":"description","kind":"text","required":true},
             {"id":"fld_i_qty","key":"quantity","kind":"decimal"},
             {"id":"fld_i_reason","key":"reason","kind":"enum","options":[{"code":"Damaged"},{"code":"Wrong"},{"code":"Surplus"}]}]}}
         ],
         "llm":{"guidance":"Quantities on return slips are positive."},
         "processing":{"review":{"autoApprove":false}}}
        """);

    internal const string Answer = """
        {
          "returnNumber": "RMA-4711", "returnDate": "2026-10-27", "currency": "EUR", "creditAmount": 150.0,
          "supplier": { "name": "Musterhandel GmbH", "vatId": "DE000000000", "taxId": null, "registrationNumber": null,
                        "addressLine1": null, "addressLine2": null, "postalCode": null, "city": null, "countryCode": null,
                        "email": null, "phone": null, "iban": null, "bic": null },
          "deliveryNotes": ["PO-77"],
          "pickup": { "date": "2026-10-30", "carrier": null },
          "items": [
            { "supplierSku": "123", "description": "Strauchtomaten", "quantity": 10, "reason": "Damaged" },
            { "supplierSku": null, "description": "Gurken", "quantity": "viele", "reason": null }
          ],
          "unreadableRegions": []
        }
        """;

    private static (LlmDefinitionExtractor Extractor, FakeGateway Gateway) Create(string? answer = Answer)
    {
        var gateway = new FakeGateway();
        if (answer is not null)
        {
            gateway.Answer(answer);
        }

        return (new LlmDefinitionExtractor(gateway, Prompts, PriceTable.Default, new LlmExtractionOptions()), gateway);
    }

    private static DefinitionExtractionRequest Request(IReadOnlyList<string>? fill = null)
    {
        var compiled = DefinitionSchemaCompiler.Compile(ReturnSlip()).Compiled!;
        var input = Inputs.Extraction(DocumentTypeKey.Parse("return-slip")) with
        {
            Definition = new ResolvedDefinition("return-slip", Guid.NewGuid(), 1, compiled.LlmSchema, []),
        };
        return new DefinitionExtractionRequest(input, compiled, "Return slip", "Quantities on return slips are positive.", fill);
    }

    [Fact]
    public async Task The_request_carries_the_compiled_schema_and_the_generic_prompt()
    {
        var (extractor, gateway) = Create();

        await extractor.ExtractAsync(Request(), CancellationToken.None);

        var request = gateway.Requests.ShouldHaveSingleItem();
        request.Stage.ShouldBe(LlmStage.Extract);
        request.Prompt.Id.ShouldBe(PromptIds.ExtractDocument);
        request.Prompt.System.ShouldContain("the schema given to you");
        request.OutputSchema.ShouldNotBeNull().Name.ShouldBe("doc_return-slip");
        SchemaPortability.Check(request.OutputSchema.Schema).ShouldBeEmpty();
        var schema = request.OutputSchema.Schema.ToJsonString();
        schema.ShouldContain("The RMA number.");
        schema.ShouldNotContain("internalNote", Case.Sensitive, "a read-only field is not asked for");
        request.Prompt.Task.ShouldContain("(a Return slip) into the doc_return-slip schema");
        request.Prompt.Task.ShouldContain("Quantities on return slips are positive.");
        request.Prompt.Task.ShouldContain("Gasthaus zur Post GmbH");
        request.Prompt.Task.ShouldNotContain("Fields to fill");
    }

    [Fact]
    public async Task Fill_missing_names_the_paths_still_to_fill()
    {
        var (extractor, gateway) = Create();

        await extractor.ExtractAsync(Request(["returnNumber", "pickup.date"]), CancellationToken.None);

        var task = gateway.Requests.Single().Prompt.Task;
        task.ShouldContain("## Fields to fill");
        task.ShouldContain("- returnNumber");
        task.ShouldContain("- pickup.date");
    }

    [Fact]
    public async Task The_answer_becomes_the_tree_of_the_type()
    {
        var (extractor, _) = Create();

        var outcome = await extractor.ExtractAsync(Request(), CancellationToken.None);

        outcome.Method.ShouldBe(ExtractionMethod.Llm);
        outcome.Cost.LlmInputTokens.ShouldBeGreaterThan(0);
        var tree = outcome.Document.Root;
        var number = tree["returnNumber"]!.AsObject();
        number["status"]!.GetValue<string>().ShouldBe(nameof(FieldStatus.Extracted));
        number["value"]!["verbatim"]!.GetValue<string>().ShouldBe("RMA-4711");
        number["source"]!["method"]!.GetValue<string>().ShouldBe("Llm");
        tree["returnDate"]!["value"]!.GetValue<string>().ShouldBe("2026-10-27");
        tree["creditAmount"]!["value"]!["amount"]!.GetValue<decimal>().ShouldBe(150m);
        tree["creditAmount"]!["value"]!["currency"]!.GetValue<string>().ShouldBe("EUR");
        tree["supplier"]!["value"]!["name"]!.GetValue<string>().ShouldBe("Musterhandel GmbH");
        tree["supplier"]!["source"]!["page"]!.GetValue<int>().ShouldBe(1, "located in the text layer");
        tree["deliveryNotes"]!.AsArray().ShouldHaveSingleItem()!["value"]!["verbatim"]!.GetValue<string>().ShouldBe("PO-77");
        tree["pickup"]!["date"]!["value"]!.GetValue<string>().ShouldBe("2026-10-30");
        tree["pickup"]!.AsObject().ContainsKey("carrier").ShouldBeFalse("a null answer leaves the field to the Extract stage, which completes it Missing");
        tree.ContainsKey("internalNote").ShouldBeFalse();
        tree.ContainsKey("unreadableRegions").ShouldBeFalse();

        var rows = tree["items"]!.AsArray();
        rows.Count.ShouldBe(2);
        rows[0]!["description"]!["value"]!.GetValue<string>().ShouldBe("Strauchtomaten");
        rows[0]!["quantity"]!["value"]!.GetValue<decimal>().ShouldBe(10m);
        rows[0]!["reason"]!["value"]!.GetValue<string>().ShouldBe("Damaged");
        rows[1]!.AsObject().ContainsKey("quantity").ShouldBeFalse("'viele' is not a decimal");
    }

    [Fact]
    public async Task A_refusal_or_an_answer_that_is_not_the_schema_is_not_mapped()
    {
        var refusingGateway = new FakeGateway().Answer("{}", stopReason: StopReasons.Refusal);
        var extractor = new LlmDefinitionExtractor(refusingGateway, Prompts, PriceTable.Default, new LlmExtractionOptions());
        (await Should.ThrowAsync<LlmException>(() => extractor.ExtractAsync(Request(), CancellationToken.None))).Kind.ShouldBe(LlmErrorKind.ContentRefused);

        var unavailable = new FakeGateway().Fail(new LlmException(LlmErrorKind.Unavailable, "no provider"));
        var none = new LlmDefinitionExtractor(unavailable, Prompts, PriceTable.Default, new LlmExtractionOptions());
        (await Should.ThrowAsync<LlmException>(() => none.ExtractAsync(Request(), CancellationToken.None))).IsRetryable.ShouldBeFalse();
    }

    [Fact]
    public void The_mapper_takes_a_row_currency_from_its_row_and_falls_back_to_the_tenant()
    {
        var definition = DefinitionSerializer.Parse("""
            {"key":"price-sheet","version":1,"name":{"en":"Price sheet"},
             "fields":[
               {"id":"fld_rows","key":"rows","kind":"array","item":{"fields":[
                 {"id":"fld_r_cur","key":"currency","kind":"currency"},
                 {"id":"fld_r_price","key":"price","kind":"money","currencyFrom":"rows[*].currency"}]}},
               {"id":"fld_total","key":"total","kind":"money"}
             ]}
            """);
        var compiled = DefinitionSchemaCompiler.Compile(definition).Compiled.ShouldNotBeNull();
        var answer = JsonNode.Parse("""{ "rows": [ { "currency": "chf", "price": 2.5 }, { "currency": null, "price": 3 } ], "total": 5.5, "unreadableRegions": [] }""")!.AsObject();

        var tree = DefinitionMapper.Map(answer, compiled.Descriptors, ProvenanceLocator.Empty, "EUR");

        tree["rows"]![0]!["price"]!["value"]!["currency"]!.GetValue<string>().ShouldBe("CHF");
        tree["rows"]![1]!["price"]!["value"]!["currency"]!.GetValue<string>().ShouldBe("EUR");
        tree["total"]!["value"]!["currency"]!.GetValue<string>().ShouldBe("EUR");
    }
    [Fact]
    public async Task Exclude_leaves_the_named_fields_out_of_the_schema_under_a_version_of_its_own()
    {
        // docs/11 §5.8: a price list's llm step excludes lines, which the layout's table reads.
        var (extractor, gateway) = Create();

        var outcome = await extractor.ExtractAsync(Request() with { Exclude = ["items", "pickup"] }, CancellationToken.None);

        var schema = gateway.Requests.Single().OutputSchema!;
        schema.Schema["properties"]!.AsObject().ContainsKey("items").ShouldBeFalse();
        schema.Schema["properties"]!.AsObject().ContainsKey("pickup").ShouldBeFalse();
        schema.Schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldNotContain("items");
        schema.Version.ShouldContain("+x.");
        SchemaPortability.Check(schema.Schema).ShouldBeEmpty();
        outcome.Document.Root.ContainsKey("items").ShouldBeFalse("an answer's excluded fields are not mapped");
        outcome.Document.Root["returnNumber"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Lines_per_page_reads_the_header_once_and_the_rows_page_by_page_in_order()
    {
        var gateway = new FakeGateway()
            .Answer("""{ "returnNumber": "RMA-4711", "returnDate": "2026-10-27", "currency": "EUR", "creditAmount": null, "supplier": null, "deliveryNotes": [], "pickup": null, "unreadableRegions": [] }""")
            .Answer("""{ "items": [ { "supplierSku": "1", "description": "Strauchtomaten", "quantity": 10, "reason": "Damaged" } ], "unreadableRegions": [] }""")
            .Answer("""{ "items": [ { "supplierSku": "2", "description": "Gurken", "quantity": 4, "reason": null }, { "supplierSku": "3", "description": "Paprika", "quantity": 1, "reason": "Wrong" } ], "unreadableRegions": [] }""");
        var extractor = new LlmDefinitionExtractor(gateway, Prompts, PriceTable.Default, new LlmExtractionOptions());
        var request = Request() with { LinesPerPage = true, Input = Request().Input with { Pdf = null, PageImages = [(ReadOnlyMemory<byte>)Inputs.PngBytes, (ReadOnlyMemory<byte>)Inputs.PngBytes] } };

        var outcome = await extractor.ExtractAsync(request, CancellationToken.None);

        gateway.Requests.Count.ShouldBe(3, "one header call and one call per page");
        gateway.Requests[0].OutputSchema!.Schema["properties"]!.AsObject().ContainsKey("items").ShouldBeFalse("the header call does not ask for the rows");
        gateway.Requests[1].OutputSchema!.Schema["properties"]!.AsObject().Select(p => p.Key).ShouldBe(["items", "unreadableRegions"], ignoreOrder: true);
        gateway.Requests[1].Document!.PageImages.Count.ShouldBe(1, "a page call sends that page only");
        gateway.Requests[2].Prompt.Task.ShouldContain("page 2 of 2");
        var rows = outcome.Document.Root["items"]!.AsArray();
        rows.Select(r => r!["supplierSku"]!["value"]!["verbatim"]!.GetValue<string>()).ShouldBe(["1", "2", "3"]);
        rows[0]!["description"]!["source"]!["method"]!.GetValue<string>().ShouldBe("Llm", "rows the LLM read never auto-approve a price list (review.lines)");
        outcome.Document.Root["returnNumber"].ShouldNotBeNull();
        outcome.PagesNotRead.ShouldBe(0);
        outcome.Cost.LlmInputTokens.ShouldBe(3_000, "the cost of every call counts");
    }

    [Fact]
    public async Task Lines_per_page_stops_at_the_steps_cost_limit_and_says_how_many_pages_it_left()
    {
        // A price of 1 USD per million tokens either way: each call below costs 0.102 USD.
        var gateway = new FakeGateway()
            .Answer("""{ "returnNumber": "RMA-4711", "returnDate": null, "currency": null, "creditAmount": null, "supplier": null, "deliveryNotes": [], "pickup": null, "unreadableRegions": [] }""", usage: new LlmUsage(100_000, 2_000), model: "test-model")
            .Answer("""{ "items": [ { "supplierSku": "1", "description": "Strauchtomaten", "quantity": 10, "reason": null } ], "unreadableRegions": [] }""", usage: new LlmUsage(100_000, 2_000), model: "test-model");
        var prices = new PriceTable(new Dictionary<string, ModelPrice>(StringComparer.Ordinal) { ["test-model"] = new ModelPrice(1m, 1m, 1m, 1m) });
        var extractor = new LlmDefinitionExtractor(gateway, Prompts, prices, new LlmExtractionOptions());
        var request = Request() with
        {
            LinesPerPage = true,
            SpentUsd = 0.01m,
            MaxCostUsd = 0.15m,
            Input = Request().Input with { Pdf = null, PageImages = [(ReadOnlyMemory<byte>)Inputs.PngBytes, (ReadOnlyMemory<byte>)Inputs.PngBytes, (ReadOnlyMemory<byte>)Inputs.PngBytes] },
        };

        var outcome = await extractor.ExtractAsync(request, CancellationToken.None);

        gateway.Requests.Count.ShouldBe(2, "the header (0.112 USD with what was spent before) and page 1; then 0.214 USD is past the limit");
        outcome.PagesNotRead.ShouldBe(2);
        outcome.Document.Root["items"]!.AsArray().Count.ShouldBe(1, "what was read is kept");
    }
}
