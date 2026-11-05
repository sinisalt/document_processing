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

    [Theory]
    [InlineData("$exists(lines[99999999999999999999])", false)]
    [InlineData("$exists(lines[-99999999999])", false)]
    [InlineData("$substring(note, 99999999999) = ''", true)]
    [InlineData("$substring(note, -99999999999) = 'Herbst'", true)]
    [InlineData("$substring(note, 1, 99999999999999) = 'erbst'", true)]
    public void Indexes_and_substring_arguments_beyond_int_are_clamped(string text, bool expected)
    {
        RuleExpression.Parse(text, Vars).Test(Doc, Doc).ShouldBe(expected, text);
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

    /// <summary>A price list of <paramref name="rows"/> rows as the values view holds it (about 280 characters a row).</summary>
    internal static JsonObject PriceList(int rows) => new()
    {
        ["validFrom"] = "2027-02-01",
        ["lines"] = new JsonArray([.. Enumerable.Range(0, rows).Select(i => (JsonNode)new JsonObject
        {
            ["articleNumber"] = $"NW-{i:D5}",
            ["description"] = $"Artikel {i} Bio Vollmilch 3,8 % Fett 1 l Tetra Pak, regional",
            ["unitPrice"] = new JsonObject { ["amount"] = i + 0.25m, ["currency"] = "EUR" },
            ["unit"] = "Stück",
            ["packSize"] = "12x1l",
            ["gtin"] = $"40{i:D11}",
            ["quantity"] = 1,
        })]),
    };

    [Fact]
    public void String_of_an_object_or_a_list_stops_at_the_string_limit_without_writing_the_rest()
    {
        var values = PriceList(450);
        values.ToJsonString().Length.ShouldBeGreaterThan(100_000);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Should.Throw<RuleExpressionBudgetException>(() => RuleExpression.Parse("$length($string($$))", Vars).Evaluate(values))
            .Message.ShouldContain($"{ExpressionLimits.MaxString} characters");
        Should.Throw<RuleExpressionBudgetException>(() => RuleExpression.Parse("$string(lines)", Vars).Evaluate(values));
        Should.Throw<RuleExpressionBudgetException>(() => RuleExpression.Parse("$string($$) & 'x'", Vars).Evaluate(values));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));

        // Below the limit, the text is the compact JSON it always was.
        RuleExpression.Parse("$string(lines[0])", Vars).Evaluate(Doc)!.GetValue<string>().ShouldBe(Doc["lines"]![0]!.ToJsonString());
        RuleExpression.Parse("$string(lines.sku)", Vars).Evaluate(Doc)!.GetValue<string>().ShouldBe("""["A","B","A"]""");
        RuleExpression.Parse("$string(total)", Vars).Evaluate(Doc)!.GetValue<string>().ShouldBe(Doc["total"]!.ToJsonString());
    }

    [Theory]
    [InlineData("$$ = $$")]
    [InlineData("$$ in $$")]
    [InlineData("$count($distinct($$.lines)) > 0")]
    [InlineData("$count($$.lines[$$ = $$]) > 0")]
    public void Comparing_whole_objects_costs_a_step_per_node(string text)
    {
        // One evaluation over a 450-row list: a deep comparison of the document is thousands of steps, not one, so a
        // few of them reach the evaluation's limit instead of running for seconds.
        var values = PriceList(450);
        var expression = RuleExpression.Parse(string.Join(" and ", Enumerable.Repeat(text, 10)), Vars);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Should.Throw<RuleExpressionBudgetException>(() => expression.Test(values, values));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Objects_compare_by_value()
    {
        RuleExpression.Parse("lines[0] = lines[0]", Vars).Test(Doc, Doc).ShouldBeTrue();
        RuleExpression.Parse("lines[0] = lines[1]", Vars).Test(Doc, Doc).ShouldBeFalse();
        RuleExpression.Parse("lines[1] in lines", Vars).Test(Doc, Doc).ShouldBeTrue();
        RuleExpression.Parse("$count($distinct(lines.unitPrice)) = 2", Vars).Test(Doc, Doc).ShouldBeTrue();
        var copy = JsonNode.Parse("""{ "a": { "x": 1, "y": [1, "b", null] }, "b": { "y": [1, "b", null], "x": 1.0 }, "c": { "x": 1, "y": [1, "b"] } }""")!;
        RuleExpression.Parse("a = b", Vars).Test(copy, copy).ShouldBeTrue("the order of the members does not matter, nor 1 against 1.0");
        RuleExpression.Parse("a = c", Vars).Test(copy, copy).ShouldBeFalse();
    }

    [Fact]
    public void An_evaluation_and_a_budget_stop_at_their_time()
    {
        var values = PriceList(450);
        var expression = RuleExpression.Parse("$count(lines[quantity > 0]) > 0", Vars);
        expression.Test(values, values).ShouldBeTrue();

        // A rule's budget whose time is spent stops every evaluation on it, also one that would take few steps.
        var spent = new ExpressionBudget(maxTime: TimeSpan.FromTicks(1));
        Should.Throw<RuleExpressionBudgetException>(() => expression.Test(values, values, budget: spent)).Message.ShouldContain("ran longer than");
        spent.Elapsed.ShouldBeGreaterThan(TimeSpan.Zero, "the time the evaluation ran is spent from the budget");

        // The document's budget is the rules' parent: once its time is spent, a new rule's budget stops at once.
        var document = ExpressionBudget.ForDocument(TimeSpan.FromMilliseconds(1));
        var first = ExpressionBudget.ForRule(document);
        Should.Throw<RuleExpressionBudgetException>(() =>
        {
            while (true)
            {
                expression.Test(values, values, budget: first);
            }
        });
        Should.Throw<RuleExpressionBudgetException>(() => RuleExpression.Parse("1 = 1", Vars).Test(values, values, budget: ExpressionBudget.ForRule(document)))
            .Message.ShouldContain("rules' expressions ran longer than");
    }

    [Fact]
    public void Reading_an_array_costs_a_step_per_item()
    {
        // $$.lines reads 20,000 items: alone it is within an evaluation's steps, three times it is not.
        var rows = new JsonArray([.. Enumerable.Range(0, 20_000).Select(i => (JsonNode)i)]);
        var big = new JsonObject { ["rows"] = rows };
        RuleExpression.Parse("$count(rows) = 20000", Vars).Test(big, big).ShouldBeTrue();
        Should.Throw<RuleExpressionBudgetException>(() => RuleExpression.Parse("$count(rows) + $count(rows) + $count(rows) > 0", Vars).Test(big, big));
    }
}
