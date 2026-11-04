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
}
