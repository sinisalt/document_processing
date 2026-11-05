using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Documents;
using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Llm.Schema;

namespace DocProc.Api.Tests;

/// <summary>
/// The single-correction write of a whole repeating group (docs/11 §12.4): <see cref="DefinitionPatcher.SetRows"/>, the
/// table tool's write with each value's place on the page, and <c>set</c> on a group through <c>PATCH …/fields</c>; both
/// one audit record, both held to the group's <c>maxItems</c>.
/// </summary>
public sealed class DefinitionPatcherRowsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly CompiledDefinition PriceList = Compile(StandardTemplates.Find("std.price-list")!);

    private static CompiledDefinition Compile(TypeDefinition definition)
    {
        var result = DefinitionSchemaCompiler.Compile(definition);
        result.Problems.ShouldBeEmpty();
        return result.Compiled!;
    }

    /// <summary>The price list with its rows capped at <paramref name="maxItems"/>.</summary>
    private static CompiledDefinition PriceListCappedAt(int maxItems)
    {
        var template = StandardTemplates.Find("std.price-list")!;
        return Compile(template with { Fields = [.. template.Fields.Select(f => f.Key == "lines" ? f with { MaxItems = maxItems } : f)] });
    }

    private static JsonElement V(object? value) => JsonSerializer.SerializeToElement(value, CanonicalJson.Options);

    private static CapturedRow Row(string sku, decimal price, int page = 1, double y = 100, params (decimal Quantity, decimal Price)[] tiers) => new(
        [
            new CapturedCell("supplierSku", V(sku), new FieldSource(SourceMethod.Human, page, new BoundingBox(55, y, 91, y + 10), sku)),
            new CapturedCell("description", V($"Artikel {sku}"), new FieldSource(SourceMethod.Human, page, new BoundingBox(155, y, 230, y + 10), $"Artikel {sku}")),
            new CapturedCell("price", V(price), new FieldSource(SourceMethod.Human, page, new BoundingBox(705, y, 735, y + 10), "12,99")),
        ],
        tiers.Length == 0 ? null : new Dictionary<string, IReadOnlyList<CapturedRow>>(StringComparer.Ordinal)
        {
            ["tiers"] = [.. tiers.Select(t => new CapturedRow([new CapturedCell("minQuantity", V(t.Quantity), null), new CapturedCell("price", V(t.Price), null)]))],
        });

    [Fact]
    public void Set_rows_writes_every_row_with_its_own_place_on_the_page_as_one_correction()
    {
        var empty = DefinitionTree.CreateEmpty(PriceList.Descriptors);
        empty["title"] = JsonNode.Parse("""{"status":"Extracted","value":"Preisliste 2027","confidence":0.9,"history":[]}""");
        var before = empty.ToJsonString();
        var source = new CorrectionSource(Method: "table", Page: 1, BBox: new SourceBox(40, 95, 995, 940));

        var result = DefinitionPatcher.SetRows(PriceList.Descriptors, before, "lines", [Row("1001", 12.99m, tiers: [(10, 11.5m), (50, 10.9m)]), Row("1002", 3.5m, page: 2, y: 80)], "anna", At, "EUR", reason: "table", source: source);

        var applied = result.Applied.ShouldHaveSingleItem();
        (applied.Path, applied.Op, applied.Reason, applied.Source).ShouldBe(("lines", CorrectionOps.Set, "table", source));
        applied.OldValueJson.ShouldBe("[]");
        var newValues = JsonNode.Parse(applied.NewValueJson!)!.AsArray();
        newValues.Count.ShouldBe(2);
        newValues[0]!["supplierSku"]!["verbatim"]!.GetValue<string>().ShouldBe("1001");
        newValues[0]!["price"]!["amount"]!.GetValue<decimal>().ShouldBe(12.99m);
        newValues[0]!["tiers"]!.AsArray().Count.ShouldBe(2);
        newValues[1]!.AsObject().ContainsKey("tiers").ShouldBeFalse("an empty group is left out of the audit values");

        var tree = JsonNode.Parse(result.Json)!.AsObject();
        tree["title"]!.ToJsonString().ShouldBe(empty["title"]!.ToJsonString(), "fields the write does not touch stay as stored");
        var rows = tree["lines"]!.AsArray();
        var sku = JsonSerializer.Deserialize<FieldValue<JsonNode>>(rows[1]!["supplierSku"]!.ToJsonString(), CanonicalJson.Options)!;
        sku.Status.ShouldBe(FieldStatus.Corrected);
        sku.Source.ShouldBe(new FieldSource(SourceMethod.Human, 2, new BoundingBox(55, 80, 91, 90), "1002"));
        sku.History.ShouldHaveSingleItem().By.ShouldBe("anna");
        rows[0]!["price"]!["value"]!["currency"]!.GetValue<string>().ShouldBe("EUR", "an amount without a printed currency takes the document's, else the tenant's");
        rows[0]!["tiers"]!.AsArray()[1]!["minQuantity"]!["value"]!.GetValue<decimal>().ShouldBe(50m);
        rows[0]!["category"]!["status"]!.GetValue<string>().ShouldBe(nameof(FieldStatus.Missing), "a field no column fills is there, Missing");

        // A second capture replaces the rows; the audit keeps what they were.
        var again = DefinitionPatcher.SetRows(PriceList.Descriptors, result.Json, "lines", [Row("2001", 1m)], "ben", At, "EUR");
        JsonNode.Parse(again.Applied[0].OldValueJson!)!.AsArray().Count.ShouldBe(2);
        JsonNode.Parse(again.Json)!["lines"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void Set_rows_holds_the_groups_max_items()
    {
        var capped = PriceListCappedAt(3);
        var tree = DefinitionTree.CreateEmpty(capped.Descriptors).ToJsonString();

        DefinitionPatcher.SetRows(capped.Descriptors, tree, "lines", [Row("1", 1), Row("2", 2), Row("3", 3)], "anna", At, "EUR").Applied.Count.ShouldBe(1);
        var refused = Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.SetRows(capped.Descriptors, tree, "lines", [Row("1", 1), Row("2", 2), Row("3", 3), Row("4", 4)], "anna", At, "EUR"));
        refused.Message.ShouldContain("at most 3");
        refused.Path.ShouldBe("lines");

        // PATCH set on the group is held to it too.
        var four = JsonSerializer.SerializeToElement(Enumerable.Range(1, 4).Select(i => new { supplierSku = $"{i}", price = i }).ToArray());
        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(capped.Descriptors, tree, [new FieldCorrection("lines", four, null)], "anna", At, "EUR")).Message.ShouldContain("at most 3");
    }

    /// <summary>A <c>set</c> of <paramref name="lines"/> empty lines with <paramref name="tiers"/> empty tiers each, as the JSON a PATCH body carries.</summary>
    private static JsonElement EmptyLines(int lines, int tiers)
    {
        var line = "{\"tiers\":[" + string.Join(',', Enumerable.Repeat("{}", tiers)) + "]}";
        return JsonDocument.Parse("[" + string.Join(',', Enumerable.Repeat(line, lines)) + "]").RootElement.Clone();
    }

    [Fact]
    public void A_group_inside_a_row_without_max_items_holds_a_handful_of_rows_and_the_tree_a_bounded_number_in_all()
    {
        // std.price-list caps lines at 10,000 and leaves tiers without maxItems. Without a nested cap, 50 lines of 20,000
        // empty tiers (a 3 MB body) made one correction write a tree of 92 MB; they are refused before a row is built.
        var tree = DefinitionTree.CreateEmpty(PriceList.Descriptors).ToJsonString();
        PriceList.Descriptors.Find("lines[*].tiers").ShouldNotBeNull().MaxItems.ShouldBeNull();
        DefinitionPatcher.MaxRowsOf(PriceList.Descriptors.Find("lines[*].tiers")!, "lines[*].tiers").ShouldBe(DefinitionPatcher.DefaultMaxNestedRows);
        DefinitionPatcher.MaxRowsOf(PriceList.Descriptors.Find("lines")!, "lines").ShouldBe(10_000);

        var attack = Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(PriceList.Descriptors, tree, [new FieldCorrection("lines", EmptyLines(50, 20_000), null)], "anna", At, "EUR"));
        attack.Path.ShouldBe("lines[0].tiers");
        attack.Message.ShouldContain($"at most {DefinitionPatcher.DefaultMaxNestedRows}");
        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(PriceList.Descriptors, tree, [new FieldCorrection("lines", EmptyLines(2, DefinitionPatcher.DefaultMaxNestedRows + 1), null)], "anna", At, "EUR")).Path.ShouldBe("lines[0].tiers");
        DefinitionPatcher.Apply(PriceList.Descriptors, tree, [new FieldCorrection("lines", EmptyLines(2, DefinitionPatcher.DefaultMaxNestedRows), null)], "anna", At, "EUR").Applied.Count.ShouldBe(1);

        // Every group within its cap, but the rows in all past the tree's: 10,000 lines of 11 tiers are 120,000 rows.
        var many = Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(PriceList.Descriptors, tree, [new FieldCorrection("lines", EmptyLines(10_000, 11), null)], "anna", At, "EUR"));
        many.Message.ShouldContain("120000 rows");
        many.Message.ShouldContain($"{DefinitionPatcher.MaxTreeRows}");

        // The table tool's write is held to the same caps.
        var row = Row("1", 1, tiers: [.. Enumerable.Range(1, DefinitionPatcher.DefaultMaxNestedRows + 1).Select(i => ((decimal)i, 1m))]);
        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.SetRows(PriceList.Descriptors, tree, "lines", [row], "anna", At, "EUR")).Path.ShouldBe("lines[0].tiers");

        // An append to a nested group stops at its cap too.
        var one = DefinitionPatcher.Apply(PriceList.Descriptors, tree, [new FieldCorrection("lines", EmptyLines(1, DefinitionPatcher.DefaultMaxNestedRows - 1), null)], "anna", At, "EUR").Json;
        var full = DefinitionPatcher.Apply(PriceList.Descriptors, one, [new FieldCorrection("lines[0].tiers", null, null, CorrectionOps.Append)], "anna", At, "EUR").Json;
        JsonNode.Parse(full)!["lines"]![0]!["tiers"]!.AsArray().Count.ShouldBe(DefinitionPatcher.DefaultMaxNestedRows);
        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(PriceList.Descriptors, full, [new FieldCorrection("lines[0].tiers", null, null, CorrectionOps.Append)], "anna", At, "EUR")).Message.ShouldContain("the most it may have");
    }

    /// <summary>The price list's empty tree with <paramref name="lines"/> lines of <paramref name="tiers"/> tiers each.</summary>
    private static string PriceListWith(int lines, int tiers) =>
        DefinitionPatcher.Apply(PriceList.Descriptors, DefinitionTree.CreateEmpty(PriceList.Descriptors).ToJsonString(), [new FieldCorrection("lines", EmptyLines(lines, tiers), null)], "anna", At, "EUR").Json;

    /// <summary>
    /// The bound of the timing tests below: generous, so a loaded build machine does not fail them, and far below what
    /// the quadratic versions took (84 s for the pairs, over 20 s for the sets).
    /// </summary>
    private static readonly TimeSpan Quickly = TimeSpan.FromSeconds(10);

    [Fact]
    public void Alternating_removes_and_appends_on_a_full_price_list_keep_the_row_count_without_recounting_the_tree()
    {
        var full = PriceListWith(10_000, 2);
        var pairs = Enumerable.Range(0, 2_500)
            .SelectMany(_ => new[] { new FieldCorrection("lines[0]", null, null, CorrectionOps.Remove), new FieldCorrection("lines", null, null, CorrectionOps.Append) })
            .ToList();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = DefinitionPatcher.Apply(PriceList.Descriptors, full, pairs, "anna", At, "EUR");
        clock.Stop();

        clock.Elapsed.ShouldBeLessThan(Quickly);
        result.Applied.Count.ShouldBe(5_000);
        var lines = JsonNode.Parse(result.Json)!["lines"]!.AsArray();
        lines.Count.ShouldBe(10_000);
        lines[^1]!["tiers"]!.AsArray().ShouldBeEmpty();
        lines[0]!["tiers"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void A_removed_row_gives_back_its_rows_and_nested_rows_to_the_tree_cap_exactly()
    {
        // 10,000 lines of 9 tiers: the tree holds exactly MaxTreeRows rows.
        var full = PriceListWith(10_000, 9);
        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(PriceList.Descriptors, full, [new FieldCorrection("lines[0].tiers", null, null, CorrectionOps.Append)], "anna", At, "EUR"))
            .Message.ShouldContain($"{DefinitionPatcher.MaxTreeRows + 1} rows");

        // Removing a line frees it and its 9 tiers: 10 rows may come back, the 11th is refused.
        var remove = new FieldCorrection("lines[0]", null, null, CorrectionOps.Remove);
        var tenTiers = Enumerable.Repeat(new FieldCorrection("lines[0].tiers", null, null, CorrectionOps.Append), 10).ToList();
        var refilled = DefinitionPatcher.Apply(PriceList.Descriptors, full, [remove, .. tenTiers], "anna", At, "EUR");
        JsonNode.Parse(refilled.Json)!["lines"]![0]!["tiers"]!.AsArray().Count.ShouldBe(19);

        var over = Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(PriceList.Descriptors, full, [remove, .. tenTiers, tenTiers[0]], "anna", At, "EUR"));
        over.Message.ShouldContain($"{DefinitionPatcher.MaxTreeRows + 1} rows");
    }

    [Fact]
    public void Five_thousand_sets_of_one_cell_keep_its_whole_history_and_take_linear_time()
    {
        var full = PriceListWith(10_000, 2);
        var first = DefinitionPatcher.Apply(PriceList.Descriptors, full, [new FieldCorrection("lines[0].description", V("erste"), null)], "anna", At, "EUR").Json;
        var stored = JsonNode.Parse(first)!["lines"]![0]!["description"]!["history"]![0]!.ToJsonString();
        var sets = Enumerable.Range(1, 5_000).Select(i => new FieldCorrection("lines[0].description", V($"Fassung {i}"), null)).ToList();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = DefinitionPatcher.Apply(PriceList.Descriptors, first, sets, "anna", At, "EUR");
        clock.Stop();

        clock.Elapsed.ShouldBeLessThan(Quickly);
        var envelope = JsonNode.Parse(result.Json)!["lines"]![0]!["description"]!;
        var field = envelope.Deserialize<FieldValue<JsonNode>>(CanonicalJson.Options)!;
        field.Value!.GetValue<string>().ShouldBe("Fassung 5000");
        field.Status.ShouldBe(FieldStatus.Corrected);
        field.History.Count.ShouldBe(5_001);
        field.History[0].Status.ShouldBe(FieldStatus.Missing);
        field.History[1].Value!.GetValue<string>().ShouldBe("erste");
        field.History[^1].Value!.GetValue<string>().ShouldBe("Fassung 4999");
        field.History[^1].By.ShouldBe("anna");

        // The trail stored before the request is carried over as it was.
        envelope["history"]![0]!.ToJsonString().ShouldBe(stored);
    }

    [Fact]
    public void Set_rows_refuses_a_value_that_does_not_fit_its_field_and_changes_nothing()
    {
        var tree = DefinitionTree.CreateEmpty(PriceList.Descriptors).ToJsonString();
        var bad = new CapturedRow([new CapturedCell("price", V("zwölf"), null)]);
        var refused = Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.SetRows(PriceList.Descriptors, tree, "lines", [Row("1", 1), bad], "anna", At, "EUR"));
        refused.Path.ShouldBe("lines[1].price");

        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.SetRows(PriceList.Descriptors, tree, "lines", [new CapturedRow([new CapturedCell("nope", V("x"), null)])], "anna", At, "EUR")).Message.ShouldContain("not a field");
        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.SetRows(PriceList.Descriptors, tree, "currency", [], "anna", At, "EUR")).Message.ShouldContain("not a repeating group");
    }

    [Fact]
    public void Set_on_a_group_through_the_fields_patch_replaces_all_its_rows_with_nested_groups()
    {
        var tree = DefinitionTree.CreateEmpty(PriceList.Descriptors).ToJsonString();
        var rows = JsonSerializer.SerializeToElement(new object[]
        {
            new { supplierSku = "A1", description = "Alpha", price = 2.5m, tiers = new[] { new { minQuantity = 10, price = 2.2m } } },
            new { supplierSku = "B2", price = new { amount = 3m, currency = "CHF" } },
        });

        var result = DefinitionPatcher.Apply(PriceList.Descriptors, tree, [new FieldCorrection("lines", rows, "pasted", Source: new CorrectionSource(Method: "paste"))], "anna", At, "EUR");

        result.Applied.ShouldHaveSingleItem().Op.ShouldBe(CorrectionOps.Set);
        var lines = JsonNode.Parse(result.Json)!["lines"]!.AsArray();
        lines.Count.ShouldBe(2);
        lines[0]!["tiers"]!.AsArray()[0]!["price"]!["value"]!["amount"]!.GetValue<decimal>().ShouldBe(2.2m);
        lines[1]!["price"]!["value"]!["currency"]!.GetValue<string>().ShouldBe("CHF");

        // An empty array removes every row; anything but an array is refused.
        JsonNode.Parse(DefinitionPatcher.Apply(PriceList.Descriptors, result.Json, [new FieldCorrection("lines", JsonSerializer.SerializeToElement(Array.Empty<object>()), null)], "anna", At, "EUR").Json)!["lines"]!.AsArray().ShouldBeEmpty();
        Should.Throw<CanonicalPatchException>(() => DefinitionPatcher.Apply(PriceList.Descriptors, tree, [new FieldCorrection("lines", V("x"), null)], "anna", At, "EUR")).Message.ShouldContain("array of row objects");
    }

    [Fact]
    public void Rows_written_into_an_invoice_tree_are_numbered_so_the_invoice_record_reads_them()
    {
        var invoice = Compile(StandardTemplates.Find("std.invoice")!);
        var tree = JsonSerializer.Serialize(TestInvoices.Valid(), CanonicalJson.Options);
        var rows = new[]
        {
            new CapturedRow([new CapturedCell("description", V("Pfand Kiste"), null), new CapturedCell("quantity", V(2m), null)]),
            new CapturedRow([new CapturedCell("description", V("Mineralwasser"), null), new CapturedCell("quantity", V(12m), null)]),
        };

        var result = DefinitionPatcher.SetRows(invoice.Descriptors, tree, "lines", rows, "anna", At, "EUR", typedRecord: true);

        var read = JsonSerializer.Deserialize<Invoice>(result.Json, CanonicalJson.Options)!;
        read.Lines.Select(l => (l.LineNumber, l.Description.Value)).ShouldBe([(1, "Pfand Kiste"), (2, "Mineralwasser")]);
    }
}
