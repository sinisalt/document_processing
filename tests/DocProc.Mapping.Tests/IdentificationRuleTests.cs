using System.Text.Json;
using DocProc.Domain.Text;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Text;

namespace DocProc.Mapping.Tests;

/// <summary>
/// Identification rules (docs/11 §8.5): the format and its meta-schema, the strict evaluator (whole words, regions,
/// inherited locations, all/any/none, supplier values compared compacted, source facts, the bounded OCR tolerance),
/// and the checks at save.
/// </summary>
public sealed class IdentificationRuleTests
{
    private const string NordwindVat = "DE811111111";

    private static readonly SupplierValues Nordwind = new(VatId: NordwindVat, Ibans: ["DE02120300000000202051"], Attributes: new Dictionary<string, string>(StringComparer.Ordinal) { ["customerNo"] = "123 456789" });

    /// <summary>Nordwind's ERP invoice: "Rechnung" in the title area, the VAT id spaced in the letterhead, a footer on page 2.</summary>
    private static DocumentWords ErpInvoice(bool ocr = false, string title = "Rechnung") => new(
    [
        new PageBuilder(1).Text(60, 40, "Nordwind Großhandel GmbH").Text(60, 60, "USt-IdNr.: DE 811 111 111").Text(60, 200, title).Text(60, 400, "Pos Artikel Menge").Words(),
        new PageBuilder(2).Text(60, 100, "Summe netto 1.250,00").Text(60, 900, "Seite 2 von 2").Words(),
    ], ocr);

    private static IdentificationResult Eval(string json, DocumentWords words, SupplierValues? supplier = null, SourceFacts? source = null) =>
        IdentificationRuleEvaluator.Evaluate(IdentificationRule.Parse(json), new IdentificationContext(words, supplier ?? Nordwind, source));

    [Fact]
    public void The_example_rule_of_section_8_5_holds_for_the_ERP_invoice_with_evidence_per_identifier()
    {
        const string rule = """
            { "all": [ { "text": "Rechnung", "page": 1, "region": "title" }, { "supplierValue": "vatId", "page": 1 } ],
              "none": [ { "text": "Rechnungskorrektur", "page": 1 }, { "text": "Gutschrift", "page": 1 } ] }
            """;
        var result = Eval(rule, ErpInvoice());

        result.Holds.ShouldBeTrue();
        result.Failed.ShouldBeEmpty();
        result.Evidence.Select(e => e.Path).ShouldBe(["all[0]", "all[1]"]);
        result.Evidence[0].Page.ShouldBe(1);
        result.Evidence[0].Text.ShouldBe("Rechnung");
        result.Evidence[1].Text.ShouldBe("DE 811 111 111", "the VAT id is found spaced, compared compacted");
        result.Evidence[1].Box.ShouldNotBeNull();
    }

    [Fact]
    public void Text_is_whole_words_case_insensitive_unless_asked_and_punctuation_around_a_word_does_not_count()
    {
        Eval("""{ "all": [ { "text": "RECHNUNG" } ] }""", ErpInvoice()).Holds.ShouldBeTrue();
        Eval("""{ "all": [ { "text": "RECHNUNG", "caseSensitive": true } ] }""", ErpInvoice()).Holds.ShouldBeFalse();
        Eval("""{ "all": [ { "text": "Rechnung" } ] }""", ErpInvoice(title: "Rechnungskorrektur")).Holds.ShouldBeFalse("whole words only");
        Eval("""{ "all": [ { "text": "Rechnung" } ] }""", ErpInvoice(title: "Rechnung:")).Holds.ShouldBeTrue();
        Eval("""{ "all": [ { "text": "Summe  netto" } ] }""", ErpInvoice()).Holds.ShouldBeTrue("whitespace is collapsed");
        Eval("""{ "all": [ { "text": "netto Summe" } ] }""", ErpInvoice()).Holds.ShouldBeFalse("consecutive words in reading order");
    }

