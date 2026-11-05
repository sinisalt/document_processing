using DocProc.Definitions;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Classification;
using Microsoft.Extensions.Options;

namespace DocProc.Integration.Tests.Classification;

/// <summary>
/// The tenant keyword set (docs/11 §9.4): a table built from the tenant's types' classification blocks scores a type
/// of the tenant's own by its title, labels and negatives; a standard type with its template's block keeps the
/// built-in rules exactly; a type the tenant lacks contributes nothing.
/// </summary>
public sealed class TenantKeywordRulesTests
{
    private readonly KeywordDocumentClassifier _classifier = new(Options.Create(new KeywordClassifierOptions()));

    private static readonly DocumentTypeKey ReturnSlip = DocumentTypeKey.Parse("return-slip");

    private static KeywordTypeSource Custom(IReadOnlyList<string> titles, IReadOnlyList<string>? negatives = null) => new(
        ReturnSlip,
        new ClassificationDefinition
        {
            Keywords = new Dictionary<string, IReadOnlyList<string>> { ["de"] = titles },
            Labels = new Dictionary<string, IReadOnlyList<string>> { ["de"] = ["Retouren-Nr."] },
            Negatives = negatives is null ? null : new Dictionary<string, IReadOnlyList<string>> { ["de"] = negatives },
        },
        UsesStandardRules: false);

    private static DocProc.Domain.Text.TextLayer Layer(params string[] lines) =>
        TextLayers.Layer(TextLayers.Page(1, [.. lines.Select((line, i) => ((double)(60 + (i * 20)), line))]));

    [Fact]
    public void A_tenant_type_is_classified_by_its_own_title_and_labels()
    {
        var table = KeywordRuleTable.FromTypes([Custom(["Retourenschein"]), new KeywordTypeSource(DocumentTypeKey.Invoice, null, UsesStandardRules: true)]);

        var outcome = _classifier.Classify(Layer("Musterfirma GmbH", "Retourenschein", "Retouren-Nr. RMA-4711"), null, table);

        outcome.Type.ShouldBe(ReturnSlip);
        outcome.Confidence.ShouldBeGreaterThanOrEqualTo(0.8);
        _classifier.Classify(Layer("Musterfirma GmbH", "Rechnung", "Rechnungsnummer RE-1"), null, table).Type.ShouldBe(DocumentTypeKey.Invoice, "the standard invoice rules come along");
        _classifier.Classify(Layer("Musterfirma GmbH", "Retourenschein", "Retouren-Nr. RMA-4711")).Type.ShouldBe(DocumentTypeKey.Unknown, "the built-in table has no return slip");
    }

    [Fact]
    public void A_standard_type_keeps_exactly_the_built_in_rules()
    {
        var all = KeywordRuleTable.FromTypes(DocumentTypeKey.Legacy.Select(k => new KeywordTypeSource(k, null, UsesStandardRules: true)));
        all.Count.ShouldBe(KeywordRuleTable.Default.Count);

        var onlyInvoices = KeywordRuleTable.FromTypes([new KeywordTypeSource(DocumentTypeKey.Invoice, null, UsesStandardRules: true)]);
        onlyInvoices.Types.ShouldBe([DocumentTypeKey.Invoice]);
        _classifier.Classify(Layer("Musterfirma GmbH", "Lieferschein", "Lieferschein-Nr. LS-1"), null, onlyInvoices).Type.ShouldBe(DocumentTypeKey.Unknown, "a type the tenant lacks contributes nothing");
    }

    [Fact]
    public void Negatives_and_whole_words_apply_to_tenant_phrases()
    {
        var table = KeywordRuleTable.FromTypes([Custom(["Retourenschein"], negatives: ["kein Retourenschein"])]);

        _classifier.Classify(Layer("Musterfirma GmbH", "Retourenscheinvorlage"), null, table).Type.ShouldBe(DocumentTypeKey.Unknown, "whole words only");
        _classifier.Classify(Layer("Musterfirma GmbH", "Dies ist kein Retourenschein"), null, table).Type.ShouldBe(DocumentTypeKey.Unknown);
    }

    [Fact]
    public void Tenant_phrases_are_literals_and_bounded()
    {
        KeywordRuleTable.Matches("Retoure (A+B)*", "Retoure (A+B)* Liste").ShouldBeTrue("regex metacharacters are escaped");
        KeywordRuleTable.Matches("Retoure (A+B)*", "Retoure AAAB Liste").ShouldBeFalse();

        // Letters only, so no phrase is a whole word inside another (Titel6 would hold in Titel60).
        var many = Enumerable.Range(0, 80).Select(i => $"Titel{(char)('a' + (i / 26))}{(char)('a' + (i % 26))}").Append(new string('x', 200)).ToList();
        var table = KeywordRuleTable.FromTypes([Custom(many)]);
        // keywords and labels: one rule each, the keywords capped at 50 phrases and the over-long phrase dropped.
        table.Count.ShouldBe(2);
        _classifier.Classify(Layer("Musterfirma GmbH", "Titelaa"), null, table).Type.ShouldBe(ReturnSlip);
        _classifier.Classify(Layer("Musterfirma GmbH", "Titelci"), null, table).Type.ShouldBe(DocumentTypeKey.Unknown, "phrases past the cap are not used");
    }

