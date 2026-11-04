using System.Text.Json.Nodes;
using DocProc.Definitions;
using DocProc.Domain.Fields;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Mapping.Learning;
using DocProc.Mapping.Tables;
using DocProc.Mapping.Text;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Tests;

/// <summary>
/// The learner of docs/11 §12.6 (<c>AnchorLearner</c>) on golden cases: a label to the left, a label above, a region
/// fallback for a value without a label, the value pattern from the printed shape, a constant only where allowed, a
/// <c>column-table</c> measured from the rows' values (and the review tool's specification taken as it is), and the
/// learned mapping reading another document of the same design. Also <c>IdentifierProposer</c> (§12.9) and the
/// position-free pre-fill (§9.3 <c>layout-prefill</c>).
/// </summary>
public sealed class AnchorLearnerTests
{
    private static readonly Dictionary<string, LayoutFieldKind> Kinds = new(StringComparer.Ordinal)
    {
        ["invoiceNumber"] = new(FieldKinds.Identifier),
        ["issueDate"] = new(FieldKinds.Date),
        ["totalGross"] = new(FieldKinds.Money),
        ["supplierCustomerRef"] = new(FieldKinds.String),
        ["customer.name"] = new(FieldKinds.String),
        ["currency"] = new(FieldKinds.Currency),
    };

    private static readonly Dictionary<string, TableField> RowFields = new(StringComparer.Ordinal)
    {
        ["supplierSku"] = new("supplierSku", FieldKinds.String),
        ["description"] = new("description", FieldKinds.Text),
        ["quantity"] = new("quantity", FieldKinds.Decimal),
        ["lineNet"] = new("lineNet", FieldKinds.Money),
    };

    /// <summary>One invoice of a design, its values shifted by <paramref name="dy"/> and with other values, as a second document of the design.</summary>
    private static DocumentWords Invoice(string number = "RE-2026-0815", string date = "28.10.2026", string reference = "123 456789", double dy = 0, int rows = 3) =>
        IdentificationRuleTests.Words(Page(number, date, reference, dy, rows));

    private static PageBuilder Page(string number, string date, string reference, double dy, int rows)
    {
        var page = new PageBuilder(1)
            .Text(60, 40, "Nordwind Großhandel GmbH").Text(60, 55, "USt-IdNr. DE 811 111 111")
            .Text(60, 120 + dy, "Gasthaus zur Post GmbH")
            .Text(60, 200, "Rechnung")
            .Text(60, 240 + dy, "Rechnungs-Nr.").Text(180, 240 + dy, number)
            .Text(60, 255 + dy, "Datum:").Text(180, 255 + dy, date)
            .Text(640, 92, "Kunden-Nr.").Text(640, 107, reference)
            .Line(300 + dy, (60, "Art.-Nr."), (160, "Bezeichnung"), (500, "Menge"), (700, "Betrag"));
        for (var i = 0; i < rows; i++)
        {
            page.Line(316 + dy + (i * 16), (60, $"10023{i}"), (160, $"Artikel {i}"), (500, $"{i + 2}"), (700, $"{(i + 1) * 10},00"));
        }

        return page.Text(60, 316 + dy + (rows * 16) + 10, "Summe netto 60,00").Text(60, 800, "Gesamtbetrag 1.487,50 EUR");
    }

    private static LearnSample Sample(DocumentWords words, string path, string text, JsonNode value, bool allowConstant = false)
    {
        var place = LayoutPrefill.Find(words, text).ShouldNotBeNull($"'{text}' is printed");
        return new LearnSample(path, Kinds[path].Kind, place.Text, value, place.Page, place.Box, AllowConstant: allowConstant);
    }

    private static LearnTable Table(DocumentWords words, int rows)
    {
        var page = words.Page(1)!;
        var lines = PageGeometry.Lines(page.Words).Where(l => l.Words[0].Text.StartsWith("10023", StringComparison.Ordinal)).Take(rows).ToList();
        var list = lines.Select(l => (IReadOnlyList<LearnTableCell>)
        [
            new LearnTableCell("supplierSku", 1, l.Words[0].Box, l.Words[0].Text),
            new LearnTableCell("description", 1, PageGeometry.Union(l.Words.Skip(1).Take(2))!.Value, "Artikel"),
            new LearnTableCell("quantity", 1, l.Words[3].Box, l.Words[3].Text),
            new LearnTableCell("lineNet", 1, l.Words[4].Box, l.Words[4].Text),
        ]).ToList();
        return new LearnTable("lines", list, RowFields, "supplierSku");
    }