    [Fact]
    public void Regions_pages_and_boxes_restrict_where_an_identifier_holds()
    {
        Eval("""{ "all": [ { "text": "Rechnung", "region": "header" } ] }""", ErpInvoice()).Holds.ShouldBeFalse("y 200 is in the title band, not the header band");
        Eval("""{ "all": [ { "text": "Rechnung", "page": 2 } ] }""", ErpInvoice()).Holds.ShouldBeFalse();
        Eval("""{ "all": [ { "text": "Seite", "page": "last", "region": "footer" } ] }""", ErpInvoice()).Holds.ShouldBeTrue();
        Eval("""{ "all": [ { "text": "Rechnung", "bBox": [60, 200, 120, 210] } ] }""", ErpInvoice()).Holds.ShouldBeTrue();
        Eval("""{ "all": [ { "text": "Rechnung", "bBox": [500, 500, 600, 600], "tolerance": 10 } ] }""", ErpInvoice()).Holds.ShouldBeFalse();
    }

    [Fact]
    public void A_location_on_a_group_is_inherited_key_by_key()
    {
        // "any" of two titles in the page-1 title area; a member's own page overrides the page only.
        const string rule = """{ "any": [ { "text": "Invoice" }, { "text": "Rechnung" } ], "page": 1, "region": "title" }""";
        Eval(rule, ErpInvoice()).Holds.ShouldBeTrue();
        Eval("""{ "all": [ { "text": "Summe", "page": 2 } ], "page": 1, "region": "title" }""", ErpInvoice()).Holds.ShouldBeTrue("page 2 from the member, the title area from the group (y 100 is inside it)");
        Eval("""{ "all": [ { "text": "Seite", "page": 2 } ], "page": 1, "region": "title" }""", ErpInvoice()).Holds.ShouldBeFalse("the footer line is not in the inherited title area");
    }

    [Fact]
    public void All_any_and_none_at_one_level_are_an_implicit_AND()
    {
        const string rule = """
            { "all": [ { "text": "Nordwind" } ], "any": [ { "text": "Invoice" }, { "text": "Lieferschein" } ], "none": [ { "text": "Gutschrift" } ] }
            """;
        var result = Eval(rule, ErpInvoice());
        result.Holds.ShouldBeFalse("no member of any holds");
        result.Failed.ShouldContain(f => f.StartsWith("any[0]", StringComparison.Ordinal));

        var excluded = Eval("""{ "all": [ { "text": "Nordwind" } ], "none": [ { "text": "Rechnung" } ] }""", ErpInvoice());
        excluded.Holds.ShouldBeFalse();
        excluded.Failed.ShouldContain(f => f.StartsWith("none[0]", StringComparison.Ordinal) && f.EndsWith("is present", StringComparison.Ordinal));
        excluded.Evidence.ShouldContain(e => e.Path == "none[0]" && !e.Positive);
    }

