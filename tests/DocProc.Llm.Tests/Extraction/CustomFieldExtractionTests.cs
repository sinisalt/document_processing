using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Llm.Extraction;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests.Extraction;

/// <summary>
/// <c>x_custom</c> in the invoice LLM call (docs/11 §9.1): a fork of an invoice-family type with added header fields asks
/// the model for them in the compiled schema (<c>invoice_extraction@1+x.&lt;hash8&gt;</c>) and stores the answers as
/// fields of the tree with LLM provenance; without added fields the request is exactly today's.
/// </summary>
public sealed class CustomFieldExtractionTests
{
    private static readonly PromptCatalogue Prompts = PromptCatalogue.Load();

    private static LlmInvoiceExtractor Extractor(FakeGateway gateway) => new(gateway, Prompts, PriceTable.Default, new LlmExtractionOptions());

    private const string Answer = """
        {
          "kind": "CreditNote", "invoiceNumber": "RE-2026-0042", "issueDate": "2026-10-27", "deliveryDate": null,
          "servicePeriodFrom": null, "servicePeriodTo": null, "dueDate": null, "currency": "EUR",
          "supplier": { "name": "Musterhandel GmbH", "vatId": "DE000000000", "taxId": null, "registrationNumber": null,
                        "addressLine1": null, "addressLine2": null, "postalCode": null, "city": null, "countryCode": "DE",
                        "email": null, "phone": null, "iban": null, "bic": null },
          "customer": null, "orderReferences": ["PO-77"], "deliveryNoteReferences": [], "originalInvoiceReference": null,
          "subtotalNet": 150.00,
          "taxBreakdown": [ { "ratePercent": 7, "taxableAmount": 100, "taxAmount": 7, "categoryCode": "S" } ],
          "totalTax": 16.50, "totalGross": 166.50, "amountDue": null, "rounding": null,
          "chargesAndAllowances": [], "paymentTerms": null, "notes": null,
          "lines": [],
          "unreadableRegions": [],
          "x_custom": { "supplierCustomerRef": "PO-77", "creditReason": "Return", "storeCount": 3, "missingOne": null }
        }
        """;

    /// <summary>The credit-note template forked with three added header fields (and one added row field, which the invoice call does not read).</summary>
    internal static TypeDefinition Fork()
    {
        var template = StandardTemplates.Find("std.credit-note")!;
        return template with
        {
            Key = "CreditNote",
            LegacyKey = null,
            Base = new DefinitionBase { Template = template.Key, Version = template.Version, Mode = "copy" },
            Fields =
            [
                .. template.Fields,
                new FieldDefinition { Id = "fld_c_scr", Key = "supplierCustomerRef", Kind = FieldKinds.Identifier, Indexed = true, Description = "The account number the supplier assigned to us." },
                new FieldDefinition { Id = "fld_c_reason", Key = "creditReason", Kind = FieldKinds.Enum, Options = [new EnumOption { Code = "Return" }, new EnumOption { Code = "Other" }] },
                new FieldDefinition { Id = "fld_c_count", Key = "storeCount", Kind = FieldKinds.Integer },
                new FieldDefinition { Id = "fld_c_missing", Key = "missingOne", Kind = FieldKinds.Date },
            ],
        };
    }

    private static ResolvedDefinition Resolved(TypeDefinition definition)
    {
        var compiled = DefinitionSchemaCompiler.Compile(definition).Compiled!;
        var custom = compiled.LlmSchema.CustomPaths.Select(p => compiled.Descriptors.Find(p)).OfType<FieldDescriptor>().ToList();
        return new ResolvedDefinition("CreditNote", Guid.NewGuid(), 2, compiled.LlmSchema, custom);
    }

    private static ExtractionInput Input(ResolvedDefinition? definition) => Inputs.Extraction(DocumentTypeKey.CreditNote) with { Definition = definition };

