using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Validation;
using DocProc.Domain.Validation.Rules;

namespace DocProc.Domain.Tests;

/// <summary>
/// The generic rule kinds of P1 (docs/11 §11) over a values view: each kind's flags, the path grammar with <c>[*]</c>
/// and value members, the severity and message the rule list gives them, the rule id in the parameters, and the data
/// the stage hands in (duplicate matches, code lists).
/// </summary>
public sealed class GenericRuleTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);
    private static readonly ValidationContext Context = ValidationContext.For(Today, new TenantContext("Gasthaus zur Post GmbH", "DE123456789", "EUR"));

    private static readonly DocumentTypeKey PriceList = DocumentTypeKey.Parse("price-list");

    /// <summary>A small price list as the values view shows it.</summary>
    private static JsonObject Values(Action<JsonObject>? change = null)
    {
        var values = JsonNode.Parse("""
            {
              "priceListNumber": "PL-2026-11", "validFrom": "2026-11-01", "validUntil": "2026-11-30", "currency": "EUR",
              "customer": { "name": "Gasthaus zur Post GmbH", "vatId": "DE 123 456 789" },
              "listScope": "Full",
              "lines": [
                { "supplierSku": "4711", "description": "Kaffee Crema 1kg", "unit": "KGM", "price": { "amount": 12.50, "currency": "EUR" }, "packSize": 1 },
                { "supplierSku": "4712", "description": "Espresso 1kg", "unit": "Krt", "price": { "amount": 0, "currency": "EUR" }, "packSize": null }
              ]
            }
            """)!.AsObject();
        change?.Invoke(values);
        return values;
    }

    private static readonly Dictionary<string, string> Roles = new(StringComparer.Ordinal)
    {
        ["documentNumber"] = "priceListNumber",
        ["documentDate"] = "validFrom",
        ["customerParty"] = "customer",
        ["duplicateKey"] = "$supplierId, validFrom, listScope, priceListNumber",
    };

    private static DocumentView View(JsonObject values, string? tree = null) =>
        DocumentView.FromJson(PriceList, tree ?? "{}").WithValues(values, Roles);

    private static RuleReference Rule(string kind, string json, string? severity = null, string? message = null) => new(
        "r_" + kind,
        kind,
        new RuleParams(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!),
        severity switch { "blocking" => FlagSeverity.Blocking, "warning" => FlagSeverity.Warning, "info" => FlagSeverity.Info, _ => null },
        message is null ? null : new Dictionary<string, string> { ["en"] = message });

    private static IReadOnlyList<DocumentFlag> Run(JsonObject values, RuleReference rule, ValidationContext? context = null, string? tree = null)
    {
        var result = DocumentValidator.Default.Validate(View(values, tree), [rule], context ?? Context);
        result.RulesNotRun.ShouldBeEmpty("every P1 kind has an evaluator");
        return result.Flags;
    }

    [Fact]
    public void Every_generic_kind_of_P1_is_registered()
    {
        GenericRules.All.Select(r => r.Kind).ShouldBe(
            ["required", "pattern", "range", "enumMember", "dateOrder", "datePlausibility", "lookupExists", "duplicate", "confidence", "customerMatch"],
            ignoreOrder: true);
        foreach (var kind in GenericRules.All.Select(r => r.Kind))
        {
            RuleRegistry.Default.Find(kind).ShouldNotBeNull();
        }
    }

    [Fact]
    public void Required_flags_each_empty_value_with_its_concrete_path()
    {
        Run(Values(), Rule("required", """{"path":"lines[*].packSize"}""")).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            f => f.Code.ShouldBe(FlagCodes.FieldRequired),
            f => f.Severity.ShouldBe(FlagSeverity.Blocking),
            f => f.Field.ShouldBe("lines[1].packSize"),
            f => f.Params!["ruleId"].ShouldBe("r_required"),
            f => f.Params!["typeKey"].ShouldBe("price-list"));
        Run(Values(v => v["title"] = "  "), Rule("required", """{"path":"title"}""")).ShouldHaveSingleItem().Field.ShouldBe("title");
        Run(Values(), Rule("required", """{"path":"priceListNumber"}""")).ShouldBeEmpty();
    }

    [Fact]
    public void Range_reads_numbers_and_money_amounts_and_takes_the_rules_severity_and_message()
    {
        var flags = Run(Values(), Rule("range", """{"path":"lines[*].price.amount","min":0.0001}""", "blocking", "Prices must be positive"));
        var flag = flags.ShouldHaveSingleItem();
        flag.Code.ShouldBe(FlagCodes.RuleFailed);
        flag.Severity.ShouldBe(FlagSeverity.Blocking);
        flag.Field.ShouldBe("lines[1].price.amount");
        flag.Message.ShouldBe("Prices must be positive");
        flag.Params!["reason"].ShouldBe("range");
        flag.Params["value"].ShouldBe("0");
        flag.Params["min"].ShouldBe("0.0001");

        // The kind's own severity without one in the definition; an empty value is not checked.
        Run(Values(), Rule("range", """{"path":"lines[*].packSize","max":0.5}""")).ShouldHaveSingleItem().Severity.ShouldBe(FlagSeverity.Warning);
    }

    [Fact]
    public void Pattern_and_enum_member_check_strings()
    {
        Run(Values(), Rule("pattern", """{"path":"lines[*].supplierSku","pattern":"^47[0-9]{2}$"}""")).ShouldBeEmpty();
        Run(Values(), Rule("pattern", """{"path":"lines[*].supplierSku","pattern":"^4711$"}""")).ShouldHaveSingleItem().Field.ShouldBe("lines[1].supplierSku");
        Run(Values(), Rule("pattern", """{"path":"priceListNumber","pattern":"("}""")).ShouldHaveSingleItem().Params!["reason"].ShouldBe("invalidPattern");

        Run(Values(), Rule("enumMember", """{"path":"listScope","values":["Full","Changes"]}""")).ShouldBeEmpty();
        Run(Values(v => v["listScope"] = "Partial"), Rule("enumMember", """{"path":"listScope","values":["Full","Changes"]}""")).ShouldHaveSingleItem().Params!["value"].ShouldBe("Partial");
        Run(Values(), Rule("enumMember", """{"path":"lines[*].unit","codeList":"uom"}""")).ShouldHaveSingleItem().Field.ShouldBe("lines[1].unit");
    }

    /// <summary>
    /// The regex of a pattern rule is compiled once per parameters instance, not on every evaluation: a type with many
    /// pattern rules cost a fresh NonBacktracking compile per rule and document (in the worker and on every inline
    /// re-validation after a correction).
    /// </summary>
    [Fact]
    public void A_pattern_rule_compiles_its_regex_once_per_parameters()
    {
        var rule = Rule("pattern", """{"path":"lines[*].supplierSku","pattern":"^47[0-9]{2}$"}""");
        var first = GenericRules.PatternRegexOf(rule.Params).ShouldNotBeNull();
        GenericRules.PatternRegexOf(rule.Params).ShouldBeSameAs(first);
        Run(Values(), rule).ShouldBeEmpty();
        GenericRules.PatternRegexOf(rule.Params).ShouldBeSameAs(first, "the evaluation used the compiled regex");
        first.Options.ShouldBe(RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        first.MatchTimeout.ShouldBe(TimeSpan.FromMilliseconds(100));

        GenericRules.PatternRegexOf(Rule("pattern", """{"path":"priceListNumber","pattern":"("}""").Params).ShouldBeNull();
        GenericRules.PatternRegexOf(Rule("pattern", """{"path":"priceListNumber"}""").Params).ShouldBeNull();
    }

    /// <summary>
    /// A construct NonBacktracking cannot run (backreference, lookahead, lookbehind, atomic group) is refused at publish;
    /// a version published before that was checked fails the rule (RULE_FAILED, invalidPattern) instead of throwing
    /// NotSupportedException out of the validation, which answered 500 to every correction of the type.
    /// </summary>
    [Theory]
    [InlineData(@"(a)\\1")]
    [InlineData("(?=P)PL")]
    [InlineData("(?<=P)L")]
    [InlineData("(?>PL)")]
    public void A_pattern_rule_with_a_construct_NonBacktracking_cannot_run_fails_the_rule(string pattern)
    {
        var flag = Run(Values(), Rule("pattern", $$"""{"path":"priceListNumber","pattern":"{{pattern}}"}""")).ShouldHaveSingleItem();
        flag.Code.ShouldBe(FlagCodes.RuleFailed);
        flag.Params!["reason"].ShouldBe("invalidPattern");
        flag.Field.ShouldBe("priceListNumber");
    }

    [Fact]
    public void Lookup_exists_uses_the_platform_uom_list_with_codes_names_and_printed_aliases()
    {
        var uom = CodeLists.Platform[CodeLists.Uom];
        uom.Contains("KGM").ShouldBeTrue();
        uom.Contains("Kilogram").ShouldBeTrue("version 1 trees hold the UnitOfMeasure name (docs/11 §5.2)");
        uom.Resolve("stk").ShouldBe("H87");
        uom.Resolve("karton").ShouldBe("BX");

        Run(Values(v => v["lines"]![1]!["unit"] = "kt"), Rule("lookupExists", """{"path":"lines[*].unit","table":"uom"}""")).ShouldBeEmpty("kt is a printed alias of a box");
        // Krt is the tenant alias of docs/11 §6.1, which only a tenant copy of the list adds.
        var flag = Run(Values(), Rule("lookupExists", """{"path":"lines[*].unit","table":"uom"}""")).ShouldHaveSingleItem();
        flag.Field.ShouldBe("lines[1].unit");
        flag.Params!["reason"].ShouldBe("lookup");

        // A table this deployment does not have is reported, not silently passed.
        Run(Values(), Rule("lookupExists", """{"path":"lines[*].unit","table":"erp-uom"}""")).ShouldHaveSingleItem().Params!["reason"].ShouldBe("tableUnknown");
    }

    [Fact]
    public void Date_order_and_date_plausibility()
    {
        Run(Values(), Rule("dateOrder", """{"before":"validFrom","after":"validUntil","allowEqual":true}""")).ShouldBeEmpty();
        Run(Values(v => v["validUntil"] = "2026-10-01"), Rule("dateOrder", """{"before":"validFrom","after":"validUntil"}""")).ShouldHaveSingleItem().Field.ShouldBe("validUntil");
        Run(Values(v => v["validUntil"] = "2026-11-01"), Rule("dateOrder", """{"before":"validFrom","after":"validUntil","allowEqual":false}""")).ShouldHaveSingleItem();
        Run(Values(v => v["validUntil"] = "2026-11-01"), Rule("dateOrder", """{"before":"validFrom","after":"validUntil"}""")).ShouldBeEmpty("equal is allowed by default");

        Run(Values(), Rule("datePlausibility", """{"path":"validFrom","maxPastDays":60,"maxFutureDays":180}""")).ShouldBeEmpty();
        var old = Run(Values(v => v["validFrom"] = "2026-01-01"), Rule("datePlausibility", """{"path":"validFrom","maxPastDays":60}""")).ShouldHaveSingleItem();
        old.Code.ShouldBe(FlagCodes.DateImplausible);
        old.Params!["reason"].ShouldBe("pathTooOld");
        Run(Values(v => v["validFrom"] = "2027-12-01"), Rule("datePlausibility", """{"path":"validFrom","maxFutureDays":7}""")).ShouldHaveSingleItem().Params!["reason"].ShouldBe("pathFuture");

        // Relative to the document date: the validity end may lie at most 30 days after it.
        Run(Values(), Rule("datePlausibility", """{"path":"validUntil","maxFutureDays":20,"relativeTo":"documentDate"}""")).ShouldHaveSingleItem();
        Run(Values(), Rule("datePlausibility", """{"path":"validUntil","maxFutureDays":30,"relativeTo":"documentDate"}""")).ShouldBeEmpty();
    }

    [Fact]
    public void Duplicate_reports_the_check_the_stage_ran_as_a_block_or_a_revision()
    {
        var match = new DuplicateCheck([new DuplicateMatch(Guid.Parse("6c1f0000-0000-0000-0000-000000000001"), "Approved", "PL-2026-11")]);
        var context = Context with { Data = new RuleData { Duplicate = match } };

        var block = Run(Values(), Rule("duplicate", "{}"), context).ShouldHaveSingleItem();
        block.Code.ShouldBe(FlagCodes.DuplicateSuspected);
        block.Severity.ShouldBe(FlagSeverity.Blocking);
        block.Field.ShouldBe("priceListNumber");

        var revision = Run(Values(), Rule("duplicate", """{"onMatch":"revision"}""", "warning"), context).ShouldHaveSingleItem();
        revision.Code.ShouldBe(FlagCodes.RevisionOf);
        revision.Severity.ShouldBe(FlagSeverity.Warning);
        revision.Params!["documentId"].ShouldBe("6c1f0000-0000-0000-0000-000000000001");
        revision.Params["status"].ShouldBe("Approved");

        Run(Values(), Rule("duplicate", "{}"), Context with { Data = new RuleData { Duplicate = DuplicateCheck.Skipped("no supplier is matched") } }).ShouldBeEmpty();
        Run(Values(), Rule("duplicate", "{}"), Context with { Data = new RuleData { Duplicate = new DuplicateCheck([]) } }).ShouldBeEmpty();
        Run(Values(), Rule("duplicate", "{}")).ShouldBeEmpty("no check, no flag");
    }

    [Fact]
    public void Confidence_walks_every_envelope_of_the_tree_and_filters_by_paths()
    {
        const string tree = """
            {
              "priceListNumber": { "value": { "verbatim": "PL-1", "normalized": "PL-1" }, "status": "Extracted", "confidence": 0.95, "history": [] },
              "validFrom": { "value": "2026-11-01", "status": "Extracted", "confidence": 0.5, "history": [] },
              "title": { "status": "Missing", "history": [] },
              "lines": [ { "price": { "value": { "amount": 1, "currency": "EUR" }, "status": "Extracted", "confidence": 0.6, "history": [] } } ],
              "ext": { "storeNo": { "value": "0417", "status": "Extracted", "confidence": 0.7, "history": [] } }
            }
            """;
        ProvenanceWalker.Envelopes(JsonNode.Parse(tree)!.AsObject()).Select(e => e.Path).ShouldBe(["priceListNumber", "validFrom", "title", "lines[0].price", "ext.storeNo"]);

        var all = Run(Values(), Rule("confidence", "{}"), tree: tree);
        all.Select(f => f.Field).ShouldBe(["validFrom", "lines[0].price", "ext.storeNo"]);
        all.ShouldAllBe(f => f.Code == FlagCodes.LowConfidence);

        Run(Values(), Rule("confidence", """{"threshold":0.55,"paths":["lines[*]"]}"""), tree: tree).Select(f => f.Field).ShouldBe([]);
        Run(Values(), Rule("confidence", """{"threshold":0.65,"paths":["lines[*].price"]}"""), tree: tree).Select(f => f.Field).ShouldBe(["lines[0].price"]);
    }

    [Fact]
    public void Customer_match_compares_the_customer_party_role_with_the_tenant()
    {
        Run(Values(), Rule("customerMatch", "{}")).ShouldBeEmpty("same VAT id, spaced");
        var flag = Run(Values(v => v["customer"] = JsonNode.Parse("""{"name":"Gasthaus zur Post GmbH","vatId":"DE999999999"}""")), Rule("customerMatch", "{}")).ShouldHaveSingleItem();
        flag.Code.ShouldBe(FlagCodes.CustomerMismatch);
        flag.Field.ShouldBe("customer.vatId");
        Run(Values(v => v["customer"] = JsonNode.Parse("""{"name":"Pizzeria Napoli"}""")), Rule("customerMatch", "{}")).ShouldHaveSingleItem().Params!["reason"].ShouldBe("name");
        Run(Values(v => v["customer"] = null), Rule("customerMatch", "{}")).ShouldBeEmpty("nothing printed, nothing to compare");
    }

    [Fact]
    public void A_view_without_values_raises_nothing_and_coded_kinds_keep_their_flags()
    {
        var view = DocumentView.FromJson(PriceList, "{}");
        DocumentValidator.Default.Validate(view, [Rule("required", """{"path":"priceListNumber"}""")], Context).Flags.ShouldBeEmpty();
    }

    [Fact]
    public void Definition_scoped_codes_are_accepted_with_their_message()
    {
        var flag = FlagCodes.Create("X_R_SCR", "Customer number at supplier missing", "supplierCustomerRef");
        flag.Severity.ShouldBe(FlagSeverity.Warning);
        flag.Message.ShouldBe("Customer number at supplier missing");
        FlagCodes.IsDefinitionScoped("X_").ShouldBeFalse();
        FlagCodes.IsDefinitionScoped("X_lower").ShouldBeFalse();
        Should.Throw<ArgumentException>(() => FlagCodes.Create("NOT_A_CODE", "x"));
    }

    [Fact]
    public void Value_paths_expand_rows_and_members()
    {
        ValuePaths.Select(Values(), "lines[*].price.amount").Select(p => (p.Path, ValuePaths.Number(p.Value))).ShouldBe([("lines[0].price.amount", 12.50m), ("lines[1].price.amount", 0m)]);
        ValuePaths.Select(Values(), "missing.deeper").ShouldHaveSingleItem().Value.ShouldBeNull();
        ValuePaths.Select(Values(v => v["lines"] = new JsonArray()), "lines[*].sku").ShouldBeEmpty();
        ValuePaths.IsUnder("lines[3].price", "lines[*]").ShouldBeTrue();
        ValuePaths.IsUnder("linesX", "lines").ShouldBeFalse();
    }
}
