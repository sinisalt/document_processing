using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Documents;
using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
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

    [Theory]
    [InlineData("move", "not available yet")]
    [InlineData("setType", "not available yet")]
    [InlineData("split", "not available yet")]
    [InlineData("frobnicate", "is not a correction operation")]
    public void Operations_of_later_phases_are_refused_as_in_P0(string op, string message)
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
}
