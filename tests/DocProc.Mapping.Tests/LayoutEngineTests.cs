using System.Diagnostics;
using System.Text.RegularExpressions;
using DocProc.Definitions;
using DocProc.Domain.Validation;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Tests;

/// <summary>
/// The field mapping of a layout (docs/11 §8.1, §8.2, §8.6): labels to the right and below, regions, patterns
/// anywhere, constants, transforms, the value read by the field's kind, rule order, the drift check, and a
/// <c>column-table</c> learned on one document read across the pages of another one of the design.
/// </summary>
public sealed class LayoutEngineTests
{
    private static readonly Dictionary<string, LayoutFieldKind> Kinds = new(StringComparer.Ordinal)
    {
        ["invoiceNumber"] = new(FieldKinds.Identifier),
        ["issueDate"] = new(FieldKinds.Date),
        ["totalGross"] = new(FieldKinds.Money),
        ["supplierCustomerRef"] = new(FieldKinds.String),
        ["currency"] = new(FieldKinds.Currency),
        ["supplier.vatId"] = new(FieldKinds.String),
        ["dueDate"] = new(FieldKinds.Date),
    };

    private static DocumentWords Invoice(double shift = 0) => IdentificationRuleTests.Words(
        new PageBuilder(1)
            .Text(60, 40, "Nordwind Großhandel GmbH").Text(60, 55, "USt-IdNr. DE 811 111 111")
            .Text(60, 200, "Rechnung")
            .Text(60 + shift, 240, "Rechnungs-Nr.").Text(180 + shift, 240, "RE-2026-0815")
            .Text(60 + shift, 255, "Datum:").Text(180 + shift, 255, "28.10.2026")
            .Text(640, 92, "Kunden-Nr.").Text(640, 107, "123 456789")
            .Text(60, 800, "Gesamtbetrag 1.487,50 EUR"));

    private static LayoutExtraction Run(string mapping, DocumentWords words, IReadOnlyCollection<string>? required = null) =>
        LayoutEngine.Run(LayoutMapping.Parse(mapping), words, "nordwind-invoice-erp@1", p => Kinds.GetValueOrDefault(p), _ => null, ValueLocale.German, required, TestContext.Current.CancellationToken);

    [Fact]
    public void Each_strategy_reads_its_value_by_the_fields_kind_with_its_place_and_rule()
    {
        const string mapping = """
            { "fields": {
                "invoiceNumber": { "rules": [ { "strategy": "label-right", "labels": ["Rechnungs-Nr.", "Rechnungsnummer"], "page": 1, "pattern": "RE-\\d{4}-\\d{4}" } ] },
                "issueDate": { "rules": [ { "strategy": "label-right", "labels": ["Datum"] } ] },
                "supplierCustomerRef": { "rules": [ { "strategy": "label-below", "labels": ["Kunden-Nr."] } ], "transforms": [ { "op": "removeWhitespace" } ] },
                "totalGross": { "rules": [ { "strategy": "regex-anywhere", "pattern": "Gesamtbetrag\\s+([\\d.,]+ EUR)", "group": 1 } ] },
                "supplier.vatId": { "rules": [ { "strategy": "region", "page": 1, "bBox": [55, 50, 300, 65] } ], "transforms": [ { "op": "regex", "pattern": "DE[\\d ]+" }, { "op": "removeWhitespace" } ] },
                "currency": { "rules": [ { "strategy": "constant", "value": "EUR" } ] } } }
            """;
        var result = Run(mapping, Invoice());

        var hits = result.Hits.ToDictionary(h => h.Path, StringComparer.Ordinal);
        hits["invoiceNumber"].Value!.GetValue<string>().ShouldBe("RE-2026-0815");
        hits["invoiceNumber"].Confidence.ShouldBe(LayoutEngine.LabelAndPattern);
        hits["invoiceNumber"].RuleRef.ShouldBe("nordwind-invoice-erp@1#invoiceNumber[0]");
        hits["invoiceNumber"].Page.ShouldBe(1);
        hits["invoiceNumber"].Box!.Value.X0.ShouldBe(180);
        hits["issueDate"].Value!.GetValue<string>().ShouldBe("2026-10-28");
        hits["issueDate"].Confidence.ShouldBe(LayoutEngine.LabelOnly);
        hits["supplierCustomerRef"].Value!.GetValue<string>().ShouldBe("123456789");
        hits["supplierCustomerRef"].Text.ShouldBe("123 456789", "the printed text is kept, the transform applies to the value");
        var total = hits["totalGross"].Value!.AsObject();
        total["amount"]!.GetValue<decimal>().ShouldBe(1487.50m);
        total["currency"]!.GetValue<string>().ShouldBe("EUR");
        hits["supplier.vatId"].Value!.GetValue<string>().ShouldBe("DE811111111");
        hits["supplier.vatId"].Confidence.ShouldBe(LayoutEngine.RegionOnly);
        hits["currency"].Inferred.ShouldBeTrue("a constant is not printed");
        result.Drift.IsDrift.ShouldBeFalse();
        result.Drift.Found.ShouldBe(6);
    }

