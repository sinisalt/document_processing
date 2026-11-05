using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Validation;

namespace DocProc.Domain.Tests;

/// <summary>
/// The generic rule kinds of P5 (docs/11 §11): <c>requiredIf</c>, <c>compare</c>, <c>sumEquals</c>, <c>arithmetic</c> and
/// <c>expression</c>; definition flags (a rule raising its definition's own <c>X_*</c> code with the declared severity
/// and messages); and the per-rule outcome the review gate reads.
/// </summary>
public sealed class GenericRuleP5Tests
{
    private static readonly ValidationContext Context = ValidationContext.For(new DateOnly(2026, 11, 3), new TenantContext("Gasthaus zur Post GmbH", "DE123456789", "EUR"));

    private static JsonObject Values(Action<JsonObject>? change = null)
    {
        var values = JsonNode.Parse("""
            {
              "returnNumber": "R-1", "returnDate": "2026-11-02", "pickupDate": "2026-11-05", "carrier": null, "reason": "Damaged",
              "subtotalNet": { "amount": 30.5, "currency": "EUR" },
              "items": [
                { "sku": "A", "quantity": 2, "unitPrice": { "amount": 10, "currency": "EUR" }, "lineNet": { "amount": 20, "currency": "EUR" } },
                { "sku": "B", "quantity": 1, "unitPrice": { "amount": 10.5, "currency": "EUR" }, "lineNet": { "amount": 10.5, "currency": "EUR" } }
              ]
            }
            """)!.AsObject();
        change?.Invoke(values);
        return values;
    }

    private static RuleReference Rule(string kind, string json, string? severity = null, string? flag = null, Dictionary<string, string>? message = null) => new(
        "r_" + kind,
        kind,
        new RuleParams(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!),
        severity switch { "blocking" => FlagSeverity.Blocking, "warning" => FlagSeverity.Warning, "info" => FlagSeverity.Info, _ => null },
        message,
        flag);

    private static ValidationResult Validate(JsonObject values, params RuleReference[] rules) =>
        DocumentValidator.Default.Validate(DocumentView.FromJson(DocumentTypeKey.Parse("return-slip"), "{}").WithValues(values), rules, Context);

    private static IReadOnlyList<DocumentFlag> Run(JsonObject values, RuleReference rule, ValidationContext? context = null)
    {
        var result = DocumentValidator.Default.Validate(DocumentView.FromJson(DocumentTypeKey.Parse("return-slip"), "{}").WithValues(values), [rule], context ?? Context);
        result.RulesNotRun.ShouldBeEmpty();
        return result.Flags;
    }

    [Fact]
    public void RequiredIf_requires_the_field_only_when_its_condition_holds()
    {
        var rule = Rule("requiredIf", """{ "path": "carrier", "when": "reason = 'Damaged'" }""");
        var flag = Run(Values(), rule).ShouldHaveSingleItem();
        flag.Code.ShouldBe(FlagCodes.FieldRequired);
        flag.Field.ShouldBe("carrier");
        flag.Params!["ruleId"].ShouldBe("r_requiredIf");

        Run(Values(v => v["reason"] = "Surplus"), rule).ShouldBeEmpty();
        Run(Values(v => v["carrier"] = "DHL"), rule).ShouldBeEmpty();
    }

    [Fact]
    public void RequiredIf_reads_the_supplier_as_a_variable()
    {
        var rule = Rule("requiredIf", """{ "path": "carrier", "when": "$exists($supplier.attributes.erpVendorId)" }""");
        Run(Values(), rule).ShouldBeEmpty("no supplier");
        var withSupplier = Context with { Data = RuleData.None with { Supplier = new JsonObject { ["attributes"] = new JsonObject { ["erpVendorId"] = "V1" } } } };
        Run(Values(), rule, withSupplier).ShouldHaveSingleItem().Code.ShouldBe(FlagCodes.FieldRequired);
    }

    [Theory]
    [InlineData("""{ "left": "pickupDate", "op": "ge", "right": "returnDate" }""", 0)]
    [InlineData("""{ "left": "pickupDate", "op": "lt", "right": "returnDate" }""", 1)]
    [InlineData("""{ "left": "subtotalNet", "op": "gt", "value": 0 }""", 0)]
    [InlineData("""{ "left": "subtotalNet", "op": "eq", "value": 30.49, "tolerance": 0.02 }""", 0)]
    [InlineData("""{ "left": "subtotalNet", "op": "eq", "value": 30 }""", 1)]
    [InlineData("""{ "left": "items[*].quantity", "op": "ge", "value": 2 }""", 1)]
    [InlineData("""{ "left": "reason", "op": "ne", "value": "Damaged" }""", 1)]
    [InlineData("""{ "left": "carrier", "op": "eq", "value": "DHL" }""", 0)]
    public void Compare_orders_numbers_dates_and_strings(string parameters, int flags)
    {
        var raised = Run(Values(), Rule("compare", parameters));
        raised.Count.ShouldBe(flags);
        if (flags > 0)
        {
            raised[0].Code.ShouldBe(FlagCodes.RuleFailed);
            raised[0].Params!["reason"].ShouldBe("compare");
        }
    }