    [Fact]
    public void Nested_groups_evaluate_and_count_their_positive_identifiers()
    {
        var rule = IdentificationRule.Parse("""
            { "all": [ { "text": "Nordwind" }, { "any": [ { "text": "Rechnung" }, { "text": "Invoice" } ] } ], "none": [ { "text": "Gutschrift" } ] }
            """);
        rule.PositiveCount.ShouldBe(3);
        rule.Identifiers.Select(i => i.Path).ShouldBe(["all[0]", "all[1].any[0]", "all[1].any[1]", "none[0]"]);
        IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(ErpInvoice(), Nordwind)).Holds.ShouldBeTrue();
    }

    [Fact]
    public void A_regex_matches_the_joined_text_of_its_region_and_names_the_words_it_covers()
    {
        var result = Eval("""{ "all": [ { "regex": "Summe netto\\s+\\d", "page": 2 } ] }""", ErpInvoice());
        result.Holds.ShouldBeTrue();
        result.Evidence[0].Text.ShouldBe("Summe netto 1.250,00");
        Eval("""{ "all": [ { "regex": "Kunden-Nr" } ] }""", ErpInvoice()).Holds.ShouldBeFalse();
    }

    [Fact]
    public void A_supplier_value_needs_the_value_in_the_master_data_and_on_the_page()
    {
        Eval("""{ "all": [ { "supplierValue": "vatId" } ] }""", ErpInvoice(), SupplierValues.None).Holds.ShouldBeFalse("no master data, nothing to find");
        Eval("""{ "all": [ { "supplierValue": "vatId" } ] }""", ErpInvoice(), new SupplierValues(VatId: "DE999999999")).Holds.ShouldBeFalse();
        Eval("""{ "all": [ { "supplierValue": "iban" } ] }""", ErpInvoice()).Holds.ShouldBeFalse();
        Eval("""{ "all": [ { "supplierValue": "vatId", "page": 2 } ] }""", ErpInvoice()).Holds.ShouldBeFalse();
    }

    [Fact]
    public void A_supplier_value_holds_only_as_a_whole_not_inside_a_longer_value()
    {
        var supplier = new SupplierValues(VatId: NordwindVat, Attributes: new Dictionary<string, string>(StringComparer.Ordinal) { ["customerNo"] = "12345" });
        static DocumentWords Line(string line) => Words(new PageBuilder(1).Text(60, 60, line));
        const string Vat = """{ "all": [ { "supplierValue": "vatId" } ] }""";
        const string Customer = """{ "all": [ { "supplierValue": "attributes.customerNo" } ] }""";

        Eval(Vat, Line("USt-IdNr.: DE8111111119"), supplier).Holds.ShouldBeFalse("a longer, different VAT id");
        Eval(Vat, Line("USt-IdNr.: XDE811111111"), supplier).Holds.ShouldBeFalse("a letter in front of it");
        Eval(Customer, Line("Tel 0401234567890"), supplier).Holds.ShouldBeFalse("a short number inside a phone number");
        Eval(Customer, Line("Konto 9912345 BLZ"), supplier).Holds.ShouldBeFalse("a short number inside a longer one");

        Eval(Vat, Line("USt-IdNr.:DE811111111"), supplier).Holds.ShouldBeTrue("a label glued on with a colon");
        Eval(Vat, Line("USt-IdNr. DE 811 111 111 Steuernr. 12/345"), supplier).Holds.ShouldBeTrue();
        var customer = Eval(Customer, Line("Kunden-Nr. 12345 vom 03.11.2026"), supplier);
        customer.Holds.ShouldBeTrue();
        customer.Evidence[0].Text.ShouldBe("12345");
        Eval(Customer, Line("KdNr12345"), supplier).Holds.ShouldBeTrue("letters turning into digits are an edge");
    }

    [Fact]
    public void A_number_printed_in_groups_goes_on_across_its_gaps_and_a_removed_separator_after_a_label_is_an_edge()
    {
        var supplier = new SupplierValues(VatId: NordwindVat, Ibans: ["DE02120300000000202051"], Attributes: new Dictionary<string, string>(StringComparer.Ordinal) { ["customerNo"] = "12345" });
        static DocumentWords Line(string line) => Words(new PageBuilder(1).Text(60, 60, line));
        const string Vat = """{ "all": [ { "supplierValue": "vatId" } ] }""";
        const string Customer = """{ "all": [ { "supplierValue": "attributes.customerNo" } ] }""";
        const string Iban = """{ "all": [ { "supplierValue": "iban" } ] }""";

        Eval(Customer, Line("Tel 040 12345 67890"), supplier).Holds.ShouldBeFalse("a group of a spaced phone number");
        Eval(Customer, Line("Konto 99 12345 BLZ"), supplier).Holds.ShouldBeFalse("the end of a spaced longer number");
        Eval(Customer, Line("Tel 040-12345-67"), supplier).Holds.ShouldBeFalse("a group of a number with dashes");
        Eval(Customer, Line("Betrag 1.12345"), supplier).Holds.ShouldBeFalse("digits across a removed dot");
        Eval(Vat, Line("USt-IdNr. DE 811 111 111 9"), supplier).Holds.ShouldBeFalse("a longer, different VAT id printed in groups");
        Eval(Vat, Line("USt-IdNr. DE 811 111 111 98"), supplier).Holds.ShouldBeFalse();

        Eval(Vat, Line("USt-IdNr.DE811111111"), supplier).Holds.ShouldBeTrue("a label glued on with a dot");
        Eval(Vat, Line("USt-IdNr.-DE811111111"), supplier).Holds.ShouldBeTrue("a label glued on with a dash");
        Eval(Vat, Line("UStId/DE 811 111 111"), supplier).Holds.ShouldBeTrue("a label glued on with a slash");
        Eval(Vat, Line("USt-IdNr. DE 811 111 111 / Steuernr."), supplier).Holds.ShouldBeTrue();
        Eval(Customer, Line("Kunden-Nr. 12345 / Auftrag A-77"), supplier).Holds.ShouldBeTrue();
        Eval(Customer, Line("Kunden-Nr.12345"), supplier).Holds.ShouldBeTrue("letters, a dot, then digits");
        var iban = Eval(Iban, Line("IBAN DE02 1203 0000 0000 2020 51 BIC BYLADEM1001"), supplier);
        iban.Holds.ShouldBeTrue("an IBAN in groups, between its label and the next one");
        iban.Evidence[0].Text.ShouldBe("DE02 1203 0000 0000 2020 51");
        Eval(Iban, Line("IBAN DE02 1203 0000 0000 2020 51 7"), supplier).Holds.ShouldBeFalse("a longer account number");
    }

    [Fact]
    public void A_supplier_value_in_a_values_row_or_its_own_column_holds_when_a_date_or_a_document_number_follows()
    {
        var supplier = new SupplierValues(VatId: NordwindVat, Attributes: new Dictionary<string, string>(StringComparer.Ordinal) { ["customerNo"] = "12345" });
        const string Customer = """{ "all": [ { "supplierValue": "attributes.customerNo" } ] }""";

        // A German header: the labels in one row, the values under them, in columns.
        var valuesRow = Words(new PageBuilder(1)
            .Line(60, (60, "Kundennr."), (300, "Datum"), (500, "Rechnungsnr."))
            .Line(75, (60, "12345"), (300, "04.03.2027"), (500, "RE-1001")));
        var holds = Eval(Customer, valuesRow, supplier);
        holds.Holds.ShouldBeTrue("a column gap ends the value");
        holds.Evidence[0].Text.ShouldBe("12345");

        Eval(Customer, Words(new PageBuilder(1).Text(60, 60, "12345 04.03.2027 RE-1001")), supplier).Holds.ShouldBeTrue("a date after a narrow space is a value of its own");
        Eval(Customer, Words(new PageBuilder(1).Text(60, 60, "RE-1001 12345")), supplier).Holds.ShouldBeTrue("a document number before it");
        Eval(Customer, Words(new PageBuilder(1).Line(60, (60, "12345"), (300, "67890"))), supplier).Holds.ShouldBeTrue("two numbers in two columns");
        Eval(Customer, Words(new PageBuilder(1).Line(60, (60, "67890"), (300, "12345"))), supplier).Holds.ShouldBeTrue();

        Eval(Customer, Words(new PageBuilder(1).Text(60, 60, "12345 67890")), supplier).Holds.ShouldBeFalse("a plain digit group after a narrow space goes on");
        Eval(Customer, Words(new PageBuilder(1).Text(60, 60, "Tel 040 12345 67890")), supplier).Holds.ShouldBeFalse();
    }

    [Fact]
    public void The_rule_in_words_names_supplier_values_by_their_labels()
    {
        IdentificationRule.Parse("""{ "all": [ { "text": "Rechnung", "page": 1, "region": "title" }, { "supplierValue": "vatId", "page": "last" }, { "supplierValue": "attributes.customerNo" }, { "supplierValue": "iban" } ] }""")
            .Describe()
            .ShouldBe("all of: text \"Rechnung\" (page 1, title area); the supplier's VAT id (last page); the supplier's attribute customerNo (anywhere); the supplier's IBAN (anywhere)");
        Eval("""{ "all": [ { "supplierValue": "vatId", "page": 1 } ] }""", ErpInvoice()).Evidence[0].Description.ShouldBe("the supplier's VAT id (page 1)");
    }

    [Fact]
    public void Source_identifiers_read_the_ingestion_facts()
    {
        var source = new SourceFacts(ItemKind: "pdf", FileName: "PL_2027.pdf", FolderPath: "lieferanten/nordwind/");
        Eval("""{ "all": [ { "source": "fileName", "regex": "^PL_\\d{4}" } ] }""", ErpInvoice(), source: source).Holds.ShouldBeTrue();
        Eval("""{ "all": [ { "source": "itemKind", "equals": "pdf" } ] }""", ErpInvoice(), source: source).Holds.ShouldBeTrue();
        Eval("""{ "all": [ { "source": "subject", "regex": "." } ] }""", ErpInvoice(), source: source).Holds.ShouldBeFalse("no subject");
    }

    [Fact]
    public void One_OCR_error_is_tolerated_only_on_an_OCR_layer_and_only_on_words_of_six_letters_or_more()
    {
        const string rule = """{ "all": [ { "text": "Rechnung", "allowOcrErrors": true }, { "text": "Nordwind" } ] }""";
        Eval(rule, ErpInvoice(ocr: true, title: "Rechnunq")).Holds.ShouldBeTrue("one substitution");
        Eval(rule, ErpInvoice(ocr: true, title: "Rechnug")).Holds.ShouldBeTrue("one deletion");
        Eval(rule, ErpInvoice(ocr: true, title: "Rechnuqq")).Holds.ShouldBeFalse("two edits");
        Eval(rule, ErpInvoice(ocr: false, title: "Rechnunq")).Holds.ShouldBeFalse("never on a born-digital text layer");
        Eval("""{ "all": [ { "text": "Pos", "allowOcrErrors": true }, { "text": "Nordwind" } ] }""", new DocumentWords([new PageBuilder(1).Text(10, 10, "Nordwind Pas").Words()], true))
            .Holds.ShouldBeFalse("a short word is compared exactly");
        IdentificationRuleEvaluator.WithinOneEdit("Rechnung", "Rechnung").ShouldBeTrue();
        IdentificationRuleEvaluator.WithinOneEdit("Rechnung", "Rechnungen").ShouldBeFalse();
    }

    [Fact]
    public void The_hash_is_eight_hex_characters_and_changes_with_the_rule()
    {
        var a = IdentificationRule.Parse("""{ "all": [ { "text": "Rechnung" } ] }""");
        var b = IdentificationRule.Parse("""{ "all": [ { "text": "Gutschrift" } ] }""");
        a.Hash.Length.ShouldBe(8);
        a.Hash.ShouldBe(IdentificationRule.Parse("""{ "all": [ { "text": "Rechnung" } ] }""").Hash);
        a.Hash.ShouldNotBe(b.Hash);
    }

    [Theory]
    [InlineData("""{ "page": 1 }""", "needs all, any or none")]
    [InlineData("""{ "all": [] }""", "non-empty array")]
    [InlineData("""{ "all": [ { "text": "a", "regex": "b" } ] }""", "exactly one")]
    [InlineData("""{ "all": [ { "text": "a", "colour": "red" } ] }""", "not a key")]
    [InlineData("""{ "all": [ { "text": "a", "region": "middle" } ] }""", "region")]
    [InlineData("""{ "all": [ { "text": "a", "region": "title", "bBox": [0, 0, 10, 10] } ] }""", "not both")]
    [InlineData("""{ "all": [ { "all": [ { "all": [ { "all": [ { "text": "a" } ] } ] } ] } ] }""", "nest at most")]
    [InlineData("""{ "all": [ { "source": "fileName" } ] }""", "exactly one of regex and equals")]
    public void A_rule_that_does_not_read_is_refused_with_its_place(string json, string message)
    {
        var ex = Should.Throw<IdentificationRuleException>(() => IdentificationRule.Parse(json));
        ex.Message.ShouldContain(message);
    }

    [Fact]
    public void The_checks_at_save_refuse_what_the_format_allows_but_the_rules_do_not()
    {
        Problems("""{ "none": [ { "text": "Gutschrift" } ] }""").ShouldContain(p => p.Message.Contains("positive identifier", StringComparison.Ordinal));
        Problems("""{ "all": [ { "text": "Rechnung", "allowOcrErrors": true } ] }""").ShouldContain(p => p.Path == "all[0]" && p.Message.Contains("only positive", StringComparison.Ordinal));
        Problems("""{ "all": [ { "regex": "(a)\\1" } ] }""").ShouldContain(p => p.Message.Contains("does not compile", StringComparison.Ordinal));
        Problems("""{ "all": [ { "regex": "(?:[^x]{0,99}\\d){0,99}x" } ] }""").ShouldContain(p => p.Message.Contains("unrolls", StringComparison.Ordinal));
        Problems("""{ "all": [ { "supplierValue": "vatId" }, { "text": "Rechnung" } ] }""", IdentificationScope.Tenant).ShouldContain(p => p.Path == "all[0]");
        Problems("""{ "all": [ { "cell": "A1", "equals": "Preisliste" } ] }""").ShouldContain(p => p.Message.Contains("P6", StringComparison.Ordinal));
        Problems("""{ "all": [ { "supplierValue": "name" } ] }""").ShouldNotBeEmpty("the meta-schema names the keys");
        var many = string.Join(", ", Enumerable.Range(0, 21).Select(i => $$"""{ "text": "w{{i}}" }"""));
        Problems($$"""{ "any": [ {{many}} ] }""").ShouldNotBeEmpty();
        Problems("""{ "all": [ { "text": "Rechnung", "page": 1, "region": "title" }, { "supplierValue": "vatId" } ], "none": [ { "text": "Gutschrift" } ] }""").ShouldBeEmpty();
    }

    [Fact]
    public void The_meta_schema_snapshot_is_committed()
    {
        var path = Path.Combine(RepositoryRoot(), "schemas", $"{IdentificationRuleSchema.Name}.v{IdentificationRuleSchema.Version}.json");
        if (Environment.GetEnvironmentVariable("UPDATE_SCHEMAS") == "1")
        {
            File.WriteAllText(path, IdentificationRuleSchema.SnapshotText);
        }

        File.Exists(path).ShouldBeTrue($"{path} is missing; rerun with UPDATE_SCHEMAS=1.");
        File.ReadAllText(path).ReplaceLineEndings().ShouldBe(IdentificationRuleSchema.SnapshotText.ReplaceLineEndings(),
            "The identification rule meta-schema changed; if intended, bump IdentificationRuleSchema.Version for a breaking change or rerun with UPDATE_SCHEMAS=1.");
    }

    [Fact]
    public void The_meta_schema_accepts_the_examples_of_section_8_5()
    {
        foreach (var example in new[]
        {
            """{ "all": [ { "text": "Rechnung", "page": 1, "region": "title" }, { "supplierValue": "vatId", "page": 1 } ], "none": [ { "text": "Rechnungskorrektur", "page": 1 }, { "text": "Gutschrift", "page": 1 } ] }""",
            """{ "any": [ { "text": "Invoice" }, { "text": "Rechnung" } ], "page": 1, "region": "title" }""",
            """{ "all": [ { "text": "Preisliste", "page": 1, "region": "title" }, { "regex": "gültig ab\\s+\\d{2}\\.\\d{2}\\.\\d{4}", "page": 1 } ] }""",
            """{ "all": [ { "source": "fileName", "regex": "^PL_\\d{4}" } ] }""",
        })
        {
            using var document = JsonDocument.Parse(example);
            IdentificationRuleSchema.Validate(document.RootElement).ShouldBeEmpty(example);
        }
    }

    private static IReadOnlyList<IdentificationProblem> Problems(string json, IdentificationScope scope = IdentificationScope.Layout)
    {
        IdentificationRuleChecks.Check(json, scope, out var problems);
        return problems;
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocProc.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (DocProc.slnx) was not found.");
    }

    internal static DocumentWords Words(params PageBuilder[] pages) => new([.. pages.Select(p => p.Words())], false);

    internal static DocumentWords Words(TextLayer layer) => DocumentWords.From(layer);

    internal static WordPage Page(PageBuilder page) => page.Words();
}
