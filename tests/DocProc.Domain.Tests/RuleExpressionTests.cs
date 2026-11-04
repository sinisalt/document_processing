using System.Text.Json.Nodes;
using DocProc.Domain.Validation.Expressions;

namespace DocProc.Domain.Tests;

/// <summary>
/// The bounded expression language of the P5 rule kinds (docs/11 §11, Correction (P5)): the JSONata subset it reads,
/// what it refuses at parse, and the limits that stop an evaluation.
/// </summary>
public sealed class RuleExpressionTests
{
    private static readonly JsonObject Doc = JsonNode.Parse("""
        {
          "total": { "amount": 30.5, "currency": "EUR" },
          "validFrom": "2026-11-01", "validUntil": "2026-11-30", "note": "Herbst",
          "lines": [
            { "sku": "A", "quantity": 2, "unitPrice": { "amount": 10, "currency": "EUR" }, "lineNet": { "amount": 20, "currency": "EUR" } },
            { "sku": "B", "quantity": 1, "unitPrice": { "amount": 10.5, "currency": "EUR" }, "lineNet": { "amount": 10.5, "currency": "EUR" } },
            { "sku": "A", "quantity": 0, "unitPrice": null, "lineNet": null }
          ]
        }
        """)!.AsObject();

    private static readonly string[] Vars = ["today", "supplier"];

    private static JsonNode? Eval(string text, JsonNode? context = null, Dictionary<string, JsonNode?>? variables = null) =>
        RuleExpression.Parse(text, Vars).Evaluate(context ?? Doc, Doc, variables);

    [Theory]
    [InlineData("$sum(lines.lineNet) = total", true)]
    [InlineData("$sum(lines.lineNet.amount) = total.amount", true)]
    [InlineData("$count(lines) = 3", true)]
    [InlineData("$count(lines[quantity > 0]) = 2", true)]
    [InlineData("lines[0].sku = 'A'", true)]
    [InlineData("lines[-1].quantity = 0", true)]
    [InlineData("validUntil >= validFrom", true)]
    [InlineData("validUntil < validFrom", false)]
    [InlineData("$exists(missing)", false)]
    [InlineData("$not($exists(missing)) or missing > 0", true)]
    [InlineData("missing > 0", false)]
    [InlineData("missing = null", false)]
    [InlineData("'B' in lines.sku", true)]
    [InlineData("$count($distinct(lines.sku)) = 2", true)]
    [InlineData("$length(note & '!') = 7", true)]
    [InlineData("$round(10 / 3, 2) = 3.33", true)]
    [InlineData("$abs(-2) = 2 and $max(lines.quantity) = 2 and $min(lines.quantity) = 0", true)]
    [InlineData("$contains($lowercase(note), 'herb')", true)]
    [InlineData("$substring(note, 0, 4) = 'Herb'", true)]
    [InlineData("$$.note = 'Herbst'", true)]
    public void Evaluates_the_JSONata_subset(string text, bool expected)
    {
        RuleExpression.Parse(text, Vars).Test(Doc, Doc).ShouldBe(expected, text);
    }

    [Fact]
    public void Row_context_reads_names_from_the_row_and_the_document_from_the_root()
    {
        var row = Doc["lines"]![0];
        var expression = RuleExpression.Parse("quantity * unitPrice", Vars);
        expression.EvaluateNumber(row, Doc).ShouldBe(20m);
        RuleExpression.Parse("$.sku = 'A' and $$.note = 'Herbst'", Vars).Test(row, Doc).ShouldBeTrue();
        expression.EvaluateNumber(Doc["lines"]![2], Doc).ShouldBeNull("an absent operand gives an absent result");
    }

    [Fact]
    public void Variables_are_bound_by_the_caller()
    {
        var variables = new Dictionary<string, JsonNode?>
        {
            ["today"] = "2026-11-15",
            ["supplier"] = new JsonObject { ["attributes"] = new JsonObject { ["erpVendorId"] = "V-1" } },
        };
        Eval("validFrom <= $today and $today <= validUntil", variables: variables)!.GetValue<bool>().ShouldBeTrue();
        Eval("$exists($supplier.attributes.erpVendorId)", variables: variables)!.GetValue<bool>().ShouldBeTrue();
        Eval("$exists($supplier.attributes.other)", variables: variables)!.GetValue<bool>().ShouldBeFalse();
    }

    [Theory]
    [InlineData("1 +", "ends too early")]
    [InlineData("$http('x')", "not a function")]
    [InlineData("$eval('1')", "not a function")]
    [InlineData("$secret", "not a variable")]
    [InlineData("a ~> b", "not part of the expression language")]
    [InlineData("'open", "not closed")]
    [InlineData("$round(1, 2, 3)", "takes 1 to 2 arguments")]
    [InlineData("a.1", "must be followed by a name")]
    public void Refuses_what_is_outside_the_language(string text, string message)
    {
        RuleExpression.TryParse(text, Vars, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain(message);
    }

    [Fact]
    public void Refuses_expressions_above_the_size_and_depth_bounds()
    {
        RuleExpression.TryParse(new string('1', ExpressionLimits.MaxLength + 1), Vars, out _, out var tooLong).ShouldBeFalse();
        tooLong!.ShouldContain("longer than");
        RuleExpression.TryParse(string.Join(" + ", Enumerable.Repeat("1", 300)), Vars, out _, out var tooMany).ShouldBeFalse();
        tooMany!.ShouldContain("more than");
        RuleExpression.TryParse(new string('(', 40) + "1" + new string(')', 40), Vars, out _, out var tooDeep).ShouldBeFalse();
        tooDeep!.ShouldContain("deeper than");
    }

    [Fact]
    public void An_evaluation_stops_at_its_step_budget()
    {
        // A filter over 20,000 rows that counts the rows again for each row is far more than 50,000 steps.
        var rows = new JsonArray([.. Enumerable.Range(0, 20_000).Select(i => (JsonNode)new JsonObject { ["n"] = i })]);
        var big = new JsonObject { ["rows"] = rows };
        var expression = RuleExpression.Parse("$count(rows[n > 0 and $count($$.rows[n > 1]) > 0])", Vars);
        Should.Throw<RuleExpressionBudgetException>(() => expression.Test(big, big));
    }

    [Fact]
    public void The_rule_budget_is_shared_by_every_evaluation_of_the_rule()
    {
        var expression = RuleExpression.Parse("$sum(lines.quantity) >= 0", Vars);
        var budget = new ExpressionBudget(100);
        Should.Throw<RuleExpressionBudgetException>(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                expression.Test(Doc, Doc, budget: budget);
            }
        });
    }

    [Fact]
    public void Type_errors_are_evaluation_errors()
    {
        Should.Throw<RuleExpressionEvaluationException>(() => Eval("note * 2"));
        Should.Throw<RuleExpressionEvaluationException>(() => Eval("1 / 0"));
        Should.Throw<RuleExpressionEvaluationException>(() => Eval("note < 1"));
    }

    [Fact]
    public void Strings_are_bounded()
    {
        var expression = RuleExpression.Parse(string.Join(" & ", Enumerable.Repeat("note", 14)) + " = ''", Vars);
        var huge = new JsonObject { ["note"] = new string('x', 1_000) };
        Should.Throw<RuleExpressionBudgetException>(() => expression.Test(huge, huge));
    }
}
