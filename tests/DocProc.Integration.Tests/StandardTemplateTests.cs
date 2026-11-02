using System.Text.Json.Nodes;
using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Domain.Documents;
using DocProc.Domain.Validation;

namespace DocProc.Integration.Tests;

/// <summary>
/// The definition format (docs/11 §5.2), its structural checks (§5.7), the seeded platform templates (§5.1, §21 step 2)
/// and the P0 <see cref="IEffectiveConfigResolver"/> that returns them.
/// </summary>
public sealed class StandardTemplateTests
{
    private static readonly string[] P0Templates =
        ["std.credit-note", "std.delivery-note", "std.generic", "std.invoice", "std.purchase-order", "std.reminder", "std.statement"];

    public static TheoryData<string> TemplateKeys() => [.. P0Templates];

    [Fact]
    public void P0_ships_the_templates_of_the_legacy_types_one_per_type()
    {
        StandardTemplates.All.Select(t => t.Key).ShouldBe(P0Templates);

        foreach (var type in DocumentTypeKey.Legacy)
        {
            var template = StandardTemplates.ForLegacyType(type);
            if (type == DocumentTypeKey.Unknown)
            {
                template.ShouldBeNull("Unknown stays a reserved key without a definition");
            }
            else
            {
                template.ShouldNotBeNull(type.ToString()).LegacyKey.ShouldBe(type.ToString());
            }
        }

        StandardTemplates.ForLegacyType(DocumentTypeKey.Other)!.Key.ShouldBe("std.generic");
        StandardTemplates.Find("std.price-list").ShouldBeNull("the price-list templates ship with P1");
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public void Every_template_is_well_formed_with_only_rule_kinds_registered_today(string key)
    {
        var template = StandardTemplates.Find(key).ShouldNotBeNull();

        DefinitionChecker.Check(template, RuleRegistry.Default.Kinds).ShouldBeEmpty();
        template.Version.ShouldBe(1);
        template.Base.ShouldBeNull("a template is not based on another");
        template.Name.Keys.ShouldBe(["en", "de"], ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public void A_template_reads_back_the_same_after_a_write(string key)
    {
        var template = StandardTemplates.Find(key)!;

        var again = DefinitionSerializer.Parse(DefinitionSerializer.Write(template));

        JsonNode.DeepEquals(JsonNode.Parse(DefinitionSerializer.Write(again)), JsonNode.Parse(DefinitionSerializer.Write(template))).ShouldBeTrue();
        Contains(JsonNode.Parse(DefinitionSerializer.Write(template)), JsonNode.Parse(StandardTemplates.ReadJson(key)), key);
    }

    [Fact]
    public void The_credit_note_shares_the_invoice_field_tree_and_differs_in_names_and_the_original_invoice_reference()
    {
        var invoice = StandardTemplates.Find("std.invoice")!;
        var credit = StandardTemplates.Find("std.credit-note")!;

        DefinitionPaths.Enumerate(credit).Select(f => (f.Path, f.Field.Id, f.Field.Kind)).ShouldBe(DefinitionPaths.Enumerate(invoice).Select(f => (f.Path, f.Field.Id, f.Field.Kind)));
        credit.Roles.ShouldBe(invoice.Roles with { DuplicateKey = credit.Roles.DuplicateKey });
        DefinitionPaths.Find(credit, "invoiceNumber")!.Label!["en"].ShouldBe("Credit note no.");
        DefinitionPaths.Find(credit, "originalInvoiceReference")!.Required.ShouldBeTrue();
        DefinitionPaths.Find(invoice, "originalInvoiceReference")!.Required.ShouldBeFalse();
    }

    [Fact]
    public void Paths_reach_list_values_and_row_fields_only_through_the_rows()
    {
        var invoice = StandardTemplates.Find("std.invoice")!;

        DefinitionPaths.Find(invoice, "lines[*].lineNet")!.Kind.ShouldBe(FieldKinds.Money);
        DefinitionPaths.Find(invoice, "lines")!.Kind.ShouldBe(FieldKinds.Array);
        DefinitionPaths.Find(invoice, "orderReferences[*]")!.List.ShouldBeTrue();
        DefinitionPaths.Find(invoice, "lines.lineNet").ShouldBeNull();
        DefinitionPaths.Find(invoice, "currency[*]").ShouldBeNull();
        DefinitionPaths.Find(invoice, "nothing").ShouldBeNull();
        DefinitionPaths.Find(invoice, string.Empty).ShouldBeNull();
        DefinitionPaths.EnvelopePaths(invoice).ShouldContain("orderReferences[*]");
        DefinitionPaths.EnvelopePaths(invoice).ShouldNotContain("taxBreakdown");
        DefinitionPaths.EnvelopePaths(invoice).ShouldNotContain("lines[*].lineNumber");
    }

    [Theory]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"colour":"red"}""", "colour")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"string","lable":{"en":"A"}}]}""", "lable")]
    [InlineData("""{"key":"x","name":{"en":"X"},"fields":[]}""", "version")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],}""", null)]
    [InlineData("""{"key":"x","version":1,/* note */"name":{"en":"X"},"fields":[]}""", null)]
    [InlineData("null", null)]
    public void Reading_is_strict(string json, string? named)
    {
        var error = Should.Throw<DefinitionFormatException>(() => DefinitionSerializer.Parse(json));

        if (named is not null)
        {
            error.Message.ShouldContain(named);
        }
    }

    [Theory]
    [InlineData("""{"key":"1x","version":1,"name":{"en":"X"},"fields":[]}""", "not a valid type key")]
    [InlineData("""{"key":"x","version":0,"name":{"en":"X"},"fields":[]}""", "version")]
    [InlineData("""{"key":"x","version":1,"name":{},"fields":[]}""", "non-empty name")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"base":{"template":"std.invoice","version":1,"mode":"fork"},"fields":[]}""", "neither 'adopt' nor 'copy'")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"string"},{"id":"fld_a","key":"b","kind":"string"}]}""", "used by another field")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"field1","key":"a","kind":"string"}]}""", "fld_xxx")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"string"},{"id":"fld_b","key":"a","kind":"string"}]}""", "declared twice")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"A b","kind":"string"}]}""", "not a valid field key")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"colour"}]}""", "not a field kind")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"array"}]}""", "item.fields")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"array","anchor":"b","item":{"fields":[{"id":"fld_c","key":"c","kind":"string"}]}}]}""", "anchor 'b'")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"array","maxItems":20001,"item":{"fields":[{"id":"fld_c","key":"c","kind":"string"}]}}]}""", "maxItems")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"string","maxItems":5}]}""", "only allowed on arrays")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"array","list":true,"item":{"fields":[{"id":"fld_c","key":"c","kind":"string"}]}}]}""", "list cannot be set")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"object"}]}""", "an object needs fields")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"enum"}]}""", "options or a codeList")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"enum","options":[{"code":"A"},{"code":"A"}]}]}""", "listed twice")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"string","options":[{"code":"A"}]}]}""", "only allowed on enums")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"money","currencyFrom":"b"},{"id":"fld_b","key":"b","kind":"string"}]}""", "does not name a currency field")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"decimal","currencyFrom":"b"}]}""", "only allowed on money")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"decimal","min":5,"max":1}]}""", "min is greater")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"string","pattern":"(a"}]}""", "pattern is not a valid")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"string","pattern":"(a)\\1"}]}""", "pattern is not a valid")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"string","visibleWhen":{"field":"b","equals":1}}]}""", "visibleWhen")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"ui":{"sections":[{"id":"s"}]},"fields":[{"id":"fld_a","key":"a","kind":"string","section":"t"}]}""", "not declared in ui.sections")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"roles":{"documentNumber":"a"},"fields":[{"id":"fld_a","key":"a","kind":"string"}]}""", "roles.documentNumber")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"roles":{"total":"b"},"fields":[]}""", "'b' is not a field")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"roles":{"lineNet":"n"},"fields":[{"id":"fld_n","key":"n","kind":"money"}]}""", "needs the lineItems role")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"roles":{"lineItems":"l","lineNet":"n"},"fields":[{"id":"fld_n","key":"n","kind":"money"},{"id":"fld_l","key":"l","kind":"array","item":{"fields":[{"id":"fld_x","key":"x","kind":"money"}]}}]}""", "not a field of the lineItems rows")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"roles":{"duplicateKey":["$supplierId","nope"]},"fields":[]}""", "neither $supplierId nor a field")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"rules":[{"id":"r","kind":"tableComplete"}]}""", "rule kind 'tableComplete' is not available yet")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"rules":[{"id":"r","kind":"invoice.totals"},{"id":"r","kind":"invoice.currency"}]}""", "used twice")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"rules":[{"id":"r","kind":"invoice.totals","severity":"fatal"}]}""", "severity")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"processing":{"extract":[{"id":"r","kind":"validate.rules"}]}}""", "not a extract step kind")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"processing":{"split":{"mode":"sometimes"}}}""", "processing.split.mode")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"processing":{"review":{"dueBy":"validFrom"}}}""", "bound validFrom")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"processing":{"review":{"requirePassedRules":["r_x"]}}}""", "'r_x'")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"processing":{"review":{"priority":"asap"}}}""", "priority")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"classification":{"keywords":{"deu":["Rechnung"]}}}""", "ISO 639-1")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"classification":{"negatives":{"de":[]}}}""", "non-empty phrases")]
    public void The_checker_names_what_is_wrong(string json, string expected)
    {
        var problems = DefinitionChecker.Check(DefinitionSerializer.Parse(json), RuleRegistry.Default.Kinds);

        problems.ShouldNotBeEmpty();
        string.Join("\n", problems).ShouldContain(expected);
    }

    [Fact]
    public void A_fork_with_a_custom_field_and_a_bag_passes_the_checks()
    {
        // The shape of docs/11 §5.3, reduced to what P0 can check.
        const string Json = """
            {
              "key": "CreditNote", "version": 3, "base": { "template": "std.credit-note", "version": 1, "mode": "copy" },
              "name": { "en": "Credit note", "de": "Gutschrift" },
              "roles": { "documentNumber": "invoiceNumber", "currency": "currency", "total": "totalGross", "lineItems": "lines",
                         "lineNet": "lines[*].lineNet", "duplicateKey": ["$supplierId", "invoiceNumber"] },
              "fields": [
                { "id": "fld_inv_no", "key": "invoiceNumber", "kind": "identifier", "required": true, "indexed": true },
                { "id": "fld_cur", "key": "currency", "kind": "currency" },
                { "id": "fld_gross", "key": "totalGross", "kind": "money", "currencyFrom": "currency" },
                { "id": "fld_c_scr", "key": "supplierCustomerRef", "kind": "identifier", "indexed": true,
                  "extraction": { "labels": { "de": ["Kunden-Nr."] }, "pattern": "^[0-9]{3}\\s?[0-9]{6}$" } },
                { "id": "fld_lines", "key": "lines", "kind": "array", "anchor": "description", "maxItems": 500, "item": { "fields": [
                  { "id": "fld_l_desc", "key": "description", "kind": "text" },
                  { "id": "fld_l_net", "key": "lineNet", "kind": "money", "currencyFrom": "currency" },
                  { "id": "fld_l_ext", "key": "ext", "kind": "bag" } ] } },
                { "id": "fld_ext", "key": "ext", "kind": "bag" }
              ],
              "rules": [ { "id": "r_totals", "kind": "invoice.totals" } ],
              "processing": { "review": { "autoApprove": true, "minConfidence": 0.9, "requirePassedRules": ["r_totals"] } }
            }
            """;

        DefinitionChecker.Check(DefinitionSerializer.Parse(Json), RuleRegistry.Default.Kinds).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(WellKnownTypeKeys.Invoice, "std.invoice", true)]
    [InlineData(WellKnownTypeKeys.CreditNote, "std.credit-note", true)]
    [InlineData(WellKnownTypeKeys.PurchaseOrder, "std.purchase-order", false)]
    [InlineData(WellKnownTypeKeys.DeliveryNote, "std.delivery-note", false)]
    [InlineData(WellKnownTypeKeys.Statement, "std.statement", false)]
    [InlineData(WellKnownTypeKeys.Reminder, "std.reminder", false)]
    [InlineData(WellKnownTypeKeys.Other, "std.generic", false)]
    public async Task The_P0_resolver_returns_the_adopted_standard_template_for_every_tenant_and_supplier(DocumentTypeKey type, string template, bool autoApprove)
    {
        var resolver = StandardTemplateConfigResolver.Instance;
        var request = new EffectiveConfigRequest(Guid.NewGuid(), type, Guid.NewGuid());

        var config = await resolver.ResolveAsync(request, TestContext.Current.CancellationToken);
        var other = await resolver.ResolveAsync(new EffectiveConfigRequest(Guid.NewGuid(), type), TestContext.Current.CancellationToken);

        config.Request.ShouldBe(request);
        var resolved = config.Type.ShouldNotBeNull();
        resolved.TypeKey.ShouldBe(type.ToString());
        resolved.TemplateKey.ShouldBe(template);
        resolved.TemplateVersion.ShouldBe(1);
        resolved.Origin.ShouldBe(DefinitionOrigin.Adopted);
        resolved.Definition.ShouldBeSameAs(StandardTemplates.Find(template));
        resolved.AutoApprove.ShouldBe(autoApprove);
        other.Type.ShouldBeSameAs(resolved, "no tenant or supplier configuration exists before P1 and P4");
    }

    [Fact]
    public async Task An_unknown_document_has_no_definition()
    {
        var config = await StandardTemplateConfigResolver.Instance.ResolveAsync(new EffectiveConfigRequest(Guid.NewGuid(), DocumentTypeKey.Unknown), TestContext.Current.CancellationToken);

        config.Type.ShouldBeNull();
    }

    [Fact]
    public void Auto_approval_needs_a_rule_as_well_as_the_switch()
    {
        var invoice = StandardTemplates.Find("std.invoice")!;

        new EffectiveType("Invoice", invoice with { Rules = [] }, DefinitionOrigin.Copied, invoice.Key, 1).AutoApprove.ShouldBeFalse();
        new EffectiveType("Invoice", invoice, DefinitionOrigin.Copied, invoice.Key, 1).AutoApprove.ShouldBeTrue();
    }

    [Fact]
    public void Rule_parameters_reach_the_validator_as_raw_values()
    {
        var definition = DefinitionSerializer.Parse("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"rules":[{"id":"r_t","kind":"invoice.totals","params":{"tolerance":0.02,"paths":["a","b"],"none":null}}]}""");

        var rule = definition.ToRuleReferences().ShouldHaveSingleItem();

        rule.Id.ShouldBe("r_t");
        rule.Kind.ShouldBe("invoice.totals");
        rule.Params.TryGet("tolerance", out var tolerance).ShouldBeTrue();
        tolerance.GetDecimal().ShouldBe(0.02m);
        rule.Params.Values["paths"].GetArrayLength().ShouldBe(2);
        rule.Params.Values["none"].ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    /// <summary>
    /// Every value of <paramref name="shipped"/> is in <paramref name="written"/> at the same place: the records keep all
    /// the shipped JSON says (the written form also spells out defaults the file leaves out).
    /// </summary>
    private static void Contains(JsonNode? written, JsonNode? shipped, string where)
    {
        switch (shipped)
        {
            case JsonObject obj:
                var target = written.ShouldBeOfType<JsonObject>(where);
                foreach (var (name, value) in obj)
                {
                    target.ContainsKey(name).ShouldBeTrue($"{where}.{name} is dropped by the records");
                    Contains(target[name], value, $"{where}.{name}");
                }

                break;
            case JsonArray array:
                var items = written.ShouldBeOfType<JsonArray>(where);
                items.Count.ShouldBe(array.Count, where);
                for (var i = 0; i < array.Count; i++)
                {
                    Contains(items[i], array[i], $"{where}[{i}]");
                }

                break;
            default:
                JsonNode.DeepEquals(written, shipped).ShouldBeTrue(where);
                break;
        }
    }
}
