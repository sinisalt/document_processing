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
        var absent = Run(Values(v => v["items"]![1]!["quantity"] = null), rule).ShouldHaveSingleItem("a row with an absent operand was not checked: it fails, never passes");
        absent.Code.ShouldBe(FlagCodes.RuleFailed);
        absent.Field.ShouldBe("items[1].lineNet");
        absent.Params!["reason"].ShouldBe("arithmeticAbsent");
    }

    [Fact]
    public void Arithmetic_with_an_optional_operand_checks_the_rows_without_it_and_when_leaves_rows_out()
    {
        // docs/11 §11's example: the discount is on some rows only. Written as a plain operand, the rows without it
        // were not checked at all; now they fail as not checked, and $sum(discountAmount) (0 when absent) checks them.
        var values = Values(v => v["items"] = JsonNode.Parse("""
            [
              { "sku": "A", "quantity": 2, "unitPrice": 10, "lineNet": 99 },
              { "sku": "B", "quantity": 1, "unitPrice": 10, "discountAmount": 1, "lineNet": 99 },
              { "sku": "C", "quantity": 3, "unitPrice": 10, "lineNet": 30 }
            ]
            """));
        var plain = Run(values, Rule("arithmetic", """{ "rows": "items[*]", "expression": "quantity * unitPrice - discountAmount", "equals": "lineNet" }"""));
        plain.Select(f => (f.Field, f.Params!["reason"])).ShouldBe([("items[0].lineNet", "arithmeticAbsent"), ("items[1].lineNet", "arithmetic"), ("items[2].lineNet", "arithmeticAbsent")]);

        var optional = Run(values, Rule("arithmetic", """{ "rows": "items[*]", "expression": "quantity * unitPrice - $sum(discountAmount)", "equals": "lineNet" }"""));
        optional.Select(f => (f.Field, f.Params!["reason"])).ShouldBe([("items[0].lineNet", "arithmetic"), ("items[1].lineNet", "arithmetic")], "row 0 (2 x 10 = 20, printed 99) is checked now; row 2 is right");

        // A rule that checks the discounted rows only says so with when.
        var discounted = Run(values, Rule("arithmetic", """{ "rows": "items[*]", "when": "$exists(discountAmount)", "expression": "quantity * unitPrice - discountAmount", "equals": "lineNet" }"""));
        discounted.ShouldHaveSingleItem().Field.ShouldBe("items[1].lineNet");

        // The review gate does not count a rule as passed that left a row unchecked.
        var result = Validate(Values(v => v["items"]![1]!["quantity"] = null), Rule("arithmetic", """{ "rows": "items[*]", "expression": "quantity * unitPrice", "equals": "lineNet" }"""));
        result.Passed("r_arithmetic").ShouldBeFalse();
    }

    [Theory]
    [InlineData("$count($$.items[$length($string($$)) > 0]) > 0")]
    [InlineData("$count($$.items[$$ = $$]) > 0")]
    [InlineData("$length($string($$)) > 0 and $length($string($$)) > 0 and $length($string($$)) > 0")]
    [InlineData("$$ = $$ and $$ = $$ and $$ = $$ and $$ = $$ and $$ = $$ and $$ = $$ and $$ = $$ and $$ = $$")]
    public void An_expression_rule_whose_steps_do_work_as_large_as_the_document_stops_in_time_with_CHECKS_SKIPPED(string predicate)
    {
        // A 450-row list (over 100,000 characters as JSON), the predicate evaluated per row: before the steps were paid
        // in proportion to the data, these ran for minutes and raised nothing.
        var values = Values(v => v["items"] = RuleExpressionTests.PriceList(450)["lines"]!.DeepClone());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var flag = Run(values, Rule("expression", $$"""{ "forEach": "items[*]", "predicate": {{System.Text.Json.JsonSerializer.Serialize(predicate)}} }""")).ShouldHaveSingleItem();
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        flag.Code.ShouldBe(FlagCodes.ChecksSkipped);
        flag.Params!["reason"].ShouldBe("expressionBudget");
    }

    [Fact]
    public void The_rules_of_one_document_share_the_documents_expression_time()
    {
        var rule = Rule("expression", """{ "forEach": "items[*]", "predicate": "quantity > 0" }""");
        Run(Values(), rule).ShouldBeEmpty();
        var spent = Context with { ExpressionBudget = Validation.Expressions.ExpressionBudget.ForDocument(TimeSpan.Zero) };
        var flag = Run(Values(), rule, spent).ShouldHaveSingleItem();
        flag.Code.ShouldBe(FlagCodes.ChecksSkipped);
        flag.Params!["reason"].ShouldBe("expressionBudget");
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

    [Fact]
    public void RequiredIf_with_rows_evaluates_its_condition_on_every_row()
    {
        var rule = Rule("requiredIf", """{ "rows": "items[*]", "path": "unitPrice", "when": "quantity > 0" }""");
        Run(Values(), rule).ShouldBeEmpty();
        var flag = Run(Values(v => v["items"]![1]!["unitPrice"] = null), rule).ShouldHaveSingleItem();
        flag.Code.ShouldBe(FlagCodes.FieldRequired);
        flag.Field.ShouldBe("items[1].unitPrice");
        Run(Values(v => { v["items"]![1]!["unitPrice"] = null; v["items"]![1]!["quantity"] = 0; }), rule).ShouldBeEmpty("the condition is the row's");

        // The document is $$ in a row's condition.
        var documentCondition = Rule("requiredIf", """{ "rows": "items[*]", "path": "unitPrice", "when": "$$.reason = 'Damaged'" }""");
        Run(Values(v => v["items"]![0]!["unitPrice"] = null), documentCondition).ShouldHaveSingleItem().Field.ShouldBe("items[0].unitPrice");
    }

    [Fact]
    public void Compare_pairs_a_single_value_with_every_row_value()
    {
        var values = Values(v =>
        {
            v["items"]![0]!["d"] = "2026-11-01";
            v["items"]![1]!["d"] = "2026-11-02";
        });

        // left single, right rows: the single value is compared with each row (before: nothing was compared).
        var raised = Run(values, Rule("compare", """{ "left": "pickupDate", "op": "le", "right": "items[*].d" }"""));
        raised.Count.ShouldBe(2);
        raised.ShouldAllBe(f => f.Params!["reason"] == "compare");
        raised.Select(f => f.Params!["otherField"]).ShouldBe(["items[0].d", "items[1].d"]);
        Run(values, Rule("compare", """{ "left": "returnDate", "op": "ge", "right": "items[*].d" }""")).ShouldBeEmpty();
    }

    [Fact]
    public void Compare_fails_two_row_paths_whose_counts_differ()
    {
        var values = Values(v => v["other"] = new JsonArray(new JsonObject { ["q"] = 1 }, new JsonObject { ["q"] = 2 }, new JsonObject { ["q"] = 3 }));
        var flag = Run(values, Rule("compare", """{ "left": "items[*].quantity", "op": "le", "right": "other[*].q" }""")).ShouldHaveSingleItem();
        flag.Code.ShouldBe(FlagCodes.RuleFailed);
        flag.Params!["reason"].ShouldBe("compareUnpaired");
    }

    [Theory]
    [InlineData("items[99999999999].sku = 'A'")]
    [InlineData("$exists(items[99999999999999999999])")]
    [InlineData("$exists(items[-99999999999999999999])")]
    [InlineData("$substring(reason, 99999999999) = 'A'")]
    [InlineData("$substring(reason, -99999999999) = 'Damaged'")]
    [InlineData("$substring(reason, 0, 99999999999999) = 'A'")]
    public void Huge_indexes_and_substring_arguments_do_not_escape_the_evaluator(string predicate)
    {
        var rule = Rule("expression", "{ \"predicate\": " + JsonSerializer.Serialize(predicate) + " }");
        var flags = Run(Values(), rule);
        flags.ShouldNotContain(f => f.Params!.ContainsKey("reason") && f.Params["reason"] == "expressionError", "a number past the end is past the end, not an error");
    }

    [Fact]
    public void Substring_arguments_from_the_document_are_clamped()
    {
        // The arguments come from the document: a quantity of 10^15 must not crash the validation.
        var rule = Rule("expression", """{ "forEach": "items[*]", "predicate": "$substring(sku, quantity) = ''", "field": "sku" }""");
        Run(Values(v => v["items"]![0]!["quantity"] = 1_000_000_000_000_000m), rule).ShouldBeEmpty("past the end is the empty string");
        var length = Rule("expression", """{ "predicate": "$substring(reason, 0, big) = 'Damaged'" }""");
        Run(Values(v => v["big"] = 1e15m), length).ShouldBeEmpty();
    }
}
