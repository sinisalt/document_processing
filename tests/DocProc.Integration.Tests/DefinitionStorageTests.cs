using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Application.Outputs;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Common;
using DocProc.Domain.DeliveryNotes;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Domain.PurchaseOrders;
using DocProc.Infrastructure.Persistence;
using DocProc.Llm.Schema;

namespace DocProc.Integration.Tests;

/// <summary>
/// Canonical storage of definition-driven documents (docs/11 §5.6): the empty tree of a type version, completing a tree
/// with added fields, the values view, the structural check of <c>validate.schema</c> (§11 layer 1), the field index
/// rows of indexed fields (§6.2) and which documents are definition-driven at all.
/// </summary>
public sealed class DefinitionStorageTests
{
    private static CompiledDefinition Compile(string template) => DefinitionSchemaCompiler.Compile(StandardTemplates.Find(template)!).Compiled!;

    private static CompiledDefinition Compile(TypeDefinition definition)
    {
        var result = DefinitionSchemaCompiler.Compile(definition);
        result.Problems.ShouldBeEmpty();
        return result.Compiled!;
    }

    [Theory]
    [InlineData("std.purchase-order", typeof(PurchaseOrder))]
    [InlineData("std.delivery-note", typeof(DeliveryNote))]
    [InlineData("std.invoice", typeof(Invoice))]
    public void The_empty_tree_of_a_template_is_the_tree_of_its_empty_record(string template, Type record)
    {
        var empty = DefinitionTree.CreateEmpty(Compile(template).Descriptors);

        // Every member of the record, Missing: the record reads it as its own empty instance, so a typed reader (the coded
        // rules, the invoice patcher) and the generated form agree on it.
        var expected = JsonSerializer.SerializeToNode(Activator.CreateInstance(record), record, CanonicalJson.Options)!.AsObject();
        var read = JsonSerializer.SerializeToNode(JsonSerializer.Deserialize(empty.ToJsonString(), record, CanonicalJson.Options), record, CanonicalJson.Options)!.AsObject();
        JsonNode.DeepEquals(read, expected).ShouldBeTrue($"{read.ToJsonString()}\n{expected.ToJsonString()}");
        empty.Select(p => p.Key).ShouldAllBe(key => expected.ContainsKey(key));
        empty.Where(p => p.Value is JsonObject).ShouldAllBe(p => p.Value!["status"]!.GetValue<string>() == "Missing");
    }

    [Fact]
    public void Completing_a_tree_keeps_its_values_and_adds_the_fields_a_version_added()
    {
        var invoice = new Invoice { InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create("GS-1")), Lines = [new InvoiceLine { LineNumber = 1, Description = FieldValue.Extracted("Kaffee") }] };
        var tree = JsonSerializer.SerializeToNode(invoice, CanonicalJson.Options)!.AsObject();
        var before = tree.DeepClone();
        var fork = WithCustomFields(StandardTemplates.Find("std.credit-note")!);

        DefinitionTree.Complete(tree, Compile(fork).Descriptors).ShouldBeTrue();

        tree["supplierCustomerRef"]!["status"]!.GetValue<string>().ShouldBe("Missing");
        tree["ext"].ShouldBeOfType<JsonObject>().Count.ShouldBe(0);
        tree["lines"]![0]!["ext"].ShouldBeOfType<JsonObject>();
        JsonNode.DeepEquals(tree["invoiceNumber"], before["invoiceNumber"]).ShouldBeTrue();
        DefinitionTree.Complete(tree, Compile(fork).Descriptors).ShouldBeFalse("nothing is missing any more");