    [Fact]
    public async Task Added_header_fields_are_asked_for_in_x_custom_and_stored_as_fields_of_the_tree()
    {
        var gateway = new FakeGateway().Answer(Answer);
        var definition = Resolved(Fork());
        definition.HasCustomFields.ShouldBeTrue();

        var outcome = (await Extractor(gateway).ExtractAsync(Input(definition), CancellationToken.None))!;

        var request = gateway.Requests.ShouldHaveSingleItem();
        request.OutputSchema.ShouldNotBeNull();
        request.OutputSchema.Name.ShouldBe("invoice_extraction");
        request.OutputSchema.Version.ShouldStartWith("1+x.");
        request.OutputSchema.Version.ShouldBe(definition.LlmSchema!.Version);
        var custom = request.OutputSchema.Schema["properties"]![DefinitionCompiler.CustomObject].ShouldNotBeNull();
        custom.ToJsonString().ShouldContain("supplierCustomerRef");
        custom.ToJsonString().ShouldContain("The account number the supplier assigned to us.");
        SchemaPortability.Check(request.OutputSchema.Schema).ShouldBeEmpty();

        // The invoice fields are mapped as always; the added ones go into the tree beside them.
        var tree = outcome.Document.Root;
        tree["invoiceNumber"]!["value"]!["verbatim"]!.GetValue<string>().ShouldBe("RE-2026-0042");
        var reference = tree["supplierCustomerRef"]!.AsObject();
        reference["status"]!.GetValue<string>().ShouldBe(nameof(FieldStatus.Extracted));
        reference["value"]!["verbatim"]!.GetValue<string>().ShouldBe("PO-77");
        reference["value"]!["normalized"]!.GetValue<string>().ShouldBe("PO-77");
        reference["source"]!["method"]!.GetValue<string>().ShouldBe("Llm");
        reference["source"]!["page"]!.GetValue<int>().ShouldBe(1, "located in the text layer like the invoice's own fields");
        tree["creditReason"]!["value"]!.GetValue<string>().ShouldBe("Return");
        tree["storeCount"]!["value"]!.GetValue<long>().ShouldBe(3);
        tree.ContainsKey("missingOne").ShouldBeFalse("a null answer leaves the field Missing; the Extract stage completes the tree");
        tree.ContainsKey(DefinitionCompiler.CustomObject).ShouldBeFalse("x_custom itself is not stored");

        // The stored JSON of the outcome carries them too (it is written from the invoice and its extension data).
        JsonNode.Parse(outcome.Document.ToJson())!["supplierCustomerRef"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Without_added_fields_the_request_is_exactly_todays()
    {
        var plain = new FakeGateway().Answer(SampleAnswers.Invoice);
        await Extractor(plain).ExtractAsync(Input(null), CancellationToken.None);

        var adopted = new FakeGateway().Answer(SampleAnswers.Invoice);
        var template = StandardTemplates.Find("std.credit-note")!;
        var compiled = DefinitionSchemaCompiler.Compile(template).Compiled!;
        compiled.LlmSchema.CustomPaths.ShouldBeEmpty();
        await Extractor(adopted).ExtractAsync(Input(new ResolvedDefinition("CreditNote", Guid.NewGuid(), 1, compiled.LlmSchema, [])), CancellationToken.None);

        plain.Requests.Single().OutputSchema.ShouldBe(CanonicalSchemas.InvoiceExtraction);
        adopted.Requests.Single().OutputSchema.ShouldBe(CanonicalSchemas.InvoiceExtraction);
    }

    [Fact]
    public void Values_that_do_not_fit_their_kind_are_left_out()
    {
        var definition = Resolved(Fork());
        var answer = """{ "x_custom": { "supplierCustomerRef": 42, "creditReason": "  ", "storeCount": 2.5, "missingOne": "31.10.2026" } }""";

        CustomFieldMapper.Map(answer, definition.CustomFields, new ProvenanceLocator(Inputs.SampleTextLayer()), "EUR").ShouldBeEmpty();
        CustomFieldMapper.Map("""{ "kind": "Invoice" }""", definition.CustomFields, new ProvenanceLocator(null), "EUR").ShouldBeEmpty();
        CustomFieldMapper.Map("not json", definition.CustomFields, new ProvenanceLocator(null), "EUR").ShouldBeEmpty();

        var dated = CustomFieldMapper.Map("""{ "x_custom": { "missingOne": "2026-10-27" } }""", definition.CustomFields, new ProvenanceLocator(Inputs.SampleTextLayer()), "EUR");
        var envelope = dated["missingOne"];
        envelope.GetProperty("value").GetString().ShouldBe("2026-10-27");
        envelope.GetProperty("status").GetString().ShouldBe(nameof(FieldStatus.Extracted));
        JsonSerializer.Deserialize<FieldValue<DateOnly>>(envelope.GetRawText(), CanonicalJson.Options)!.Value.ShouldBe(new DateOnly(2026, 10, 27));
    }
}
