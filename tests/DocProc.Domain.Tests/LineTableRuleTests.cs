using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Validation;
using DocProc.Domain.Validation.Rules;

namespace DocProc.Domain.Tests;

/// <summary>
/// The line-table rule kinds of P5 (docs/11 §5.8, §11): <c>tableComplete</c> over the table report (rejected rows, pages
/// without rows, the printed count, rows without a report), <c>unique</c>, <c>ordered</c> (tier breaks and prices) and
/// <c>deltaToPrevious</c> (row values against the last approved list), with the severities the validator keeps.
/// </summary>
public sealed class LineTableRuleTests
{
    private static readonly ValidationContext Context = ValidationContext.For(new DateOnly(2026, 11, 3), new TenantContext("Gasthaus zur Post GmbH", "DE123456789", "EUR"));

    private static JsonObject List(int rows = 3, Action<JsonObject>? change = null)
    {
        var lines = new JsonArray();
        for (var i = 0; i < rows; i++)
        {
            lines.Add(new JsonObject
            {
                ["supplierSku"] = (104000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["description"] = $"Artikel {i}",
                ["price"] = new JsonObject { ["amount"] = 10m + i, ["currency"] = "EUR" },
                ["lineValidFrom"] = null,
                ["tiers"] = new JsonArray(
                    new JsonObject { ["minQuantity"] = 10, ["price"] = new JsonObject { ["amount"] = 9.5m + i, ["currency"] = "EUR" } },
                    new JsonObject { ["minQuantity"] = 50, ["price"] = new JsonObject { ["amount"] = 9m + i, ["currency"] = "EUR" } }),
            });
        }

        var values = new JsonObject { ["validFrom"] = "2027-01-01", ["declaredLineCount"] = null, ["lines"] = lines };
        change?.Invoke(values);
        return values;
    }

    private static RuleReference Rule(string kind, string json, FlagSeverity? severity = null) =>
        new("r_" + kind, kind, new RuleParams(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!), severity);

    private static IReadOnlyList<DocumentFlag> Run(JsonObject values, RuleReference rule, RuleData? data = null)
    {
        var context = Context with { Data = data ?? RuleData.None };
        var result = DocumentValidator.Default.Validate(DocumentView.FromJson(DocumentTypeKey.Parse("price-list"), "{}").WithValues(values), [rule], context);
        result.RulesNotRun.ShouldBeEmpty();
        return result.Flags;
    }

    private static RuleData Reports(int rows, int rejected = 0, int[]? withoutRows = null, Dictionary<int, int>? rejectedByPage = null) =>
        RuleData.None with
        {
            TableReports = new Dictionary<string, TableReportSummary>(StringComparer.Ordinal)
            {
                ["lines"] = new(rows, rejected, [1, 2, 3], withoutRows ?? [], rejectedByPage ?? new Dictionary<int, int>()),
            },
        };

    [Fact]
    public void Every_line_table_kind_is_registered()
    {
        foreach (var kind in new[] { GenericRules.TableComplete, GenericRules.Unique, GenericRules.Ordered, GenericRules.DeltaToPrevious })
        {
            RuleRegistry.Default.Find(kind).ShouldNotBeNull(kind);
        }
    }

    [Fact]
    public void TableComplete_passes_a_complete_report_and_fails_rows_without_one_unless_allowed()
    {
        var rule = Rule("tableComplete", """{ "path": "lines", "maxRejectedRows": 0, "everyPageInRange": true }""", FlagSeverity.Blocking);
        Run(List(), rule, Reports(3)).ShouldBeEmpty();

        var unreported = Run(List(), rule).ShouldHaveSingleItem();
        unreported.Code.ShouldBe(FlagCodes.RuleFailed);
        unreported.Params!["reason"].ShouldBe("tableUnreported");
        unreported.Severity.ShouldBe(FlagSeverity.Blocking);

        Run(List(), Rule("tableComplete", """{ "path": "lines", "allowUnreported": true }""", FlagSeverity.Blocking)).ShouldBeEmpty("LLM-read rows leave the decision to the gate's lines conditions");
    }

    [Fact]
    public void TableComplete_raises_rejected_rows_per_page_and_pages_without_rows()
    {
        var strict = Rule("tableComplete", """{ "path": "lines", "maxRejectedRows": 0, "everyPageInRange": true }""", FlagSeverity.Blocking);
        var flags = Run(List(), strict, Reports(3, rejected: 2, withoutRows: [3], rejectedByPage: new() { [2] = 2 }));
        var rejected = flags.Single(f => f.Code == FlagCodes.TableRowsRejected);
        rejected.Params!["page"].ShouldBe("2");
        rejected.Params["rows"].ShouldBe("2");
        rejected.Severity.ShouldBe(FlagSeverity.Blocking);
        var empty = flags.Single(f => f.Code == FlagCodes.TablePageNoRows);
        empty.Params!["page"].ShouldBe("3");
        empty.Severity.ShouldBe(FlagSeverity.Blocking, "everyPageInRange");

        // Without everyPageInRange a page without rows is a warning, even under a blocking rule; within the allowance
        // rejected rows pass.
        var lenient = Rule("tableComplete", """{ "path": "lines", "maxRejectedRows": 2 }""", FlagSeverity.Blocking);
        var warning = Run(List(), lenient, Reports(3, rejected: 2, withoutRows: [3], rejectedByPage: new() { [2] = 2 })).ShouldHaveSingleItem();
        warning.Code.ShouldBe(FlagCodes.TablePageNoRows);
        warning.Severity.ShouldBe(FlagSeverity.Warning);
    }

    [Fact]
    public void TableComplete_checks_the_printed_count_against_the_rows_read()
    {
        var rule = Rule("tableComplete", """{ "path": "lines", "declaredCount": "declaredLineCount" }""", FlagSeverity.Blocking);
        Run(List(3, v => v["declaredLineCount"] = 3), rule, Reports(3)).ShouldBeEmpty();
        var flag = Run(List(3, v => v["declaredLineCount"] = 412), rule, Reports(3)).ShouldHaveSingleItem();
        flag.Params!["reason"].ShouldBe("tableCount");
        flag.Params["declared"].ShouldBe("412");
        flag.Params["read"].ShouldBe("3");
        Run(List(3), rule, Reports(3)).ShouldBeEmpty("no printed count, nothing to compare");
    }

    [Fact]
    public void Unique_flags_the_repeated_value_once_and_respects_the_scope()
    {
        var rule = Rule("unique", """{ "path": "lines[*].supplierSku", "scopeBy": ["lineValidFrom"] }""", FlagSeverity.Blocking);
        Run(List(), rule).ShouldBeEmpty();

        var twice = List(3, v => v["lines"]![2]!["supplierSku"] = "104000");
        var flag = Run(twice, rule).ShouldHaveSingleItem();
        flag.Field.ShouldBe("lines[2].supplierSku");
        flag.Params!["firstField"].ShouldBe("lines[0].supplierSku");

        // The same article with two validities is two rows of its own.
        var scoped = List(3, v =>
        {
            v["lines"]![2]!["supplierSku"] = "104000";
            v["lines"]![2]!["lineValidFrom"] = "2027-02-01";
        });
        Run(scoped, rule).ShouldBeEmpty();
    }

    [Fact]
    public void Ordered_checks_tier_breaks_ascend_and_their_prices_do_not_rise()
    {
        var rule = Rule("ordered", """{ "path": "lines[*].tiers", "by": "minQuantity", "strict": true, "then": { "path": "price.amount", "order": "nonIncreasing" } }""");
        Run(List(), rule).ShouldBeEmpty();

        var breaks = List(2, v => v["lines"]![1]!["tiers"]![1]!["minQuantity"] = 10);
        Run(breaks, rule).ShouldHaveSingleItem().Field.ShouldBe("lines[1].tiers[1].minQuantity");

        var rising = List(2, v => v["lines"]![0]!["tiers"]![1]!["price"]!["amount"] = 20m);
        var flag = Run(rising, rule).ShouldHaveSingleItem();
        flag.Field.ShouldBe("lines[0].tiers[1].price.amount");
        flag.Params!["reason"].ShouldBe("orderedThen");

        Run(List(1, v => v["lines"]![0]!["tiers"] = new JsonArray()), rule).ShouldBeEmpty("a row without tiers");
    }

    [Fact]
    public void DeltaToPrevious_passes_with_a_note_without_a_previous_list()
    {
        var rule = Rule("deltaToPrevious", """{ "path": "lines[*].price.amount", "joinOn": "supplierSku", "maxChangePercent": 25 }""", FlagSeverity.Warning);
        var result = DocumentValidator.Default.Validate(DocumentView.FromJson(DocumentTypeKey.Parse("price-list"), "{}").WithValues(List()), [rule], Context);
        var note = result.Flags.ShouldHaveSingleItem();
        note.Severity.ShouldBe(FlagSeverity.Info, "a note stays a note under a warning rule");
        note.Params!["reason"].ShouldBe("noPrevious");
        result.Passed("r_deltaToPrevious").ShouldBeTrue();
    }

    [Fact]
    public void DeltaToPrevious_flags_the_rows_over_the_threshold_and_lists_them_for_the_grid()
    {
        var rule = Rule("deltaToPrevious", """{ "path": "lines[*].price.amount", "joinOn": "supplierSku", "maxChangePercent": 25, "maxChangedRowsPercent": 30 }""", FlagSeverity.Warning);
        var previous = new PreviousDocument(Guid.NewGuid(), DateTimeOffset.UtcNow, List(4));

        // A yearly 3 % on every row raises no flag (docs/11 §5.8): moves below ignoreBelowPercent (5 %) are not counted.
        var normal = List(4, v =>
        {
            foreach (var row in v["lines"]!.AsArray())
            {
                row!["price"]!["amount"] = Math.Round(row["price"]!["amount"]!.GetValue<decimal>() * 1.03m, 2);
            }
        });
        Run(normal, rule, RuleData.None with { Previous = previous }).ShouldBeEmpty();

        // An article that is not in the previous list is new, not changed.
        Run(List(5), rule, RuleData.None with { Previous = previous }).ShouldBeEmpty();
    }

    [Fact]
    public void DeltaToPrevious_one_article_up_40_percent_is_one_row_flag_and_a_summary()
    {
        var rule = Rule("deltaToPrevious", """{ "path": "lines[*].price.amount", "joinOn": "supplierSku", "maxChangePercent": 25, "maxChangedRowsPercent": 30 }""", FlagSeverity.Warning);
        var previous = new PreviousDocument(Guid.NewGuid(), DateTimeOffset.UtcNow, List(10));
        var current = List(10, v => v["lines"]![7]!["price"]!["amount"] = 17m * 1.4m);

        var flags = Run(current, rule, RuleData.None with { Previous = previous });
        flags.Count.ShouldBe(2);
        flags[0].Field.ShouldBe("lines");
        flags[0].Params!["rows"].ShouldBe("7");
        flags[0].Params!["changed"].ShouldBe("1");
        flags[0].Params!["previousDocumentId"].ShouldBe(previous.DocumentId.ToString());
        flags[1].Field.ShouldBe("lines[7].price.amount");
        flags[1].Params!["changePercent"].ShouldBe("40");
        flags.ShouldAllBe(f => f.Severity == FlagSeverity.Warning);

        // Many small changes: more than 30 % of the rows changed, none over 25 %.
        var many = List(10, v =>
        {
            for (var i = 0; i < 5; i++)
            {
                v["lines"]![i]!["price"]!["amount"] = 10m + i + 1m;
            }
        });
        var summary = Run(many, rule, RuleData.None with { Previous = previous }).ShouldHaveSingleItem();
        summary.Params!["changedPercent"].ShouldBe("50");
        summary.Params.ContainsKey("rows").ShouldBeFalse();
    }

    [Fact]
    public void A_split_row_path_names_the_rows_and_the_field()
    {
        GenericRules.SplitRowPath("lines[*].price.amount").ShouldBe(("lines[*]", "price.amount"));
        GenericRules.SplitRowPath("lines[*].tiers[*].price").ShouldBe(("lines[*].tiers[*]", "price"));
        GenericRules.SplitRowPath("validFrom").ShouldBeNull();
    }
}
