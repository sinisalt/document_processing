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
using DocProc.Domain.Validation;
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

    /// <summary>The approval gate's "no canonical data" (docs/11 §11): the empty tree, and rows of Missing fields, hold no value.</summary>
    [Fact]
    public void An_empty_tree_holds_no_value_until_a_field_or_a_row_field_has_one()
    {
        var compiled = Compile("std.price-list");
        var tree = DefinitionTree.CreateEmpty(compiled.Descriptors);
        DefinitionTree.HoldsValue(tree).ShouldBeFalse();
        DefinitionTree.HoldsValue(null).ShouldBeFalse();

        tree["lines"] = new JsonArray(DefinitionTree.EmptyRow(compiled.Descriptors, "lines"));
        DefinitionTree.HoldsValue(tree).ShouldBeFalse("a row of Missing fields is no value");

        tree["lines"] = new JsonArray(Row(compiled, row => row["description"] = Envelope(JsonValue.Create("Kaffee"))));
        DefinitionTree.HoldsValue(tree).ShouldBeTrue();

        var header = DefinitionTree.CreateEmpty(compiled.Descriptors);
        header["currency"] = Envelope(JsonValue.Create("EUR"));
        DefinitionTree.HoldsValue(header).ShouldBeTrue();

        // The coded invoice record typed in review: only its kind is set, and the kind is the record's own.
        var record = JsonSerializer.SerializeToNode(new Invoice { Kind = FieldValue<InvoiceKind>.Inferred(InvoiceKind.Invoice, FieldSource.Human, 1) }, CanonicalJson.Options);
        DefinitionTree.HoldsValue(record).ShouldBeTrue("the kind is an envelope with a value");
        DefinitionTree.HoldsValue(record, ["kind"]).ShouldBeFalse();
        var numbered = JsonSerializer.SerializeToNode(new Invoice { Kind = FieldValue<InvoiceKind>.Inferred(InvoiceKind.Invoice, FieldSource.Human, 1), InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create("RE-1")) }, CanonicalJson.Options);
        DefinitionTree.HoldsValue(numbered, ["kind"]).ShouldBeTrue();
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
    public void A_tenants_field_pattern_runs_without_backtracking_and_is_reported_like_a_schema_error()
    {
        var template = StandardTemplates.Find("std.price-list")!;
        var definition = template with
        {
            Key = "PatternProbe",
            Fields =
            [
                .. template.Fields,
                new FieldDefinition { Id = "fld_c_probe", Key = "probe", Kind = FieldKinds.String, Pattern = "^(a+)+$" },
                new FieldDefinition { Id = "fld_c_refs", Key = "refs", Kind = FieldKinds.Identifier, List = true, Pattern = "^R-[0-9]+$" },
            ],
        };
        var compiled = Compile(definition);
        compiled.JsonSchema["properties"]!["probe"]!["pattern"]!.GetValue<string>().ShouldBe("^(a+)+$", "the stored artifact keeps the keyword");
        var tree = DefinitionTree.CreateEmpty(compiled.Descriptors);
        tree["probe"] = Envelope(JsonValue.Create(new string('a', 40) + "!"));
        tree["refs"] = new JsonArray(Envelope(JsonValue.Create("R-1")), Envelope(JsonValue.Create("X-2")));
        tree["currency"] = Envelope(JsonValue.Create("Euro"));
        var values = DocumentDataView.Values(tree, compiled.Descriptors);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var flags = StructuralValidator.Validate(values, compiled);
        watch.Stop();

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "a backtracking evaluator takes hours on this value");
        var probe = flags.Single(f => f.Field == "probe");
        probe.Code.ShouldBe(FlagCodes.FieldInvalid);
        probe.Severity.ShouldBe(FlagSeverity.Warning, "an optional field");
        probe.Params!["keyword"].ShouldBe("pattern");
        flags.Single(f => f.Field == "refs[1]").Params!["keyword"].ShouldBe("pattern");
        flags.ShouldNotContain(f => f.Field == "refs[0]");
        flags.Single(f => f.Field == "currency").Params!["keyword"].ShouldBe("pattern", "the compiler's own currency pattern still applies");

        tree["probe"] = Envelope(JsonValue.Create(new string('a', 40)));
        StructuralValidator.Validate(DocumentDataView.Values(tree, compiled.Descriptors), compiled).ShouldNotContain(f => f.Field == "probe");
    }

    /// <summary>
    /// A field pattern NonBacktracking cannot run (a version stored before the publish checks refused it) matches
    /// nothing, as a syntax error does, instead of throwing NotSupportedException out of the structural check.
    /// </summary>
    [Theory]
    [InlineData(@"^(a)\1$")]
    [InlineData("^(?=P)PL$")]
    [InlineData("^P(?<=P)L$")]
    [InlineData("^(?>PL)$")]
    public void A_field_pattern_NonBacktracking_cannot_run_matches_nothing_instead_of_throwing(string pattern)
    {
        var template = StandardTemplates.Find("std.price-list")!;
        var compiled = Compile(template with
        {
            Key = "PatternProbe",
            Fields = [.. template.Fields, new FieldDefinition { Id = "fld_c_probe", Key = "probe", Kind = FieldKinds.String, Pattern = pattern }],
        });
        var tree = DefinitionTree.CreateEmpty(compiled.Descriptors);
        tree["probe"] = Envelope(JsonValue.Create("PL"));

        var flags = StructuralValidator.Validate(DocumentDataView.Values(tree, compiled.Descriptors), compiled);

        flags.Single(f => f.Field == "probe").Params!["keyword"].ShouldBe("pattern");
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

    /// <summary>
    /// A row field has a value per row, up to 20,000: the index holds the first FieldIndexLimits.MaxValuesPerField of
    /// each field and at most MaxEntriesPerDocument rows per document, every field's first value before any field's
    /// further ones (the inbox column and the duplicate check read ordinal 0).
    /// </summary>
    [Fact]
    public void The_field_index_holds_the_first_values_of_a_row_field_and_a_bounded_number_per_document()
    {
        var lines = new JsonArray();
        for (var i = 0; i < 5_000; i++)
        {
            lines.Add(new JsonObject { ["supplierSku"] = Envelope(JsonValue.Create($"sku-{i}")) });
        }

        var tree = new JsonObject { ["lines"] = lines, ["priceListNumber"] = Envelope(JsonValue.Create("PL-1")) };
        var sku = RowField(0);
        var number = new FieldDescriptor { Path = "priceListNumber", FieldId = "n", Key = "priceListNumber", Kind = FieldKinds.Identifier, Indexed = true };

        var entries = DocumentFieldIndexWriter.Entries(Guid.Empty, Guid.NewGuid(), tree.ToJsonString(), [sku, number]);

        entries.Where(e => e.FieldKey == sku.Path).Select(e => e.Ordinal).ShouldBe(Enumerable.Range(0, DocumentFieldIndexWriter.MaxValuesPerField));
        entries.Single(e => e.FieldKey == sku.Path && e.Ordinal == DocumentFieldIndexWriter.MaxValuesPerField - 1).StringValue.ShouldBe($"SKU-{DocumentFieldIndexWriter.MaxValuesPerField - 1}");
        entries.Single(e => e.FieldKey == "priceListNumber").Ordinal.ShouldBe(0);

        // Thirty indexed row fields: the document's cap holds, and the header field listed last keeps its value.
        var wide = new JsonArray();
        for (var i = 0; i < 200; i++)
        {
            var row = new JsonObject();
            for (var n = 0; n < 30; n++)
            {
                row[RowField(n).Key] = Envelope(JsonValue.Create($"v{i}"));
            }

            wide.Add(row);
        }

        var many = Enumerable.Range(0, 30).Select(RowField).Append(number).ToList();
        var bounded = DocumentFieldIndexWriter.Entries(Guid.Empty, Guid.NewGuid(), new JsonObject { ["lines"] = wide, ["priceListNumber"] = Envelope(JsonValue.Create("PL-1")) }.ToJsonString(), many);
        bounded.Count.ShouldBe(DocumentFieldIndexWriter.MaxEntriesPerDocument);
        bounded.Where(e => e.Ordinal == 0).Select(e => e.FieldKey).ShouldBe(many.Select(f => f.Path), ignoreOrder: true);
        bounded.Single(e => e.FieldKey == "priceListNumber").StringValue.ShouldBe("PL-1");
        bounded.GroupBy(e => e.FieldKey).ShouldAllBe(g => g.Count() <= DocumentFieldIndexWriter.MaxValuesPerField);
        bounded.Select(e => (e.FieldKey, e.Ordinal)).Distinct().Count().ShouldBe(bounded.Count);

        static FieldDescriptor RowField(int n)
        {
            var key = n == 0 ? "supplierSku" : $"supplierSku{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            return new FieldDescriptor { Path = $"lines[*].{key}", FieldId = $"f{n}", Key = key, Kind = FieldKinds.Identifier, Indexed = true };
        }
    }

    /// <summary>
    /// A field pattern on a row field flags at most FlagCap.MaxFlagsPerCheck rows, then stops matching and raises one
    /// FIELD_INVALID {reason: notChecked, count}; a schema error or a missing required value on every row is capped the
    /// same way per field (reason capped); the patterns draw on the validation's budget, and a value it has no room
    /// for is skipped, not failed.
    /// </summary>
    [Fact]
    public void The_structural_check_caps_its_flags_per_field_and_shares_the_pattern_budget()
    {
        var template = StandardTemplates.Find("std.price-list")!;
        var lines = template.Fields.Single(f => f.Key == "lines");
        var definition = template with
        {
            Key = "PatternRows",
            Fields =
            [
                .. template.Fields.Select(f => f.Key != "lines" ? f : lines with
                {
                    Item = lines.Item! with { Fields = [.. lines.Item!.Fields, new FieldDefinition { Id = "fld_c_code", Key = "code", Kind = FieldKinds.String, Pattern = "^[0-9]+$" }] },
                }),
            ],
        };
        var compiled = Compile(definition);
        var tree = DefinitionTree.CreateEmpty(compiled.Descriptors);
        var rows = new JsonArray();
        for (var i = 0; i < 300; i++)
        {
            var code = $"C-{i}";
            rows.Add(Row(compiled, row =>
            {
                row["code"] = Envelope(JsonValue.Create(code));
                row["packSize"] = Envelope(JsonValue.Create("many"));
            }));
        }

        tree["lines"] = rows;
        var values = DocumentDataView.Values(tree, compiled.Descriptors);
        var budget = new PatternBudget();

        var flags = StructuralValidator.Validate(values, compiled, budget);

        var pattern = flags.Where(f => f.Field is { } path && path.EndsWith(".code", StringComparison.Ordinal)).ToList();
        pattern.Count.ShouldBe(FlagCap.MaxFlagsPerCheck + 1);
        pattern.ShouldAllBe(f => f.Code == FlagCodes.FieldInvalid && f.Params!["keyword"] == "pattern");
        var notChecked = pattern[^1];
        notChecked.Params!["reason"].ShouldBe(FlagCap.NotCheckedReason);
        notChecked.Params["count"].ShouldBe("250");
        notChecked.Field.ShouldBe("lines[50].code");
        budget.Evaluations.ShouldBe(FlagCap.MaxFlagsPerCheck, "matching stopped at the cap");

        var packSize = flags.Where(f => f.Field is { } path && path.EndsWith(".packSize", StringComparison.Ordinal)).ToList();
        packSize.Count.ShouldBe(FlagCap.MaxFlagsPerCheck + 1);
        packSize[^1].ShouldSatisfyAllConditions(
            f => f.Code.ShouldBe(FlagCodes.FieldInvalid),
            f => f.Field.ShouldBe("lines[50].packSize"),
            f => f.Params!["reason"].ShouldBe(FlagCap.CappedReason),
            f => f.Params!["keyword"].ShouldBe("type"),
            f => f.Params!["count"].ShouldBe("250"));
        var sku = flags.Where(f => f.Code == FlagCodes.FieldRequired && f.Field!.EndsWith(".supplierSku", StringComparison.Ordinal)).ToList();
        sku.Count.ShouldBe(FlagCap.MaxFlagsPerCheck + 1);
        sku[^1].Severity.ShouldBe(FlagSeverity.Blocking);
        sku[^1].Params!["reason"].ShouldBe(FlagCap.CappedReason);

        // A spent budget skips the pattern instead of failing it.
        var spent = new PatternBudget(maxEvaluations: 0, maxTime: TimeSpan.FromMinutes(1));
        StructuralValidator.Validate(values, compiled, spent).ShouldNotContain(f => f.Field != null && f.Field.EndsWith(".code", StringComparison.Ordinal));
        spent.Skipped.ShouldBe(300);
        spent.SkippedFlag().ShouldNotBeNull().Code.ShouldBe(FlagCodes.ChecksSkipped);
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
