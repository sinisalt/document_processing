using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation;

namespace DocProc.EInvoice.Tests;

/// <summary>
/// The extraction contract of docs/11 §9.1 in P0: an outcome carries an <see cref="ExtractedDocument"/> tree, and the
/// compatibility constructor that takes an invoice serialises it into exactly the JSON the canonical column stored
/// before, so the extractors compile unchanged and the stored documents do not change by a byte. On the real
/// e-invoices the rule seam raises the validator's flags too.
/// </summary>
public sealed class ExtractionOutcomeTests
{
    public static TheoryData<string> AllFixtures => [.. Fixtures.All];

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void The_compatibility_constructor_stores_the_invoice_byte_for_byte(string fixture)
    {
        var invoice = Fixtures.Parse(fixture);

        var outcome = new ExtractionOutcome(invoice, ExtractionMethod.StructuredXml, [], DocumentCost.Free);

        outcome.Document.ToJson().ShouldBe(JsonSerializer.Serialize(invoice, CanonicalJson.Options));
        outcome.Document.DefinitionId.ShouldBeNull("no definitions exist before P1");
        outcome.Document.DefinitionVersion.ShouldBeNull();
        outcome.Invoice.ShouldBeSameAs(invoice, "invoice consumers read the instance the path handed over");
        outcome.Method.ShouldBe(ExtractionMethod.StructuredXml);
        outcome.Cost.ShouldBe(DocumentCost.Free);
    }

    [Fact]
    public void Text_numbers_provenance_history_and_custom_properties_survive_the_tree()
    {
        var source = new FieldSource(SourceMethod.Llm, Page: 2, BBox: new BoundingBox(1.5, 2.25, 300, 70), RawText: "Gemüse & Obst <frisch> \"Bio\" 🍅");
        var invoice = new Invoice
        {
            InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create("RE/2026-ÄÖÜ-0042"), source, 0.875),
            Currency = FieldValue.Extracted("EUR", source, 0.99),
            SubtotalNet = FieldValue.Extracted(new Money(150.00m, "EUR"), source, 0.99),
            TotalGross = FieldValue.Extracted(new Money(178.500m, "EUR"), source, 0.9).Correct(new Money(178.50m, "EUR"), "anna", new DateTimeOffset(2026, 10, 30, 14, 0, 0, TimeSpan.FromHours(1))),
            Notes = FieldValue.Extracted("Zeile 1\nZeile 2\tTab  nbsp", source, 0.5),
            Lines = [new InvoiceLine { LineNumber = 1, Quantity = FieldValue.Extracted(0.0010m, source, 0.9), UnitRaw = "Stück" }],
            Extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["x_costCentre"] = JsonSerializer.SerializeToElement(new { value = "K-4711", confidence = 1.0 }),
                ["x_flags"] = JsonSerializer.SerializeToElement(new object?[] { 1, "zwei", null, 3.50m }),
            },
        };

        var outcome = new ExtractionOutcome(invoice, ExtractionMethod.Llm, [], DocumentCost.Free);

        var stored = JsonSerializer.Serialize(invoice, CanonicalJson.Options);
        outcome.Document.ToJson().ShouldBe(stored, "the history's +01:00 offset included, which a node tree would write escaped");
        JsonNode.DeepEquals(outcome.Document.Root, JsonNode.Parse(stored)).ShouldBeTrue("the tree holds the same values");
        outcome.Document.Root["extra"].ShouldBeNull("the extension bag's entries sit beside the declared properties");
        outcome.Document.Root["x_costCentre"]!["value"]!.GetValue<string>().ShouldBe("K-4711");
    }

    [Fact]
    public void A_tree_built_without_an_invoice_reads_back_as_one()
    {
        var invoice = Fixtures.Parse(Fixtures.PeppolInvoice);
        var tree = JsonNode.Parse(JsonSerializer.Serialize(invoice, CanonicalJson.Options))!.AsObject();
        var definition = Guid.NewGuid();

        var outcome = new ExtractionOutcome(new ExtractedDocument(tree, definition, 3), ExtractionMethod.Profile, [], DocumentCost.Free);

        outcome.Document.DefinitionId.ShouldBe(definition);
        outcome.Document.DefinitionVersion.ShouldBe(3);
        JsonSerializer.Serialize(outcome.Invoice, CanonicalJson.Options).ShouldBe(JsonSerializer.Serialize(invoice, CanonicalJson.Options));
        outcome.Invoice.ShouldBeSameAs(outcome.Invoice, "read once");
        var (document, method, flags, cost) = outcome;
        (document, method, flags.Count, cost).ShouldBe((outcome.Document, ExtractionMethod.Profile, 0, DocumentCost.Free));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void The_rule_seam_raises_the_validators_flags_on_real_e_invoices(string fixture)
    {
        var invoice = Fixtures.Parse(fixture);
        var json = JsonSerializer.Serialize(invoice, CanonicalJson.Options);

        var expected = InvoiceValidator.Default.Validate(JsonSerializer.Deserialize<Invoice>(json, CanonicalJson.Options)!, Fixtures.Context).Flags;
        var actual = DocumentValidator.Default.Validate(DocumentView.FromJson(DocumentType.Invoice, json), StandardRuleSets.Invoice, Fixtures.Context).Flags;

        actual.Select(Describe).ShouldBe(expected.Select(Describe));
    }

    private static string Describe(Domain.Flags.DocumentFlag f) =>
        $"{f.Code}|{f.Severity}|{f.Field}|{f.Message}|{string.Join(",", (f.Params ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"))}";
}