    [Fact]
    public void A_phrase_of_separators_compiles_to_a_linear_pattern_that_cannot_backtrack()
    {
        // "a - - - … z": before, each space became \s+ and each hyphen [-\s]?, side by side, which backtracked
        // exponentially over a run of white space on the page and spent the whole timeout on every page.
        var phrase = "a" + string.Concat(Enumerable.Repeat(" -", 24)) + " z";
        KeywordRuleSet.PhrasePattern(phrase).ShouldBe(@"a[-\s]*z");
        var regex = KeywordRuleSet.Compile([phrase], compiled: false, KeywordRuleTable.TenantTimeout);
        var page = string.Concat(Enumerable.Repeat("a" + new string(' ', 60) + "b ", 40));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        regex.IsMatch(page).ShouldBeFalse();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1), "linear in the page (the old pattern spent its whole 2 s timeout here)");

        // The separators still mean what they meant: spaces any white space, a hyphen optional or a space.
        KeywordRuleTable.Matches("Retouren-Nr", "Retouren-Nr. 4711").ShouldBeTrue();
        KeywordRuleTable.Matches("Retouren-Nr", "Retouren Nr. 4711").ShouldBeTrue();
        KeywordRuleTable.Matches("Retouren-Nr", "RetourenNr. 4711").ShouldBeTrue();
        KeywordRuleTable.Matches("Retouren schein", "Retouren \n  schein").ShouldBeTrue();
        KeywordRuleTable.Matches("Retouren schein", "Retourenschein").ShouldBeFalse("a space is not optional");
        KeywordRuleSet.PhrasePattern(" - Retoure - ").ShouldBe("Retoure", "separators at the ends are dropped");
    }

    [Fact]
    public void The_built_in_phrases_compile_as_before()
    {
        var invoiceLabel = KeywordRuleSet.Rules.Single(r => r.Type == DocumentTypeKey.Invoice && r.Kind == KeywordKind.Label && r.Language == DocumentLanguages.German);
        foreach (var text in new[] { "Rechnungs-Nr 42", "Rechnungs Nr 42", "RechnungsNr 42", "Rechnungsnummer 42" })
        {
            invoiceLabel.Pattern.IsMatch(text).ShouldBeTrue(text);
        }

        invoiceLabel.Pattern.IsMatch("Rechnungsnummernkreis").ShouldBeFalse("whole words only");
    }

    [Fact]
    public void A_tenant_table_stops_at_its_budget_per_page()
    {
        // Wall-clock backstops: short enough that a large table cannot hold the shared worker for long, long enough
        // that a busy worker or a garbage-collection pause does not make a linear match time out.
        KeywordRuleTable.PageBudget.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(2));
        KeywordRuleTable.TenantTimeout.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        KeywordRuleTable.TenantTimeout.ShouldBeLessThanOrEqualTo(KeywordRuleTable.PageBudget);
    }

    [Fact]
    public void A_page_whose_tenant_rules_did_not_all_run_decides_no_type()
    {
        // The return slip's title rule times out (a pattern that backtracks, with a 1 ms timeout, stands in for a
        // stalled worker); the rule of the other type still matches. Before, that other type won on partial evidence.
        var stalled = new KeywordRule(ReturnSlip, "de", KeywordKind.Title,
            new System.Text.RegularExpressions.Regex(@"(a+)+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(1)),
            KeywordRuleSet.TitleWeight, ["Retourenschein"]);
        var other = KeywordRuleTable.FromTypes([new KeywordTypeSource(DocumentTypeKey.Invoice, null, UsesStandardRules: true)]);
        var table = new KeywordRuleTable([stalled, .. other.Rules]);
        var text = Layer("Musterfirma GmbH", "Rechnung", "Rechnungsnummer RE-1", new string('a', 5000) + "!");

        var page = IndexedPageText.From(text.Pages[0]);
        KeywordMatcher.FindHits(page, table, out var complete);
        complete.ShouldBeFalse("the stalled rule timed out");
        KeywordMatcher.FindHits(page, other, out var completeWithoutIt);
        completeWithoutIt.ShouldBeTrue();

        var outcome = _classifier.Classify(text, null, table);
        outcome.Type.ShouldBe(DocumentTypeKey.Unknown, "the missing rule may be the winner's");
        outcome.Confidence.ShouldBe(0);
        _classifier.Classify(text, null, other).Type.ShouldBe(DocumentTypeKey.Invoice, "a complete evaluation still decides");
    }
}