    [Fact]
    public void Rules_are_tried_in_order_and_a_value_that_does_not_read_as_the_kind_falls_through()
    {
        const string mapping = """
            { "fields": { "issueDate": { "rules": [
                { "strategy": "label-right", "labels": ["Rechnungs-Nr."] },
                { "strategy": "region", "page": 1, "bBox": [180, 255, 260, 265], "tolerance": 2 } ] } } }
            """;
        var hit = Run(mapping, Invoice()).Hits.ShouldHaveSingleItem();
        hit.Strategy.ShouldBe(LayoutStrategies.Region, "RE-2026-0815 is no date, so the region rule reads the date");
        hit.RuleRef.ShouldEndWith("#issueDate[1]");
    }

    [Fact]
    public void Missing_fields_and_a_missing_required_field_are_drift()
    {
        const string mapping = """
            { "fields": {
                "invoiceNumber": { "rules": [ { "strategy": "label-right", "labels": ["Rechnungs-Nr."] } ] },
                "dueDate": { "rules": [ { "strategy": "label-right", "labels": ["Fällig am"] } ] } },
              "requiredFields": ["invoiceNumber"] }
            """;
        var ok = Run(mapping, Invoice());
        ok.Drift.IsDrift.ShouldBeTrue("one of two covered fields is below 70 %");
        ok.Drift.Missing.ShouldBe(["dueDate"]);
        ok.Drift.RequiredMissing.ShouldBeEmpty();

        // The positions moved (a redesign that kept the title): the required number is not found.
        var moved = IdentificationRuleTests.Words(new PageBuilder(1).Text(60, 200, "Rechnung").Text(60, 240, "Beleg").Text(180, 240, "RE-2026-0815"));
        var drift = Run(mapping, moved);
        drift.Drift.IsDrift.ShouldBeTrue();
        drift.Drift.RequiredMissing.ShouldBe(["invoiceNumber"]);
    }

    [Theory]
    [InlineData("""{ "fields": { "a": { "rules": [ { "strategy": "xml-path", "path": "x" } ] } } }""", "not run yet")]
    [InlineData("""{ "fields": { "a": { "rules": [ { "strategy": "guess" } ] } } }""", "not a strategy")]
    [InlineData("""{ "fields": { "a": { "rules": [ { "strategy": "label-right" } ] } } }""", "label")]
    [InlineData("""{ "fields": { "a": { "rules": [ { "strategy": "regex-anywhere", "pattern": "(a)\\1" } ] } } }""", "does not compile")]
    [InlineData("""{ "fields": { "a": { "rules": [ { "strategy": "regex-anywhere", "pattern": "(.{0,15}a){30}x" } ] } } }""", "combination of lengths")]
    [InlineData("""{ "fields": { "a": { "rules": [ { "strategy": "constant", "value": "x" } ], "transforms": [ { "op": "regex", "pattern": "(.{0,15}a){30}x" } ] } } }""", "combination of lengths")]
    [InlineData("""{ "fields": { "a": { "rules": [ { "strategy": "region", "bBox": [10, 10, 5, 5] } ] } } }""", "x0 < x1")]
    [InlineData("""{ "fields": { "a": { "rules": [ { "strategy": "constant", "value": "x" } ], "transforms": [ { "op": "parseNumber" } ] } } }""", "not a transform")]
    [InlineData("""{ "fields": { "lines.x": { "strategy": "column-table", "bBox": [0, 0, 10, 10], "columns": [ { "x0": 0, "x1": 5 } ] } } }""", "top-level")]
    public void A_mapping_that_does_not_read_is_refused_with_its_place(string json, string message)
    {
        LayoutMapping.TryParse(json, out var error).ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain(message);
    }

