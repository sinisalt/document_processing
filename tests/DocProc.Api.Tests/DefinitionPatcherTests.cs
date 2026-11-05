using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Documents;
using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.PurchaseOrders;
using DocProc.Llm.Schema;

namespace DocProc.Api.Tests;

/// <summary>
/// The descriptor-driven patcher of definition-driven documents (docs/11 §12.8): set by kind with the shorthands, the
/// history push, creating missing fields, <c>append</c> and <c>remove</c> on rows, lists and bags, all-or-nothing, and the
/// refusal of the operations of later phases with the message of P0.
/// </summary>
public sealed class DefinitionPatcherTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly CompiledDefinition Order = Compile(StandardTemplates.Find("std.purchase-order")!);
    private static readonly CompiledDefinition PriceList = Compile(StandardTemplates.Find("std.price-list")!);

    private static CompiledDefinition Compile(TypeDefinition definition)
    {
        var result = DefinitionSchemaCompiler.Compile(definition);
        result.Problems.ShouldBeEmpty();
        return result.Compiled!;
    }

    private static JsonElement V(object? value) => JsonSerializer.SerializeToElement(value, CanonicalJson.Options);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CanonicalPatchResult Apply(CompiledDefinition compiled, JsonObject tree, params FieldCorrection[] corrections) =>
        DefinitionPatcher.Apply(compiled.Descriptors, tree.ToJsonString(), corrections, "anna", At, fallbackCurrency: "EUR");

    private static JsonObject Empty(CompiledDefinition compiled) => DefinitionTree.CreateEmpty(compiled.Descriptors);

    [Fact]
    public void Set_converts_by_kind_and_pushes_the_previous_value_into_the_history()
    {
        var first = Apply(Order, Empty(Order),
            new FieldCorrection("poNumber", V("po 4711"), "typed"),
            new FieldCorrection("orderDate", V("2026-10-30"), null),
            new FieldCorrection("currency", V("eur"), null),
            new FieldCorrection("totalGross", V(119.5m), null),
            new FieldCorrection("supplier.vatId", V("DE 811 111 111"), null));
        var tree = JsonNode.Parse(first.Json)!.AsObject();

        tree["poNumber"]!["status"]!.GetValue<string>().ShouldBe(nameof(FieldStatus.Corrected));
        tree["poNumber"]!["value"]!["verbatim"]!.GetValue<string>().ShouldBe("po 4711");
        tree["poNumber"]!["value"]!["normalized"]!.GetValue<string>().ShouldBe("PO 4711");
        tree["orderDate"]!["value"]!.GetValue<string>().ShouldBe("2026-10-30");
        tree["currency"]!["value"]!.GetValue<string>().ShouldBe("EUR");
        tree["totalGross"]!["value"]!["amount"]!.GetValue<decimal>().ShouldBe(119.5m);
        tree["totalGross"]!["value"]!["currency"]!.GetValue<string>().ShouldBe("EUR", "a bare amount takes the document's currency");
        tree["supplier"]!["value"]!["vatId"]!.GetValue<string>().ShouldBe("DE 811 111 111");
        first.Applied.Select(a => (a.Path, a.Op)).ShouldBe([("poNumber", "set"), ("orderDate", "set"), ("currency", "set"), ("totalGross", "set"), ("supplier.vatId", "set")]);
        first.Applied[0].Reason.ShouldBe("typed");

        // The tree is still the record's: the coded PurchaseOrder reads it.
        var order = JsonSerializer.Deserialize<PurchaseOrder>(first.Json, CanonicalJson.Options)!;
        order.PoNumber.Value!.Verbatim.ShouldBe("po 4711");
        order.TotalGross.Value!.Amount.ShouldBe(119.5m);

        var second = Apply(Order, tree, new FieldCorrection("orderDate", V("2026-10-31"), "misread"));
        var date = JsonSerializer.Deserialize<FieldValue<DateOnly>>(JsonNode.Parse(second.Json)!["orderDate"]!.ToJsonString(), CanonicalJson.Options)!;
        date.Value.ShouldBe(new DateOnly(2026, 10, 31));
        date.Source.ShouldBe(FieldSource.Human);
        // As with the coded patcher, every earlier state is kept: the Missing one, then the first correction.
        date.History.Count.ShouldBe(2);
        date.History[0].Status.ShouldBe(FieldStatus.Missing);
        var previous = date.History[^1];
        previous.Value.ShouldBe(new DateOnly(2026, 10, 30));
        previous.By.ShouldBe("anna");
        second.Applied.Single().OldValueJson.ShouldBe("\"2026-10-30\"");
        second.Applied.Single().NewValueJson.ShouldBe("\"2026-10-31\"");

        // Untouched fields stay as they were stored.
        JsonNode.DeepEquals(JsonNode.Parse(second.Json)!["poNumber"], tree["poNumber"]).ShouldBeTrue();
    }

    [Fact]
    public void Append_adds_rows_with_values_and_remove_takes_them_out()
    {
        var appended = Apply(Order, Empty(Order),
            new FieldCorrection("lines", J("""{"description":"Kaffee","quantityOrdered":2,"unitPrice":12.5}"""), null, Op: "append"),
            new FieldCorrection("lines", null, null, Op: "append"),
            new FieldCorrection("lines[1].description", V("Tee"), null));
        var lines = JsonNode.Parse(appended.Json)!["lines"]!.AsArray();

        lines.Count.ShouldBe(2);
        lines[0]!["description"]!["value"]!.GetValue<string>().ShouldBe("Kaffee");
        lines[0]!["quantityOrdered"]!["value"]!.GetValue<decimal>().ShouldBe(2m);
        lines[0]!["unitPrice"]!["value"]!["currency"]!.GetValue<string>().ShouldBe("EUR");
        lines[0]!["supplierSku"]!["status"]!.GetValue<string>().ShouldBe(nameof(FieldStatus.Missing), "a new row has every field");
        lines[1]!["description"]!["value"]!.GetValue<string>().ShouldBe("Tee");
        appended.Applied.Select(a => (a.Path, a.Op)).ShouldBe([("lines[0]", "append"), ("lines[1]", "append"), ("lines[1].description", "set")]);
        appended.Applied[0].NewValueJson.ShouldNotBeNull().ShouldContain("Kaffee");

        var removed = Apply(Order, JsonNode.Parse(appended.Json)!.AsObject(), new FieldCorrection("lines[0]", null, "duplicate row", Op: "remove"));
        var left = JsonNode.Parse(removed.Json)!["lines"]!.AsArray();
        left.Count.ShouldBe(1);
        left[0]!["description"]!["value"]!.GetValue<string>().ShouldBe("Tee");
        removed.Applied.Single().OldValueJson.ShouldNotBeNull().ShouldContain("Kaffee");
        removed.Applied.Single().NewValueJson.ShouldBeNull();
    }

    [Fact]
    public void Nested_rows_lists_and_bags_take_append_set_and_remove()
    {
        var tree = Empty(PriceList);
        var result = Apply(PriceList, tree,
            new FieldCorrection("lines", J("""{"supplierSku":"4711","description":"Kaffee","price":{"amount":12.5,"currency":"EUR"}}"""), null, Op: "append"),
            new FieldCorrection("lines[0].tiers", J("""{"minQuantity":10,"price":11.9}"""), null, Op: "append"),
            new FieldCorrection("lines[0].ext.storeNo", V("0417"), null),
            new FieldCorrection("ext.region", V("Nord"), null),
            new FieldCorrection("ext.region", null, null, Op: "remove"));
        var json = JsonNode.Parse(result.Json)!;

        json["lines"]![0]!["tiers"]![0]!["minQuantity"]!["value"]!.GetValue<decimal>().ShouldBe(10m);
        json["lines"]![0]!["tiers"]![0]!["price"]!["value"]!["amount"]!.GetValue<decimal>().ShouldBe(11.9m);
        json["lines"]![0]!["ext"]!["storeNo"]!["value"]!.GetValue<string>().ShouldBe("0417");
        json["lines"]![0]!["ext"]!["storeNo"]!["status"]!.GetValue<string>().ShouldBe(nameof(FieldStatus.Corrected));
        json["ext"]!.AsObject().ContainsKey("region").ShouldBeFalse();
        result.Applied.Select(a => a.Path).ShouldBe(["lines[0]", "lines[0].tiers[0]", "lines[0].ext.storeNo", "ext.region", "ext.region"]);

        // The values view reads what the patcher wrote.
        var values = Application.Outputs.DocumentDataView.Values(json.AsObject(), PriceList.Descriptors);
        values["lines"]![0]!["supplierSku"]!.GetValue<string>().ShouldBe("4711");
        values["lines"]![0]!["ext"]!["storeNo"]!.GetValue<string>().ShouldBe("0417");
    }

    [Fact]
    public void A_field_the_stored_tree_lacks_is_created()
    {
        // A tree stored for an earlier version of the type, without the field the current version added.
        var tree = Empty(PriceList);
        tree.Remove("supplierCustomerRef");

        var result = Apply(PriceList, tree, new FieldCorrection("supplierCustomerRef", V("123 456789"), null));

        JsonNode.Parse(result.Json)!["supplierCustomerRef"]!["value"]!["verbatim"]!.GetValue<string>().ShouldBe("123 456789");
        result.Applied.Single().OldValueJson.ShouldBeNull();
    }

    [Fact]
    public void Move_takes_the_value_with_its_place_into_another_field_and_leaves_the_source_missing_with_its_history()
    {
        var located = new CorrectionSource(Page: 2, BBox: new SourceBox(10, 20, 30, 40), RawText: "4711");
        var filled = Apply(Order, Empty(Order),
            new FieldCorrection("lines", J("""{"description":"Kaffee"}"""), null, Op: "append"),
            new FieldCorrection("lines", J("""{"description":"Tee"}"""), null, Op: "append"),
            new FieldCorrection("lines[0].description", V("4711"), null, Source: located),
            new FieldCorrection("totalGross", V(119.5m), null),
            new FieldCorrection("requestedDeliveryDate", V("2026-11-05"), null));
        var tree = JsonNode.Parse(filled.Json)!.AsObject();

        // A row's text into another row's article number; an amount into a decimal; a date into another date.
        var moved = Apply(Order, tree,
            new FieldCorrection("lines[1].supplierSku", null, "article no., not text", Op: "move", From: "lines[0].description"),
            new FieldCorrection("orderDate", null, null, Op: "move", From: "requestedDeliveryDate"));
        var result = JsonNode.Parse(moved.Json)!.AsObject();

        var sku = JsonSerializer.Deserialize<FieldValue<string>>(result["lines"]![1]!["supplierSku"]!.ToJsonString(), CanonicalJson.Options)!;
        sku.Value.ShouldBe("4711");
        sku.Status.ShouldBe(FieldStatus.Corrected);
        sku.Source.ShouldBe(new FieldSource(SourceMethod.Human, 2, new BoundingBox(10, 20, 30, 40), "4711"), "the value keeps the place it was read from");
        var description = JsonSerializer.Deserialize<FieldValue<string>>(result["lines"]![0]!["description"]!.ToJsonString(), CanonicalJson.Options)!;
        description.Status.ShouldBe(FieldStatus.Missing);
        description.HasValue.ShouldBeFalse();
        description.History[^1].Value.ShouldBe("4711");
        description.History[^1].Source!.Page.ShouldBe(2);
        result["orderDate"]!["value"]!.GetValue<string>().ShouldBe("2026-11-05");
        result["requestedDeliveryDate"]!["status"]!.GetValue<string>().ShouldBe("Missing");
        moved.Applied.Select(a => (a.Path, a.Op, a.From)).ShouldBe([("lines[1].supplierSku", "move", "lines[0].description"), ("orderDate", "move", "requestedDeliveryDate")]);
        moved.Applied[0].NewValueJson.ShouldBe("\"4711\"");
        moved.Applied[0].Reason.ShouldBe("article no., not text");
        JsonSerializer.Deserialize<PurchaseOrder>(moved.Json, CanonicalJson.Options).ShouldNotBeNull("the record still reads the tree");

        // An amount becomes its number in a decimal field of the price list's rows; a source with no value is refused.
        var priceList = Apply(PriceList, Empty(PriceList),
            new FieldCorrection("lines", J("""{"supplierSku":"A-1"}"""), null, Op: "append"),
            new FieldCorrection("declaredLineCount", V(12), null));
        var asText = Apply(PriceList, JsonNode.Parse(priceList.Json)!.AsObject(), new FieldCorrection("title", null, null, Op: "move", From: "declaredLineCount"));
        JsonNode.Parse(asText.Json)!["title"]!["value"]!.GetValue<string>().ShouldBe("12", "a number moved into a text field is its text");
        Should.Throw<CanonicalPatchException>(() => Apply(PriceList, JsonNode.Parse(priceList.Json)!.AsObject(), new FieldCorrection("title", null, null, Op: "move", From: "issueDate")))
            .Message.ShouldContain("has no value to move");
    }

    [Theory]
    [InlineData("orderDate", "currency", "does not fit")]
    [InlineData("poNumber", "poNumber", "onto itself")]
    [InlineData("lines", "poNumber", "not the repeating group")]
    [InlineData("supplier.vatId", "poNumber", "inside a composite")]
    [InlineData("nope", "poNumber", "is not a field")]
    [InlineData("status", "poNumber", "read-only")]
    public void Move_refuses_targets_it_cannot_fill(string from, string path, string message)
    {
        var tree = JsonNode.Parse(Apply(Order, Empty(Order),
            new FieldCorrection("poNumber", V("PO-1"), null),
            new FieldCorrection("orderDate", V("2026-10-30"), null),
            new FieldCorrection("supplier.vatId", V("DE1"), null)).Json)!.AsObject();
        var refused = Should.Throw<CanonicalPatchException>(() => Apply(Order, tree, new FieldCorrection(path, null, null, Op: "move", From: from)));
        refused.Message.ShouldContain(message);
    }

    [Theory]
    [InlineData("move", "names the field the value comes from in 'from'")]
    [InlineData("setType", "use POST /documents/{id}:set-type")]
    [InlineData("split", "use POST /documents/{id}:split")]
    [InlineData("join", "use POST /documents/{id}:join")]
    [InlineData("frobnicate", "is not a correction operation")]
    public void Review_actions_and_unknown_operations_are_refused_and_a_move_needs_its_source(string op, string message)
    {
        var refused = Should.Throw<CanonicalPatchException>(() => Apply(Order, Empty(Order), new FieldCorrection("poNumber", V("1"), null, Op: op)));
        refused.Message.ShouldContain(message);
    }

    [Theory]
    [InlineData("nope", "\"x\"", null, "is not a field of the document type")]
    [InlineData("lines", "\"x\"", null, "repeating group")]
    [InlineData("lines[3].description", "\"x\"", null, "has no row at index 3")]
    [InlineData("orderDate", "\"30.10.2026\"", null, "yyyy-MM-dd")]
    [InlineData("currency", "\"Euro\"", null, "not a currency")]
    [InlineData("status", "\"Open\"", null, "read-only")]
    [InlineData("poNumber", "\"1\"", "append", "only 'set' applies")]
    [InlineData("supplier.colour", "\"red\"", null, "not a property of a party value")]
    [InlineData("lines", "[1,2]", "append", "object of its field values")]
    public void Corrections_that_do_not_fit_the_definition_are_refused(string path, string value, string? op, string message)
    {
        var refused = Should.Throw<CanonicalPatchException>(() => Apply(Order, Empty(Order), new FieldCorrection(path, J(value), null, Op: op)));
        refused.Message.ShouldContain(message);
        refused.Path.ShouldBe(path);
    }

    [Fact]
    public void A_patch_applies_all_or_nothing()
    {
        var tree = Empty(Order);
        var before = tree.ToJsonString();

        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(Order.Descriptors, before,
        [
            new FieldCorrection("poNumber", V("PO-1"), null),
            new FieldCorrection("orderDate", V("never"), null),
        ], "anna", At));

        tree.ToJsonString().ShouldBe(before, "the input is never modified; the endpoint stores nothing on a refusal");
    }

    [Fact]
    public void An_amount_needs_a_currency_from_somewhere()
    {
        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(Order.Descriptors, Empty(Order).ToJsonString(), [new FieldCorrection("totalNet", V(10m), null)], "anna", At))
            .Message.ShouldContain("has no currency");

        var withCurrency = Apply(Order, Empty(Order), new FieldCorrection("currency", V("CHF"), null), new FieldCorrection("totalNet", V(10m), null));
        JsonNode.Parse(withCurrency.Json)!["totalNet"]!["value"]!["currency"]!.GetValue<string>().ShouldBe("CHF", "the document's currency comes before the tenant's");
    }

    [Fact]
    public void For_a_tree_a_coded_record_reads_too_typed_fields_cannot_be_cleared()
    {
        var tree = JsonNode.Parse(Apply(Order, Empty(Order), new FieldCorrection("orderDate", V("2026-10-30"), null)).Json)!.AsObject();

        DefinitionPatcher.Apply(Order.Descriptors, tree.ToJsonString(), [new FieldCorrection("orderDate", null, null)], "anna", At).Applied.Single().NewValueJson.ShouldBeNull();
        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(Order.Descriptors, tree.ToJsonString(), [new FieldCorrection("orderDate", null, null)], "anna", At, typedRecord: true))
            .Message.ShouldContain("cannot be cleared");
    }

    private static readonly CompiledDefinition InvoiceFork = Compile(StandardTemplates.Find("std.invoice")!);

    private static string InvoiceTree() => JsonSerializer.Serialize(TestInvoices.Valid(), CanonicalJson.Options);

    private static CanonicalPatchResult ApplyToInvoice(string json, params FieldCorrection[] corrections) =>
        DefinitionPatcher.Apply(InvoiceFork.Descriptors, json, corrections, "anna", At, fallbackCurrency: "EUR", typedRecord: true);

    [Fact]
    public void A_row_appended_to_an_invoice_tree_gets_its_next_line_number_so_the_invoice_record_reads_it()
    {
        var appended = ApplyToInvoice(InvoiceTree(),
            new FieldCorrection("lines", J("""{"description": "Pfand Kiste", "quantity": 2}"""), null, CorrectionOps.Append),
            new FieldCorrection("lines", null, null, CorrectionOps.Append));

        var invoice = JsonSerializer.Deserialize<Invoice>(appended.Json, CanonicalJson.Options)!;
        invoice.Lines.Select(l => l.LineNumber).ShouldBe([1, 2, 3, 4]);
        invoice.Lines[2].Description.Value.ShouldBe("Pfand Kiste");
        invoice.Lines[2].Quantity.Value.ShouldBe(2m);
        appended.Applied.Select(a => a.Path).ShouldBe(["lines[2]", "lines[3]"]);

        // Numbers continue after the highest one, also when a row was removed before.
        var removed = ApplyToInvoice(appended.Json, new FieldCorrection("lines[0]", null, null, CorrectionOps.Remove), new FieldCorrection("lines", null, null, CorrectionOps.Append));
        JsonSerializer.Deserialize<Invoice>(removed.Json, CanonicalJson.Options)!.Lines.Select(l => l.LineNumber).ShouldBe([2, 3, 4, 5]);

        // Any definition whose rows carry a bare line number numbers its new rows the same way.
        var order = JsonNode.Parse(Apply(Order, Empty(Order), new FieldCorrection("lines", null, null, CorrectionOps.Append), new FieldCorrection("lines", null, null, CorrectionOps.Append)).Json)!;
        order["lines"]!.AsArray().Select(r => r!["lineNumber"]!.GetValue<int>()).ShouldBe([1, 2]);
    }

    [Theory]
    [InlineData("kg", "Kilogram")]
    [InlineData("KGM", "Kilogram")]
    [InlineData("crate", "Crate")]
    [InlineData("H87", "Piece")]
    [InlineData("C62", "Piece")]
    [InlineData("litre", "Litre")]
    [InlineData("Box", "Box")]
    [InlineData("other", "Other")]
    public void A_unit_of_an_invoice_tree_is_stored_as_the_name_the_invoice_record_holds(string given, string stored)
    {
        var patched = ApplyToInvoice(InvoiceTree(), new FieldCorrection("lines[0].unit", V(given), null));

        JsonNode.Parse(patched.Json)!["lines"]![0]!["unit"]!["value"]!.GetValue<string>().ShouldBe(stored);
        JsonSerializer.Deserialize<Invoice>(patched.Json, CanonicalJson.Options)!.Lines[0].Unit.Value.ShouldBe(Enum.Parse<UnitOfMeasure>(stored));
    }

    [Fact]
    public void A_unit_the_invoice_record_cannot_hold_is_refused_and_a_unit_cannot_be_cleared()
    {
        Should.Throw<CanonicalPatchException>(() => ApplyToInvoice(InvoiceTree(), new FieldCorrection("lines[0].unit", V("furlong"), null)))
            .Message.ShouldContain("is not a unit of an invoice line");
        Should.Throw<CanonicalPatchException>(() => ApplyToInvoice(InvoiceTree(), new FieldCorrection("lines[0].unit", null, null)))
            .Message.ShouldContain("cannot be cleared");
        Should.Throw<CanonicalPatchException>(() => ApplyToInvoice(InvoiceTree(), new FieldCorrection("lines", J("""{"unit": "furlong"}"""), null, CorrectionOps.Append)))
            .Path.ShouldBe("lines.unit");

        // A tree no coded record reads keeps the code it is given (a delivery note holds UN/ECE codes).
        var order = Apply(Order, Empty(Order), new FieldCorrection("lines", J("""{"unit": "KGM"}"""), null, CorrectionOps.Append));
        JsonNode.Parse(order.Json)!["lines"]![0]!["unit"]!["value"]!.GetValue<string>().ShouldBe("KGM");
    }

    [Fact]
    public void A_patch_that_would_leave_a_tree_the_invoice_record_cannot_read_is_refused_as_a_whole()
    {
        // A stored tree that is already unreadable (a row without its number, as an earlier patcher wrote it) takes no
        // further correction until the row is fixed or removed; the error names where the record fails.
        var broken = JsonNode.Parse(InvoiceTree())!.AsObject();
        broken["lines"]!.AsArray().Add(DefinitionTree.EmptyRow(InvoiceFork.Descriptors, "lines"));
        var refused = Should.Throw<CanonicalPatchException>(() => ApplyToInvoice(broken.ToJsonString(), new FieldCorrection("invoiceNumber", V("RE-1"), null)));
        refused.Message.ShouldContain("the invoice record cannot read");
        refused.Message.ShouldContain("nothing was changed");

        var repaired = ApplyToInvoice(broken.ToJsonString(), new FieldCorrection("lines[2]", null, null, CorrectionOps.Remove), new FieldCorrection("invoiceNumber", V("RE-1"), null));
        JsonSerializer.Deserialize<Invoice>(repaired.Json, CanonicalJson.Options)!.InvoiceNumber.Value!.Verbatim.ShouldBe("RE-1");

        // A unit stored as a code the record does not know is named by its field path.
        var badUnit = JsonNode.Parse(InvoiceTree())!.AsObject();
        badUnit["lines"]![1]!["unit"]!["value"] = "KGM";
        Should.Throw<CanonicalPatchException>(() => ApplyToInvoice(badUnit.ToJsonString(), new FieldCorrection("notes", V("x"), null)))
            .Path.ShouldBe("lines[1].unit");
    }
}