    private static LearnResult LearnFromFirst()
    {
        var words = Invoice();
        return AnchorLearner.Learn(new LearnRequest(
            words,
            [
                Sample(words, "invoiceNumber", "RE-2026-0815", JsonValue.Create("RE-2026-0815")),
                Sample(words, "issueDate", "28.10.2026", JsonValue.Create("2026-10-28")),
                Sample(words, "supplierCustomerRef", "123 456789", JsonValue.Create("123 456789")),
                Sample(words, "customer.name", "Gasthaus zur Post GmbH", JsonValue.Create("Gasthaus zur Post GmbH")),
                new LearnSample("currency", FieldKinds.Currency, null, JsonValue.Create("EUR"), null, null, AllowConstant: true),
                new LearnSample("totalGross", FieldKinds.Money, null, JsonValue.Create(1487.50m), null, null),
            ],
            [Table(words, 3)],
            ValueLocale.German));
    }

    private static LayoutExtraction Run(JsonObject mapping, DocumentWords words) =>
        LayoutEngine.Run(LayoutMapping.Parse(mapping.ToJsonString()), words, "learned@1", p => Kinds.GetValueOrDefault(p), p => p == "lines" ? RowFields : null, ValueLocale.German, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public void A_label_to_the_left_becomes_label_right_with_a_shape_pattern_and_a_region_fallback()
    {
        var result = LearnFromFirst();

        var number = result.Fields.Single(f => f.Path == "invoiceNumber");
        number.Strategies.ShouldBe([LayoutStrategies.LabelRight, LayoutStrategies.Region]);
        number.Label.ShouldBe("Rechnungs-Nr.");
        number.Pattern.ShouldBe("[A-Z]{2}-[0-9]{4}-[0-9]{4}");
        number.Verified.ShouldBeTrue();
        result.Fields.Single(f => f.Path == "issueDate").Label.ShouldBe("Datum:");
        result.Fields.Single(f => f.Path == "issueDate").Pattern.ShouldBeNull("a date is read by its kind");
    }

    [Fact]
    public void A_label_above_becomes_label_below_and_a_value_without_a_label_is_learned_by_its_region()
    {
        var result = LearnFromFirst();

        var reference = result.Fields.Single(f => f.Path == "supplierCustomerRef");
        reference.Strategies[0].ShouldBe(LayoutStrategies.LabelBelow);
        reference.Label.ShouldBe("Kunden-Nr.");
        reference.Pattern.ShouldBe("[0-9]{3}\\s?[0-9]{6}", "123 456789 gives the pattern of docs/11 §12.6");

        result.Fields.Single(f => f.Path == "customer.name").Strategies.ShouldBe([LayoutStrategies.Region]);
    }

    [Fact]
    public void A_value_without_a_place_is_a_constant_only_where_allowed()
    {
        var result = LearnFromFirst();

        result.Fields.Single(f => f.Path == "currency").Strategies.ShouldBe([LayoutStrategies.Constant]);
        result.Skipped.Single(s => s.Path == "totalGross").Reason.ShouldContain("no place");
        result.Mapping["fields"]!["currency"]!["rules"]![0]!["value"]!.GetValue<string>().ShouldBe("EUR");
    }

    [Fact]
    public void Rows_become_a_column_table_with_headers_and_a_stop_line()
    {
        var result = LearnFromFirst();

        var lines = result.Fields.Single(f => f.Path == "lines");
        lines.Strategies.ShouldBe([LayoutStrategies.ColumnTable]);
        lines.Verified.ShouldBeTrue("the column table reads the three rows back");
        var rule = result.Mapping["fields"]!["lines"]!.AsObject();
        rule["rowAnchor"]!.GetValue<string>().ShouldBe("supplierSku");
        rule["columns"]!.AsArray().Select(c => c!["header"]!.GetValue<string>()).ShouldBe(["Art.-Nr.", "Bezeichnung", "Menge", "Betrag"]);
        rule["stopAt"]![0]!.GetValue<string>().ShouldBe("^Summe");
        rule["headerRow"].ShouldNotBeNull();
    }

    [Fact]
    public void The_learned_mapping_reads_another_document_of_the_design()
    {
        var mapping = LearnFromFirst().Mapping;
        var other = Invoice("RE-2026-0901", "03.11.2026", "123 456789", dy: 6, rows: 5);

        var run = Run(mapping, other);

        run.Hits.Single(h => h.Path == "invoiceNumber").Value!.GetValue<string>().ShouldBe("RE-2026-0901");
        run.Hits.Single(h => h.Path == "issueDate").Value!.GetValue<string>().ShouldBe("2026-11-03");
        run.Hits.Single(h => h.Path == "supplierCustomerRef").Text.ShouldBe("123 456789");
        run.Tables["lines"].Rows.Count.ShouldBe(5, "the header is found again and every row of the longer table is read");
        run.Drift.IsDrift.ShouldBeFalse();
    }

    [Fact]
    public void A_review_tool_specification_is_taken_as_the_column_table()
    {
        var spec = JsonNode.Parse("""
            { "page": 1, "bBox": { "x0": 50, "y0": 295, "x1": 800, "y1": 400 }, "path": "lines", "rowAnchor": "supplierSku",
              "headerRow": { "y0": 298, "y1": 312 }, "pages": "1-*", "stopAt": ["^Summe"],
              "columns": [ { "x0": 50, "x1": 150, "field": "supplierSku", "header": "Art.-Nr." }, { "x0": 150, "x1": 490, "field": "description", "header": "Bezeichnung", "tierMinQuantity": null },
                           { "x0": 490, "x1": 690, "field": "quantity", "header": "Menge" }, { "x0": 690, "x1": 800, "field": "lineNet", "header": "Betrag" } ] }
            """)!.AsObject();

        var rule = AnchorLearner.FromSpecification(spec);

        rule["strategy"]!.GetValue<string>().ShouldBe(LayoutStrategies.ColumnTable);
        rule["bBox"]!.AsArray().Select(n => n!.GetValue<double>()).ShouldBe([50, 295, 800, 400]);
        rule.ContainsKey("pages").ShouldBeFalse("1-* reads every page");
        rule["columns"]![1]!.AsObject().ContainsKey("tierMinQuantity").ShouldBeFalse();
        var words = Invoice(rows: 4);
        var run = Run(new JsonObject { ["fields"] = new JsonObject { ["lines"] = rule } }, words);
        run.Tables["lines"].Rows.Count.ShouldBe(4);
    }

    [Fact]
    public void A_specification_without_a_stop_line_takes_the_line_under_the_confirmed_rows()
    {
        // The review tool read the rows inside its box (no stop line); the layout reads to the end of the page, where the
        // web shop's footer "Fortsetzung umseitig" was read as a third row in the compose demo.
        var words = Invoice(rows: 2);
        var table = Table(words, 2);
        var spec = JsonNode.Parse("""
            { "page": 1, "bBox": { "x0": 50, "y0": 295, "x1": 800, "y1": 340 }, "path": "lines", "rowAnchor": "supplierSku",
              "headerRow": { "y0": 298, "y1": 312 }, "pages": "page",
              "columns": [ { "x0": 50, "x1": 150, "field": "supplierSku", "header": "Art.-Nr." }, { "x0": 150, "x1": 490, "field": "description", "header": "Bezeichnung" },
                           { "x0": 490, "x1": 690, "field": "quantity", "header": "Menge" }, { "x0": 690, "x1": 800, "field": "lineNet", "header": "Betrag" } ] }
            """)!.AsObject();

        var result = AnchorLearner.Learn(new LearnRequest(words, [], [table with { Specification = spec }], ValueLocale.German));

        var rule = result.Mapping["fields"]!["lines"]!.AsObject();
        rule["stopAt"]![0]!.GetValue<string>().ShouldBe("^Summe");
        result.Fields.Single(f => f.Path == "lines").Verified.ShouldBeTrue("the rule reads the two confirmed rows back, not the total line");
    }

    [Theory]
    [InlineData(FieldKinds.String, "123 456789", "[0-9]{3}\\s?[0-9]{6}")]
    [InlineData(FieldKinds.Identifier, "WS-2026-000318", "[A-Z]{2}-[0-9]{4}-[0-9]{6}")]
    [InlineData(FieldKinds.Identifier, "Re4711b", "[A-Za-z]{2}[0-9]{4}[A-Za-z]{1}")]
    [InlineData(FieldKinds.String, "Gasthaus zur Post", null)]
    [InlineData(FieldKinds.Money, "12,50", null)]
    [InlineData(FieldKinds.Date, "28.10.2026", null)]
    public void The_value_pattern_follows_the_printed_shape(string kind, string printed, string? pattern) =>
        AnchorLearner.ShapePattern(kind, printed).ShouldBe(pattern);

    [Fact]
    public void A_selection_in_the_title_band_is_a_title_identifier_and_the_vat_id_a_supplier_value()
    {
        var words = Invoice();
        var title = words.Page(1)!.Words.Single(w => w.Text == "Rechnung");
        var supplier = new SupplierValues(VatId: "DE811111111");

        var proposal = IdentifierProposer.Propose(words, 1, null, [title.Index], tenantScope: false, supplier).ShouldNotBeNull();
        proposal.Identifier.ToJsonString().ShouldBe("""{"text":"Rechnung","page":1,"region":"title"}""");
        proposal.Holds.ShouldBeTrue();

        var vatBox = LayoutPrefill.Find(words, "DE 811 111 111")!.Value.Box;
        var vat = IdentifierProposer.Propose(words, 1, vatBox, null, tenantScope: false, supplier).ShouldNotBeNull();
        vat.Identifier.ToJsonString().ShouldBe("""{"supplierValue":"vatId","page":1}""");
        vat.Holds.ShouldBeTrue();

        var tenant = IdentifierProposer.Propose(words, 1, vatBox, null, tenantScope: true, supplier).ShouldNotBeNull();
        tenant.Identifier["text"]!.GetValue<string>().ShouldBe("DE 811 111 111");
        tenant.Warnings.ShouldContain(w => w.Contains("vatId", StringComparison.Ordinal), "a tenant rule never proposes supplierValue");
    }

    [Fact]
    public void A_proposed_supplier_attribute_in_a_values_row_holds_on_its_own_document()
    {
        // docs/11 §12.10: identifiers:propose turns a selected scalar attribute into supplierValue attributes.<key>; the
        // proposal must hold on the document it was proposed from, also where a date and a number follow in their columns.
        var words = new DocumentWords([new PageBuilder(1)
            .Text(60, 40, "Nordwind Großhandel GmbH")
            .Line(300, (60, "Kundennr."), (300, "Datum"), (500, "Rechnungsnr."))
            .Line(315, (60, "12345"), (300, "04.03.2027"), (500, "RE-1001"))
            .Words()], false);
        var supplier = new SupplierValues(Attributes: new Dictionary<string, string>(StringComparer.Ordinal) { ["customerNo"] = "12345" });
        var customer = words.Page(1)!.Words.Single(w => w.Text == "12345");

        var proposal = IdentifierProposer.Propose(words, 1, null, [customer.Index], tenantScope: false, supplier).ShouldNotBeNull();

        proposal.Identifier.ToJsonString().ShouldBe("""{"supplierValue":"attributes.customerNo","page":1}""");
        proposal.Holds.ShouldBeTrue();
    }

    [Fact]
    public void A_selection_below_the_title_band_gets_a_box_around_it()
    {
        var words = Invoice();
        var box = LayoutPrefill.Find(words, "Gesamtbetrag")!.Value.Box;

        var proposal = IdentifierProposer.Propose(words, 1, new BoundingBox(box.X0 - 1, box.Y0 - 1, box.X1 + 1, box.Y1 + 1), null, tenantScope: false, null).ShouldNotBeNull();

        proposal.Identifier["text"]!.GetValue<string>().ShouldBe("Gesamtbetrag");
        proposal.Identifier["bBox"].ShouldNotBeNull();
        proposal.Identifier.ContainsKey("region").ShouldBeFalse();
        proposal.Holds.ShouldBeTrue();
    }

    [Fact]
    public void The_proposed_rule_takes_the_type_keyword_of_the_title_and_the_vat_id_where_printed()
    {
        var words = Invoice();

        var proposal = IdentifierProposer.ProposeRule(words, ["Rechnung", "Invoice"], new SupplierValues(VatId: "DE811111111")).ShouldNotBeNull();

        proposal.Holds.ShouldBeTrue();
        var rule = IdentificationRule.Parse(proposal.Rule);
        rule.Identifiers.Select(i => i.Identifier.Kind).ShouldBe([IdentifierKind.Text, IdentifierKind.SupplierValue]);
        rule.Identifiers[0].Identifier.Value.ShouldBe("Rechnung");
        proposal.Describe.ShouldContain("Rechnung");
    }

    [Fact]
    public void The_prefill_searches_other_layouts_labels_anywhere_and_locates_master_data()
    {
        var erp = LayoutMapping.Parse("""
            { "fields": {
                "invoiceNumber": { "rules": [ { "strategy": "label-right", "labels": ["Rechnungs-Nr."], "page": 1 }, { "strategy": "region", "page": 1, "bBox": [900, 900, 950, 920] } ] },
                "issueDate": { "rules": [ { "strategy": "region", "page": 1, "bBox": [180, 255, 260, 265] } ] } } }
            """);
        var mapping = LayoutPrefill.FromLayouts([erp]);
        mapping.Fields.Single().Path.ShouldBe("invoiceNumber");
        mapping.Fields.Single().Rules.Single().Page.ShouldBeNull("positions are ignored");

        var words = Invoice(dy: 40);
        var hits = LayoutPrefill.Search(mapping, words, p => Kinds.GetValueOrDefault(p), ValueLocale.German, TestContext.Current.CancellationToken);
        var number = hits.Single();
        number.Value!.GetValue<string>().ShouldBe("RE-2026-0815");
        number.Inferred.ShouldBeTrue();
        number.RuleRef.ShouldBe(PrefillSources.OtherLayout);

        var located = LayoutPrefill.Locate(words, [new PrefillValue("supplier.vatId", FieldKinds.String, "DE811111111", PrefillSources.MasterData), new PrefillValue("supplierCustomerRef", FieldKinds.String, "999", PrefillSources.KnownValue)], ValueLocale.German);
        var vat = located.Single();
        vat.Path.ShouldBe("supplier.vatId");
        vat.Text.ShouldBe("DE 811 111 111");
        vat.Page.ShouldBe(1);
        vat.Box.ShouldNotBeNull();
    }

    [Fact]
    public void The_prefill_searches_the_types_own_labels_and_skips_a_value_without_a_digit()
    {
        // A bilingual web-shop box: "Rechnungsnummer / Invoice no.:" with the number far to the right.
        var words = IdentificationRuleTests.Words(new PageBuilder(1)
            .Text(330, 54, "RECHNUNG / INVOICE")
            .Text(306, 96, "Rechnungsnummer / Invoice no.:").Text(500, 96, "WS-2026-000318")
            .Text(306, 128, "Kundennummer / Customer no.:").Text(520, 128, "401187")
            .Text(60, 300, "Gesamt / Total").Text(500, 300, "1.487,50 EUR"));
        var fields = new[]
        {
            // The Invoice template's display labels for its number, its date, and a custom field's extraction labels.
            new DefinitionLabels("invoiceNumber", FieldKinds.Identifier, ["Rechnungsnummer", "Invoice no."]),
            new DefinitionLabels("issueDate", FieldKinds.Date, ["Rechnungsdatum", "Invoice date"]),
            new DefinitionLabels("supplierCustomerRef", FieldKinds.Identifier, ["Kundennummer", "Kunden-Nr.", "Customer No."]),
            new DefinitionLabels("customer.name", FieldKinds.String, ["Kunde"]),
        };
        var kinds = new Dictionary<string, LayoutFieldKind>(Kinds) { ["supplierCustomerRef"] = new(FieldKinds.Identifier) };

        var hits = LayoutPrefill.SearchDefinitionLabels(fields, words, p => kinds.GetValueOrDefault(p), ValueLocale.German, TestContext.Current.CancellationToken);

        var number = hits.Single(h => h.Path == "invoiceNumber");
        number.Value!.GetValue<string>().ShouldBe("WS-2026-000318", "'Rechnungsnummer' reads '/ Invoice no.: WS-…', which holds a word of another label; 'Invoice no.' reads the number");
        number.RuleRef.ShouldBe(PrefillSources.DefinitionLabel);
        number.Inferred.ShouldBeTrue();
        number.Page.ShouldBe(1);
        hits.Single(h => h.Path == "supplierCustomerRef").Value!.GetValue<string>().ShouldBe("401187");
        hits.ShouldNotContain(h => h.Path == "issueDate", "no date label is printed");
        hits.ShouldNotContain(h => h.Path == "customer.name", "only kinds that carry a digit are searched by the type's labels");
    }
}
