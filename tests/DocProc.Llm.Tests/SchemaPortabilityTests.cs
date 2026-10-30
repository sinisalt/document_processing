using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Llm.Contracts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests;

public class SchemaPortabilityTests
{
    public static TheoryData<string> CanonicalSchemaNames =>
        new(CanonicalSchemas.All().Select(s => s.Name));

    [Theory]
    [MemberData(nameof(CanonicalSchemaNames))]
    public void Every_canonical_schema_is_portable_across_providers(string name)
    {
        var schema = CanonicalSchemas.All().Single(s => s.Name == name);

        var violations = SchemaPortability.Check(schema);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Invoice_schema_marks_every_property_required_and_optional_ones_nullable()
    {
        var root = CanonicalSchemas.InvoiceExtraction.Schema;

        root["type"]!.GetValue<string>().ShouldBe("object");
        root["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        var properties = root["properties"]!.AsObject();
        var required = root["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        required.ShouldBe(properties.Select(p => p.Key).ToHashSet());

        // camelCase names, nullable scalars expressed as a type union, dates with a portable format.
        properties.ContainsKey("invoiceNumber").ShouldBeTrue();
        properties["invoiceNumber"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["string", "null"], ignoreOrder: true);
        properties["subtotalNet"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["number", "null"], ignoreOrder: true);
        properties["issueDate"]!["format"]!.GetValue<string>().ShouldBe("date");

        // Nested objects and arrays follow the same rules.
        var line = properties["lines"]!["items"]!.AsObject();
        line["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        line["required"]!.AsArray().Count.ShouldBe(line["properties"]!.AsObject().Count);

        // Enums serialise as strings with an explicit (nullable) type.
        properties["kind"]!["enum"]!.AsArray().Select(n => n?.GetValue<string>()).ShouldContain("CreditNote");
        properties["kind"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["string", "null"], ignoreOrder: true);
        var splitType = CanonicalSchemas.Classification.Schema["properties"]!["documentType"]!;
        splitType["type"]!.GetValue<string>().ShouldBe("string");
    }

    [Fact]
    public void Checker_reports_each_vendor_incompatibility()
    {
        var bad = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "amount": { "type": "number", "minimum": 0 },
                "code": { "type": "string", "pattern": "^[A-Z]{3}$", "format": "regex" },
                "child": { "$ref": "#" },
                "optional": { "type": "string" }
              },
              "required": ["amount", "code", "child"]
            }
            """)!.AsObject();

        var violations = SchemaPortability.Check(bad);

        violations.ShouldContain(v => v.Contains("additionalProperties"));
        violations.ShouldContain(v => v.Contains("'minimum'"));
        violations.ShouldContain(v => v.Contains("'pattern'"));
        violations.ShouldContain(v => v.Contains("format 'regex'"));
        violations.ShouldContain(v => v.Contains("'$ref'"));
        violations.ShouldContain(v => v.Contains("properties/optional") && v.Contains("required"));
        Should.Throw<InvalidOperationException>(() => SchemaPortability.EnsurePortable(new JsonSchemaDefinition("bad", "1", bad)));
    }

    [Fact]
    public void A_model_answer_shaped_by_the_schema_deserialises_into_the_contract()
    {
        const string answer = """
            {
              "kind": "Invoice", "invoiceNumber": "RE-2026-0042", "issueDate": "2026-10-27", "deliveryDate": null,
              "servicePeriodFrom": null, "servicePeriodTo": null, "dueDate": "2026-11-10", "currency": "EUR",
              "supplier": { "name": "METRO Deutschland GmbH", "vatId": "DE811177936", "taxId": null, "registrationNumber": null,
                            "addressLine1": null, "addressLine2": null, "postalCode": null, "city": null, "countryCode": "DE",
                            "email": null, "phone": null, "iban": null, "bic": null },
              "customer": null, "orderReferences": ["PO-77"], "deliveryNoteReferences": [], "originalInvoiceReference": null,
              "subtotalNet": 150.00,
              "taxBreakdown": [ { "ratePercent": 7, "taxableAmount": 100, "taxAmount": 7, "categoryCode": "S" } ],
              "totalTax": 16.50, "totalGross": 166.50, "amountDue": null, "rounding": null,
              "chargesAndAllowances": [], "paymentTerms": null, "notes": null,
              "lines": [ { "lineNumber": 1, "supplierSku": "123", "gtin": null, "description": "Tomaten", "quantity": 10, "unit": "kg",
                           "packSize": null, "unitPrice": 10, "discountPercent": null, "discountAmount": null, "lineNet": 100,
                           "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 } ],
              "unreadableRegions": [ { "page": 2, "area": "stamp over totals", "reason": "ink smudge" } ]
            }
            """;

        var parsed = JsonSerializer.Deserialize<InvoiceExtraction>(answer, PortableSchemaGenerator.SerializerOptions)!;

        parsed.Kind.ShouldBe(ExtractedInvoiceKind.Invoice);
        parsed.IssueDate.ShouldBe(new DateOnly(2026, 10, 27));
        parsed.Supplier!.VatId.ShouldBe("DE811177936");
        parsed.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(10m);
        parsed.UnreadableRegions.ShouldHaveSingleItem().Page.ShouldBe(2);
        parsed.Customer.ShouldBeNull();
    }
}