        // The typed record still reads the tree and keeps the added members (P0 [JsonExtensionData]).
        var typed = JsonSerializer.Deserialize<Invoice>(tree.ToJsonString(), CanonicalJson.Options)!;
        typed.InvoiceNumber.Value!.Verbatim.ShouldBe("GS-1");
        typed.Extra!.Keys.ShouldBe(["supplierCustomerRef", "ext"], ignoreOrder: true);
        JsonNode.Parse(JsonSerializer.Serialize(typed, CanonicalJson.Options))!["supplierCustomerRef"].ShouldNotBeNull();
    }

    [Fact]
    public void The_values_view_turns_envelopes_into_plain_values_by_kind()
    {
        var fork = WithCustomFields(StandardTemplates.Find("std.credit-note")!);
        var compiled = Compile(fork);
        var invoice = new Invoice
        {
            InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create("GS-2026-0042")),
            IssueDate = FieldValue.Extracted(new DateOnly(2026, 10, 28)),
            Currency = FieldValue.Extracted("EUR"),
            TotalGross = FieldValue.Extracted(new Money(-119.00m, "EUR")),
            OrderReferences = [FieldValue.Extracted(DocumentIdentifier.Create("PO-1"))],
            Supplier = FieldValue.Unreadable<Party>(),
            Lines = [new InvoiceLine { LineNumber = 1, Description = FieldValue.Extracted("Kaffee Crema 1kg"), Quantity = FieldValue.Extracted(-2m) }],
        };
        var tree = JsonSerializer.SerializeToNode(invoice, CanonicalJson.Options)!.AsObject();
        DefinitionTree.Complete(tree, compiled.Descriptors);
        tree["supplierCustomerRef"] = JsonSerializer.SerializeToNode(FieldValue.Extracted<JsonNode>(JsonSerializer.SerializeToNode(DocumentIdentifier.Create("123 456789"), CanonicalJson.Options)!), CanonicalJson.Options);
        tree["ext"]!["storeNo"] = JsonSerializer.SerializeToNode(FieldValue.Extracted<JsonNode>(JsonValue.Create("0417")), CanonicalJson.Options);

        var view = DocumentDataView.Build(tree, compiled, new DocumentDataMeta(Guid.Parse("6c1f0000-0000-0000-0000-000000000001"), 7, "CreditNote", 3, "Approved"));

        var doc = view["doc"]!;
        doc["invoiceNumber"]!.GetValue<string>().ShouldBe("GS-2026-0042");
        doc["issueDate"]!.GetValue<string>().ShouldBe("2026-10-28");
        doc["totalGross"]!["amount"]!.GetValue<decimal>().ShouldBe(-119.00m);
        doc["totalGross"]!["currency"]!.GetValue<string>().ShouldBe("EUR");
        doc["supplier"].ShouldBeNull("Unreadable is null in the view");
        doc["dueDate"].ShouldBeNull("Missing is null");
        doc["orderReferences"]!.AsArray().Select(v => v!.GetValue<string>()).ShouldBe(["PO-1"]);
        doc["supplierCustomerRef"]!.GetValue<string>().ShouldBe("123 456789");
        doc["ext"]!["storeNo"]!.GetValue<string>().ShouldBe("0417");
        doc["lines"]![0]!["description"]!.GetValue<string>().ShouldBe("Kaffee Crema 1kg");
        doc["lines"]![0]!["lineNumber"]!.GetValue<int>().ShouldBe(1, "a bare member is shown as stored");
        view["roles"]!["documentNumber"]!.GetValue<string>().ShouldBe("GS-2026-0042");
        view["roles"]!["total"]!["amount"]!.GetValue<decimal>().ShouldBe(-119.00m);
        view["meta"]!["typeKey"]!.GetValue<string>().ShouldBe("CreditNote");
        view["meta"]!["version"]!.GetValue<int>().ShouldBe(7);
    }

    [Fact]
    public void The_structural_check_raises_field_required_and_field_invalid_with_grammar_paths()
    {
        var compiled = Compile("std.price-list");
        var tree = DefinitionTree.CreateEmpty(compiled.Descriptors);
        tree["currency"] = Envelope(JsonValue.Create("Euro"));
        tree["declaredLineCount"] = Envelope(JsonValue.Create("many"));
        tree["lines"] = new JsonArray(Row(compiled, row => row["description"] = Envelope(JsonValue.Create("Kaffee"))));
        var values = DocumentDataView.Values(tree, compiled.Descriptors);

        var flags = StructuralValidator.Validate(values, compiled);

        flags.Where(f => f.Code == FlagCodes.FieldRequired).Select(f => f.Field).ShouldBe(["validFrom", "supplier", "lines[0].supplierSku", "lines[0].price"], ignoreOrder: true);
        flags.ShouldAllBe(f => f.Code != FlagCodes.FieldRequired || f.Severity == FlagSeverity.Blocking);
        var currency = flags.Single(f => f.Field == "currency");
        currency.Code.ShouldBe(FlagCodes.FieldInvalid);
        currency.Severity.ShouldBe(FlagSeverity.Blocking, "currency is required");
        currency.Params!["keyword"].ShouldBe("pattern");
        var count = flags.Single(f => f.Field == "declaredLineCount");
        count.Severity.ShouldBe(FlagSeverity.Warning, "an optional field");
        count.Params!["keyword"].ShouldBe("type");

        // A complete list passes.
        tree["validFrom"] = Envelope(JsonValue.Create("2026-11-01"));
        tree["currency"] = Envelope(JsonValue.Create("EUR"));
        tree["declaredLineCount"] = Envelope(JsonValue.Create(1));
        tree["supplier"] = Envelope(JsonNode.Parse("""{"name":"Nordwind"}"""));
        tree["lines"]![0]!["supplierSku"] = Envelope(JsonNode.Parse("""{"verbatim":"4711","normalized":"4711"}"""));
        tree["lines"]![0]!["price"] = Envelope(JsonNode.Parse("""{"amount":12.5,"currency":"EUR"}"""));
        StructuralValidator.Validate(DocumentDataView.Values(tree, compiled.Descriptors), compiled).ShouldBeEmpty();

        // A date that is not a date is invalid (format validation is required, not an annotation).
        tree["issueDate"] = Envelope(JsonValue.Create("31.10.2026"));
        StructuralValidator.Validate(DocumentDataView.Values(tree, compiled.Descriptors), compiled).ShouldHaveSingleItem().Params!["keyword"].ShouldBe("format");

        StructuralValidator.ToPath("/lines/2/lineNet").ShouldBe("lines[2].lineNet");
        StructuralValidator.ToPath(string.Empty).ShouldBe(string.Empty);
        StructuralValidator.ToPath("/a~1b/0").ShouldBe("a/b[0]");
    }

    [Fact]
    public void The_field_index_holds_the_values_of_indexed_fields_normalised()
    {
        var compiled = Compile("std.price-list");
        var tree = DefinitionTree.CreateEmpty(compiled.Descriptors);
        tree["priceListNumber"] = Envelope(JsonNode.Parse("""{"verbatim":"pl 2026 / 11","normalized":"PL 2026 / 11"}"""));
        tree["validFrom"] = Envelope(JsonValue.Create("2026-11-01"));
        tree["listScope"] = Envelope(JsonValue.Create("Full"));
        var indexed = compiled.Descriptors.Fields.Where(f => f.Indexed).ToList();
        var document = Guid.NewGuid();

        var entries = DocumentFieldIndexWriter.Entries(Guid.Empty, document, tree.ToJsonString(), indexed);

        entries.Select(e => (e.FieldKey, e.Ordinal, e.StringValue, e.DateValue)).ShouldBe(
        [
            ("priceListNumber", 0, "PL 2026 / 11", null),
            ("validFrom", 0, null, new DateOnly(2026, 11, 1)),
            ("listScope", 0, "FULL", null),
        ]);
        entries.ShouldAllBe(e => e.DocumentId == document);
        DocumentFieldIndexWriter.Entries(Guid.Empty, document, "not json", indexed).ShouldBeEmpty();
        DocumentFieldIndexWriter.Entries(Guid.Empty, document, tree.ToJsonString(), []).ShouldBeEmpty();

        // Row fields and lists get one row per value, in order.
        var rows = DocumentFieldIndexWriter.Entries(Guid.Empty, document, """{"lines":[{"supplierSku":{"value":"a-1","status":"Extracted"}},{"supplierSku":{"status":"Missing"}},{"supplierSku":{"value":"b 2","status":"Corrected"}}]}""",
            [new FieldDescriptor { Path = "lines[*].supplierSku", FieldId = "f", Key = "supplierSku", Kind = FieldKinds.Identifier, Indexed = true }]);
        rows.Select(e => (e.FieldKey, e.Ordinal, e.StringValue)).ShouldBe([("lines[*].supplierSku", 0, "A-1"), ("lines[*].supplierSku", 1, "B 2")]);
    }

    [Fact]
    public void Only_tenants_on_configurable_types_store_documents_by_their_definition()
    {
        var resolver = new StandardTemplateConfigResolver(DefinitionSchemaCompiler.Options);
        var invoice = resolver.Find(DocumentTypeKey.Invoice)!;
        var order = resolver.Find(DocumentTypeKey.PurchaseOrder)!;
        var statement = resolver.Find(DocumentTypeKey.Parse("Statement"))!;
        invoice.Compiled.ShouldNotBeNull();
        StandardTemplateConfigResolver.Instance.Find(DocumentTypeKey.Invoice)!.Compiled.ShouldBeNull("without the adapter schemas the invoice does not compile");

        CanonicalStorage.IsDefinitionDriven(order, configurable: false).ShouldBeFalse();
        CanonicalStorage.IsDefinitionDriven(order, configurable: true).ShouldBeTrue();
        CanonicalStorage.IsDefinitionDriven(invoice, configurable: true).ShouldBeFalse("an adopted invoice keeps the coded records");
        CanonicalStorage.IsDefinitionDriven(invoice with { }, configurable: true).ShouldBeFalse();
        var fork = new EffectiveType("Invoice", invoice.Definition, DefinitionOrigin.Copied, "std.invoice", 1, compile: () => invoice.Compiled);
        CanonicalStorage.IsDefinitionDriven(fork, configurable: true).ShouldBeTrue();
        CanonicalStorage.IsDefinitionDriven(statement, configurable: true).ShouldBeFalse("no fields, nothing to store");
        CanonicalStorage.IsDefinitionDriven(null, configurable: true).ShouldBeFalse();

        invoice.RunsValidateStep(ValidateStepKinds.Duplicate).ShouldBeTrue();
        invoice.RunsValidateStep(ValidateStepKinds.Schema).ShouldBeFalse();
        order.RunsValidateStep(ValidateStepKinds.Rules).ShouldBeTrue("an empty validate list keeps the legacy default");
    }

    [Fact]
    public void The_role_projector_fills_the_validity_and_row_count_columns()
    {
        var bindings = new RoleBindings("priceListNumber", "issueDate", "currency", Total: null, ValidFrom: "validFrom", ValidUntil: "validUntil", LineItems: "lines");
        var projection = RoleProjector.Project(bindings, """{"validFrom":{"value":"2026-11-01","status":"Extracted"},"validUntil":{"status":"Missing"},"lines":[{},{},{}]}""");
        projection.ValidFrom.ShouldBe(new DateOnly(2026, 11, 1));
        projection.ValidUntil.ShouldBeNull();
        projection.LineCount.ShouldBe(3);
        RoleProjector.Project(RoleBindings.Invoice, "{}").LineCount.ShouldBeNull();
        RoleBindings.Invoice.LineItems.ShouldBe("lines");
    }

    internal static TypeDefinition WithCustomFields(TypeDefinition template) => template with
    {
        Key = template.LegacyKey ?? template.Key,
        LegacyKey = null,
        Base = new DefinitionBase { Template = template.Key, Version = template.Version, Mode = "copy" },
        Fields =
        [
            .. template.Fields.Select(f => f.Key == "lines" ? f with { Item = new FieldGroup { Fields = [.. f.Item!.Fields, new FieldDefinition { Id = "fld_l_ext", Key = "ext", Kind = FieldKinds.Bag }] } } : f),
            new FieldDefinition { Id = "fld_c_scr", Key = "supplierCustomerRef", Kind = FieldKinds.Identifier, Indexed = true, Description = "The account number the supplier assigned to us." },
            new FieldDefinition { Id = "fld_ext", Key = "ext", Kind = FieldKinds.Bag },
        ],
    };

    private static JsonObject Envelope(JsonNode? value) =>
        JsonSerializer.SerializeToNode(FieldValue.Extracted<JsonNode>(value!, confidence: 0.95), CanonicalJson.Options)!.AsObject();

    private static JsonObject Row(CompiledDefinition compiled, Action<JsonObject> change)
    {
        var row = DefinitionTree.EmptyRow(compiled.Descriptors, "lines");
        change(row);
        return row;
    }
}
