using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Llm.Contracts;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests.Prompts;

public class PromptCatalogueTests
{
    private static readonly PromptCatalogue Catalogue = PromptCatalogue.Load();

    [Fact]
    public void Every_bundle_has_an_id_an_explicit_version_and_a_system_prompt()
    {
        foreach (var bundle in new[] { Catalogue.ExtractInvoice, Catalogue.ClassifyDocument, Catalogue.SplitDocument })
        {
            bundle.Id.ShouldNotBeNullOrWhiteSpace();
            bundle.Version.ShouldNotBeNullOrWhiteSpace();
            bundle.System.Length.ShouldBeGreaterThan(200);
            bundle.Task.ShouldBe(string.Empty, "the catalogue holds the stable prefix only; callers set the task");
            bundle.CacheKey.ShouldBe($"{bundle.Id}@{bundle.Version}");
        }

        Catalogue.ExtractInvoice.Id.ShouldBe(PromptIds.ExtractInvoice);
        Catalogue.ClassifyDocument.Id.ShouldBe(PromptIds.ClassifyDocument);
        Catalogue.SplitDocument.Id.ShouldBe(PromptIds.SplitDocument);
    }

    [Fact]
    public void Extraction_system_prompt_states_the_rules_of_docs_06_and_describes_the_schema()
    {
        var system = Catalogue.ExtractInvoice.System;

        system.ShouldContain("verbatim");
        system.ShouldContain("`null`");
        system.ShouldContain("Deposits");
        system.ShouldContain("discount");
        system.ShouldContain("next page");
        system.ShouldContain("language");
        system.ShouldContain("unreadableRegions");
        system.ShouldContain("Never invent");

        // Every top-level schema property is described by name.
        foreach (var property in CanonicalSchemas.InvoiceExtraction.Schema["properties"]!.AsObject())
        {
            system.ShouldContain($"`{property.Key}`", customMessage: $"schema property {property.Key} is not described");
        }
    }

    [Fact]
    public void Extraction_prompt_carries_two_worked_examples_with_text_inputs()
    {
        Catalogue.ExtractInvoice.Examples.Count.ShouldBe(2);
        Catalogue.ExtractInvoice.Examples[0].Input.ShouldContain("Rechnung Nr. 4711-2026-00917");
        Catalogue.ExtractInvoice.Examples[1].Input.ShouldContain("CREDIT NOTE");
    }

    public static TheoryData<int> ExampleIndexes => new(0, 1);

    [Theory]
    [MemberData(nameof(ExampleIndexes))]
    public void Each_example_output_deserialises_into_the_contract_and_covers_every_schema_property(int index)
    {
        var output = Catalogue.ExtractInvoice.Examples[index].Output;

        var parsed = JsonSerializer.Deserialize<InvoiceExtraction>(output, PortableSchemaGenerator.SerializerOptions);

        parsed.ShouldNotBeNull();
        parsed.Lines.Count.ShouldBe(3);
        var violations = new List<string>();
        CheckAgainstSchema(JsonNode.Parse(output)!, CanonicalSchemas.InvoiceExtraction.Schema, "$", violations);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void First_example_keeps_deposit_and_discount_as_the_prompt_rules_say()
    {
        var parsed = JsonSerializer.Deserialize<InvoiceExtraction>(Catalogue.ExtractInvoice.Examples[0].Output, PortableSchemaGenerator.SerializerOptions)!;

        parsed.Kind.ShouldBe(ExtractedInvoiceKind.Invoice);
        parsed.Currency.ShouldBe("EUR");
        parsed.Lines[1].DiscountPercent.ShouldBe(10m);
        parsed.Lines[1].DiscountAmount.ShouldBeNull("the discount amount is not printed, so it is not invented");
        parsed.Lines[2].DepositAmount.ShouldBe(9.90m);
        parsed.Lines[2].LineNet.ShouldBe(9.90m);
        parsed.Lines.Sum(l => l.LineNet).ShouldBe(parsed.SubtotalNet);
        (parsed.SubtotalNet + parsed.TaxBreakdown.Sum(t => t.TaxAmount)).ShouldBe(parsed.TotalGross);
    }

    [Fact]
    public void Second_example_is_a_credit_note_with_an_unreadable_region_and_no_invented_currency()
    {
        var parsed = JsonSerializer.Deserialize<InvoiceExtraction>(Catalogue.ExtractInvoice.Examples[1].Output, PortableSchemaGenerator.SerializerOptions)!;

        parsed.Kind.ShouldBe(ExtractedInvoiceKind.CreditNote);
        parsed.OriginalInvoiceReference.ShouldBe("INV-2026-2231");
        parsed.Currency.ShouldBeNull();
        parsed.TotalGross.ShouldBeNull();
        parsed.TaxBreakdown.ShouldHaveSingleItem().CategoryCode.ShouldBe("AE");
        parsed.Lines[2].Page.ShouldBe(2);
        parsed.UnreadableRegions.ShouldHaveSingleItem().Page.ShouldBe(2);
    }

    [Fact]
    public void Resources_are_read_with_normalised_line_endings()
    {
        PromptCatalogue.ReadResource("extract-invoice.system.md").ShouldNotContain("\r");
        Should.Throw<InvalidOperationException>(() => PromptCatalogue.ReadResource("missing.md"));
    }

    /// <summary>Structural check: every object carries exactly the schema's properties, recursively, with the same nullability.</summary>
    private static void CheckAgainstSchema(JsonNode? instance, JsonObject schema, string path, List<string> violations)
    {
        var types = TypesOf(schema);
        if (instance is null)
        {
            if (!types.Contains("null"))
            {
                violations.Add($"{path}: null is not allowed");
            }

            return;
        }

        if (schema["properties"] is JsonObject properties)
        {
            if (instance is not JsonObject obj)
            {
                violations.Add($"{path}: expected an object");
                return;
            }

            var expected = properties.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var actual = obj.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var missing in expected.Except(actual))
            {
                violations.Add($"{path}: property '{missing}' is missing");
            }

            foreach (var extra in actual.Except(expected))
            {
                violations.Add($"{path}: property '{extra}' is not in the schema");
            }

            foreach (var (name, child) in properties)
            {
                if (actual.Contains(name))
                {
                    CheckAgainstSchema(obj[name], child!.AsObject(), $"{path}.{name}", violations);
                }
            }

            return;
        }

        if (schema["items"] is JsonObject items)
        {
            if (instance is not JsonArray array)
            {
                violations.Add($"{path}: expected an array");
                return;
            }

            for (var i = 0; i < array.Count; i++)
            {
                CheckAgainstSchema(array[i], items, $"{path}[{i}]", violations);
            }

            return;
        }

        if (schema["enum"] is JsonArray allowed && instance is JsonValue value && value.TryGetValue<string>(out var s)
            && !allowed.Any(a => a is JsonValue av && av.TryGetValue<string>(out var name) && name == s))
        {
            violations.Add($"{path}: '{s}' is not one of the enum values");
        }
    }

    private static HashSet<string> TypesOf(JsonObject schema) => schema["type"] switch
    {
        JsonArray array => array.Select(t => t!.GetValue<string>()).ToHashSet(StringComparer.Ordinal),
        JsonValue value => [value.GetValue<string>()],
        _ => [],
    };
}