    [Fact]
    public void SumEquals_adds_up_the_rows_within_the_tolerance()
    {
        var rule = Rule("sumEquals", """{ "path": "items[*].lineNet", "equals": "subtotalNet" }""");
        Run(Values(), rule).ShouldBeEmpty();
        var flag = Run(Values(v => v["subtotalNet"]!["amount"] = 31), rule).ShouldHaveSingleItem();
        flag.Field.ShouldBe("subtotalNet");
        flag.Params!["sum"].ShouldBe("30.50");
        flag.Params["expected"].ShouldBe("31.00");
        Run(Values(v => v["subtotalNet"]!["amount"] = 30.52), rule).ShouldBeEmpty("within 0.02");
        Run(Values(v => v["subtotalNet"] = null), rule).ShouldBeEmpty("no total to compare with");
    }

    [Fact]
    public void Arithmetic_checks_every_row()
    {
        var rule = Rule("arithmetic", """{ "rows": "items[*]", "expression": "quantity * unitPrice", "equals": "lineNet" }""");
        Run(Values(), rule).ShouldBeEmpty();
        var flag = Run(Values(v => v["items"]![1]!["lineNet"]!["amount"] = 12), rule).ShouldHaveSingleItem();
        flag.Field.ShouldBe("items[1].lineNet");
        flag.Params!["expected"].ShouldBe("10.50");
        Run(Values(v => v["items"]![1]!["quantity"] = null), rule).ShouldBeEmpty("an absent operand is not checked");
    }

    [Fact]
    public void Arithmetic_reports_an_expression_that_cannot_be_evaluated()
    {
        var rule = Rule("arithmetic", """{ "rows": "items[*]", "expression": "sku * 2", "equals": "lineNet" }""");
        var flags = Run(Values(), rule);
        flags.Count.ShouldBe(2);
        flags.ShouldAllBe(f => f.Code == FlagCodes.RuleFailed && f.Params!["reason"] == "expressionError");
    }

    [Fact]
    public void Expression_tests_the_document_or_every_row()
    {
        Run(Values(), Rule("expression", """{ "predicate": "$count(items) > 0 and returnDate <= $today" }""")).ShouldBeEmpty();
        var rows = Rule("expression", """{ "forEach": "items[*]", "predicate": "quantity >= 2", "field": "quantity" }""");
        var flag = Run(Values(), rows).ShouldHaveSingleItem();
        flag.Field.ShouldBe("items[1].quantity");
        flag.Params!["expression"].ShouldBe("quantity >= 2");
    }

    [Fact]
    public void An_expression_rule_out_of_steps_raises_a_blocking_CHECKS_SKIPPED_whatever_its_severity()
    {
        var rows = new JsonArray([.. Enumerable.Range(0, 5_000).Select(i => (JsonNode)new JsonObject { ["sku"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture), ["quantity"] = 1 })]);
        var values = Values(v => v["items"] = rows);
        var rule = Rule("expression", """{ "forEach": "items[*]", "predicate": "$count($$.items[quantity > 0]) > 0" }""", severity: "info");
        var flag = Run(values, rule).ShouldHaveSingleItem();
        flag.Code.ShouldBe(FlagCodes.ChecksSkipped);
        flag.Severity.ShouldBe(FlagSeverity.Blocking);
        flag.Params!["reason"].ShouldBe("expressionBudget");
        flag.Params["ruleId"].ShouldBe("r_expression");
    }

    [Fact]
    public void A_rule_with_a_definition_flag_raises_the_declared_code_with_its_messages()
    {
        var rule = Rule(
            "compare",
            """{ "left": "pickupDate", "op": "lt", "right": "returnDate" }""",
            severity: "blocking",
            flag: "X_PICKUP_BEFORE_RETURN",
            message: new Dictionary<string, string> { ["en"] = "Pickup is after the return date.", ["de"] = "Abholung liegt nach dem Rückgabedatum." });
        var flag = Run(Values(), rule).ShouldHaveSingleItem();
        flag.Code.ShouldBe("X_PICKUP_BEFORE_RETURN");
        flag.Severity.ShouldBe(FlagSeverity.Blocking);
        flag.Message.ShouldBe("Pickup is after the return date.");
        flag.Params!["message.de"].ShouldBe("Abholung liegt nach dem Rückgabedatum.");
        flag.Params["ruleCode"].ShouldBe(FlagCodes.RuleFailed);
    }

    [Fact]
    public void The_result_records_what_each_rule_raised()
    {
        var passing = Rule("sumEquals", """{ "path": "items[*].lineNet", "equals": "subtotalNet" }""");
        var warning = Rule("compare", """{ "left": "pickupDate", "op": "lt", "right": "returnDate" }""") with { Id = "r_warn" };
        var info = Rule("compare", """{ "left": "reason", "op": "eq", "value": "Other" }""", severity: "info") with { Id = "r_info" };
        var result = Validate(Values(), passing, warning, info, new RuleReference("r_later", "notRegisteredYet", RuleParams.None));
        result.Passed("r_sumEquals").ShouldBeTrue();
        result.Passed("r_info").ShouldBeTrue("an info note does not fail a rule");
        result.Passed("r_warn").ShouldBeFalse();
        result.Warned("r_warn").ShouldBeTrue();
        result.Passed("r_later").ShouldBeFalse("a rule that did not run did not pass");
        result.RulesNotRun.ShouldBe(["r_later"]);
    }
}