    /// <summary>
    /// A table learned on a one-page list (header at y 100) read on another list of the design whose header sits at y
    /// 160 and repeats on page 2 at y 60: the header is found by its texts, both pages are read and reported.
    /// </summary>
    [Fact]
    public void A_column_table_finds_its_header_again_and_reads_every_page_with_a_table_report()
    {
        var columns = string.Join(", ", PriceList.Dividers.Select(d =>
            $$"""{ "x0": {{d.X0}}, "x1": {{d.X1}}, "header": "{{d.Header}}"{{(d.Field is null ? string.Empty : $", \"field\": \"{d.Field}\"")}}{{(d.Tier is null ? string.Empty : $", \"tierMinQuantity\": {d.Tier}")}} }"""));
        var mapping = LayoutMapping.Parse($$"""
            { "fields": { "lines": { "strategy": "column-table", "page": "*", "bBox": [40, 95, 995, 940], "headerRow": { "y0": 99, "y1": 111 },
                "columns": [ {{columns}} ], "rowAnchor": "supplierSku", "groupHeader": { "target": "category" },
                "tierColumns": { "headerPattern": "ab\\s+(\\d+)" }, "stopAt": ["^Ende der Preisliste"] } } }
            """);
        var table = mapping.Tables.ShouldHaveSingleItem();
        table.Path.ShouldBe("lines");

        var words = IdentificationRuleTests.Words(
            new PageBuilder(1).Text(55, 40, "Nordwind Preisliste 2028").Text(55, 60, "gültig ab 01.01.2028").Header(160)
                .Article(180, "100234", "Rioja Crianza", "13,49", "12,00", "11,40").Article(195, "100235", "Rioja Reserva", "19,90"),
            new PageBuilder(2).Header(60).Text(PriceList.Sku, 80, "Weißweine aus Deutschland").Article(95, "200001", "Riesling trocken", "8,90").Text(55, 120, "Ende der Preisliste").Article(135, "999999", "Nach dem Ende", "1,00"));

        var result = LayoutEngine.Table(table, PriceList.Fields, words, ValueLocale.German, TestContext.Current.CancellationToken).ShouldNotBeNull();
        result.Rows.Select(r => r.Cell("supplierSku")!.Text).ShouldBe(["100234", "100235", "200001"]);
        result.Rows[2].Cell("category")!.Text.ShouldBe("Weißweine aus Deutschland");
        result.Rows[0].Tiers.Select(t => t.MinQuantity).ShouldBe([10m, 50m]);
        result.Report.Pages.ShouldBe([1, 2]);
        result.Report.StoppedOnPage.ShouldBe(2);
        result.Report.PerPage.All(p => p.HeaderFound).ShouldBeTrue();
        result.Report.RejectedRows.ShouldBe(0);

        var none = IdentificationRuleTests.Words(new PageBuilder(1).Text(55, 40, "Rechnung").Text(55, 100, "Pos Artikel Menge"));
        LayoutEngine.Table(table, PriceList.Fields, none, ValueLocale.German, TestContext.Current.CancellationToken).ShouldBeNull("no line holds three of the headers");

        var run = LayoutEngine.Run(mapping, none, "x@1", _ => null, p => p == "lines" ? PriceList.Fields : null, ValueLocale.German, cancellationToken: TestContext.Current.CancellationToken);
        run.Drift.IsDrift.ShouldBeTrue("the only covered path, the table, read nothing");
        run.Drift.Missing.ShouldBe(["lines"]);
    }

    [Fact]
    public void Transforms_apply_in_order()
    {
        LayoutEngine.Transform(" kd 12 34 ", [new LayoutTransform("trim"), new LayoutTransform("uppercase"), new LayoutTransform("removeWhitespace")]).ShouldBe("KD1234");
        LayoutEngine.Transform("Markt 0815 Nord", [new LayoutTransform("regex", @"Markt\s+(\d+)", 1)]).ShouldBe("0815");
        LayoutEngine.Transform("12-34", [new LayoutTransform("replace", "-", With: string.Empty)]).ShouldBe("1234");
        LayoutEngine.Transform("none here", [new LayoutTransform("regex", @"\d+")]).ShouldBeNull();
    }

    /// <summary>
    /// The pattern bound is what keeps a match short, not the match timeout (the engine does not check it while it
    /// builds its automaton): a pattern at the bound's limit, on a fresh instance, answers or reports a timeout within a
    /// few seconds over 20,000 characters (a few hundred milliseconds on an idle machine), where
    /// <c>(.{0,15}a){30}x</c>, now refused, ran about 11 s and answered "no match".
    /// </summary>
    [Fact]
    public void A_pattern_at_the_bound_finishes_a_first_match_quickly()
    {
        var pattern = "(.{0,16}a){4}" + string.Concat(Enumerable.Repeat(".{0,15}a", 26)) + "x";
        PatternSize.IsSmallEnough(pattern).ShouldBeTrue();
        PatternSize.Variation(pattern).ShouldBe(PatternSize.MaxVariation);
        var random = new Random(7);
        var text = new string([.. Enumerable.Range(0, 20_000).Select(_ => "abc"[random.Next(3)])]);
        var regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, IdentificationRuleEvaluator.PatternTimeout);

        var watch = Stopwatch.StartNew();
        try
        {
            regex.IsMatch(text).ShouldBeFalse();
        }
        catch (RegexMatchTimeoutException)
        {
            // Reported as a timeout by the caller, which is a bound too.
        }

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void The_coverage_lists_every_mapped_field_and_table()
    {
        var mapping = LayoutMapping.Parse("""
            { "fields": { "invoiceNumber": { "rules": [ { "strategy": "constant", "value": "x" } ] },
                          "lines": { "strategy": "column-table", "bBox": [0, 0, 500, 500], "columns": [ { "x0": 0, "x1": 100, "field": "supplierSku" } ] } },
              "defaults": { "currency": { "value": "EUR" }, "ext.costCentre": { "fromSupplierAttribute": "defaultCostCentre" } },
              "requiredFields": ["invoiceNumber"], "locale": "de-DE" }
            """);
        mapping.Coverage.ShouldBe(["invoiceNumber", "lines"]);
        mapping.Defaults.Select(d => d.Path).ShouldBe(["currency", "ext.costCentre"]);
        mapping.RequiredFields.ShouldBe(["invoiceNumber"]);
        mapping.Culture.ShouldBe("de-DE");
        mapping.Tables[0].RowAnchor.ShouldBe("supplierSku");
        LayoutMapping.Parse(null).Fields.ShouldBeEmpty();
    }
}
