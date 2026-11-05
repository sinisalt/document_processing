using DocProc.Mapping.Identification;

namespace DocProc.Mapping.Tests;

/// <summary>
/// <see cref="LayoutResolver"/> (docs/11 §8.4): one layout holds, priority then specificity, a tie is ambiguous, a
/// layout-managed supplier with no layout holding gets a new layout (with a pending Candidate), group layouts only
/// without own ones, item kinds, receiving entities, a fixed type, e-invoices exempt; and <see cref="LayoutSuggester"/>.
/// </summary>
public sealed class LayoutResolverTests
{
    private static readonly SupplierValues Nordwind = new(VatId: "DE811111111");

    private static readonly DocumentWords ErpInvoice = IdentificationRuleTests.Words(
        new PageBuilder(1).Text(60, 40, "Nordwind Großhandel GmbH USt-IdNr. DE811111111").Text(60, 200, "Rechnung").Text(60, 230, "Rechnungs-Nr. RE-1"));

    private static readonly DocumentWords WebShopInvoice = IdentificationRuleTests.Words(
        new PageBuilder(1).Text(600, 40, "RECHNUNG / INVOICE").Text(60, 400, "Bestellung Webshop 4711"),
        new PageBuilder(2).Text(60, 900, "Nordwind Großhandel GmbH DE 811 111 111"));

    private static LayoutCandidate Layout(string key, string rule, LayoutCandidateState state = LayoutCandidateState.Active, int priority = 100, LayoutScope scope = LayoutScope.Supplier, string type = "Invoice", IReadOnlyList<string>? kinds = null, Guid? entity = null) =>
        new(Guid.NewGuid(), key, Guid.NewGuid(), 1, type, scope, state, IdentificationRule.Parse(rule), priority, kinds, entity);

    private const string ErpRule = """{ "all": [ { "text": "Rechnung", "page": 1, "region": "title" }, { "supplierValue": "vatId", "page": 1 } ] }""";
    private const string WebShopRule = """{ "all": [ { "text": "RECHNUNG / INVOICE", "page": 1, "region": "header" }, { "supplierValue": "vatId", "page": "last" } ] }""";

    private static LayoutResolverResult Resolve(DocumentWords words, IReadOnlyList<LayoutCandidate> candidates, string itemKind = "pdf", string? fixedType = null, bool required = false, Guid? entity = null) =>
        LayoutResolver.Instance.Resolve(new LayoutResolverInput(new IdentificationContext(words, Nordwind), candidates, itemKind, fixedType, required, entity));

    [Fact]
    public void Exactly_one_layout_holding_is_identified_and_a_new_design_is_a_new_layout_not_the_nearest()
    {
        var erp = Layout("nordwind-invoice-erp", ErpRule);
        var result = Resolve(ErpInvoice, [erp]);
        result.Resolution.ShouldBe(LayoutResolution.Identified);
        result.Selected!.Candidate.ShouldBe(erp);
        result.Selected.Result.Evidence.Count.ShouldBe(2);

        var web = Resolve(WebShopInvoice, [erp]);
        web.Resolution.ShouldBe(LayoutResolution.NewLayout, "the supplier has an Active layout, so it is layout-managed, and no rule holds");
        web.Selected.ShouldBeNull();
        web.LayoutManaged.ShouldBeTrue();
    }

    [Fact]
    public void Several_layouts_holding_are_ordered_by_priority_then_by_positive_identifiers_and_a_tie_is_ambiguous()
    {
        const string broad = """{ "all": [ { "text": "Rechnung" } ] }""";
        var specific = Layout("specific", ErpRule);
        var general = Layout("general", broad);
        Resolve(ErpInvoice, [general, specific]).Selected!.Candidate.ShouldBe(specific, "two positive identifiers beat one");

        var preferred = Layout("preferred", broad, priority: 200);
        Resolve(ErpInvoice, [specific, preferred]).Selected!.Candidate.ShouldBe(preferred, "priority first");

        var twin = Layout("twin", """{ "all": [ { "text": "Rechnung" }, { "text": "Nordwind" } ] }""");
        var ambiguous = Resolve(ErpInvoice, [specific, twin]);
        ambiguous.Resolution.ShouldBe(LayoutResolution.Ambiguous);
        ambiguous.Selected.ShouldBeNull();
        ambiguous.Tied.Select(t => t.Candidate.LayoutKey).Order(StringComparer.Ordinal).ShouldBe(["specific", "twin"]);
    }

    /// <summary>A pattern that cannot finish on <see cref="Stalled"/> within 1 ms: a stand-in for a stalled worker.</summary>
    internal const string StallingPattern = @"(a+\s?)+$";

    private static readonly string StallingJson = System.Text.Json.JsonSerializer.Serialize(StallingPattern);

    internal static readonly DocumentWords Stalled = IdentificationRuleTests.Words(Enumerable.Range(0, 10).Aggregate(
        new PageBuilder(1).Text(60, 40, "Nordwind Großhandel GmbH USt-IdNr. DE811111111").Text(60, 200, "Rechnung").Text(60, 230, "Rechnungs-Nr. RE-1"),
        (page, i) => page.Text(60, 300 + (i * 20), "aaaaaaaa aaaaaaaa aaaaaaaa")).Text(60, 520, "!"));

    internal static void StallPattern() =>
        IdentificationRuleEvaluator.UseForTests(StallingPattern, new System.Text.RegularExpressions.Regex(StallingPattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(1)));

    [Fact]
    public void A_layout_that_could_not_be_checked_is_not_outranked_by_one_that_held()
    {
        StallPattern();
        var stalled = $$"""{ "all": [ { "text": "Rechnung" }, { "regex": {{StallingJson}} } ] }""";
        var broad = Layout("broad", """{ "all": [ { "text": "Rechnung" } ] }""");
        var timedOut = Layout("timed-out", stalled, priority: 200);

        var result = Resolve(Stalled, [broad, timedOut]);
        var timedOutResult = result.Evaluated.Single(e => e.Candidate == timedOut).Result;
        timedOutResult.TimedOut.ShouldBeTrue(string.Join(" | ", timedOutResult.Failed));
        result.Resolution.ShouldBe(LayoutResolution.Ambiguous, "the higher-priority layout might have held");
        result.Selected.ShouldBeNull();
        result.Tied.Select(t => t.Candidate.LayoutKey).Order(StringComparer.Ordinal).ShouldBe(["broad", "timed-out"]);

        var lower = Layout("lower", stalled, priority: 50);
        var decided = Resolve(Stalled, [broad, lower]);
        decided.Resolution.ShouldBe(LayoutResolution.Identified, "a layout that would rank lower cannot change the winner");
        decided.Selected!.Candidate.ShouldBe(broad);
    }

    [Fact]
    public void A_tenant_rule_of_another_type_that_could_not_be_checked_leaves_the_type_undecided()
    {
        StallPattern();
        TenantRuleCandidate Rule(string type, string rule) => new(Guid.NewGuid(), Guid.NewGuid(), 1, type, type, 100, IdentificationRule.Parse(rule), []);
        var holds = Rule("invoice", """{ "all": [ { "text": "Rechnung" } ] }""");
        var timedOut = Rule("price-list", $$"""{ "all": [ { "regex": {{StallingJson}} } ] }""");

        var result = TypeIdentificationRuleSet.Resolve([holds, timedOut], Stalled, null, null);
        result.Resolution.ShouldBe(TenantRuleResolution.Conflicting, "the price-list rule might have held");
        result.Selected.ShouldBeNull();

        var sameType = Rule("invoice", $$"""{ "all": [ { "regex": {{StallingJson}} } ] }""");
        var same = TypeIdentificationRuleSet.Resolve([holds, sameType], Stalled, null, null);
        same.Resolution.ShouldBe(TenantRuleResolution.Identified, "a rule of the same type cannot change the type");
        same.Selected!.Candidate.ShouldBe(holds);
    }

    [Fact]
    public void A_Candidate_is_never_applied_but_named_as_pending_and_a_Shadow_only_counts()
    {
        var erp = Layout("nordwind-invoice-erp", ErpRule);
        var pending = Layout("nordwind-invoice-webshop", WebShopRule, LayoutCandidateState.Candidate);
        var shadow = Layout("shadow", WebShopRule, LayoutCandidateState.Shadow);
        var result = Resolve(WebShopInvoice, [erp, pending, shadow]);
        result.Resolution.ShouldBe(LayoutResolution.NewLayout);
        result.Pending!.Candidate.ShouldBe(pending);
        result.ShadowHits.Single().Candidate.ShouldBe(shadow);

        var onlyCandidate = Resolve(WebShopInvoice, [pending]);
        onlyCandidate.Resolution.ShouldBe(LayoutResolution.NotManaged, "a Candidate alone does not make the supplier layout-managed");
        onlyCandidate.Pending.ShouldNotBeNull();
    }

    [Fact]
    public void Layouts_required_makes_a_supplier_without_layouts_layout_managed()
    {
        Resolve(ErpInvoice, []).Resolution.ShouldBe(LayoutResolution.NotManaged);
        Resolve(ErpInvoice, [], required: true).Resolution.ShouldBe(LayoutResolution.NewLayout);
    }

    [Fact]
    public void Group_layouts_apply_only_while_the_supplier_has_no_Active_layout_of_its_own()
    {
        var group = Layout("group-invoice", """{ "all": [ { "text": "Rechnung" } ] }""", scope: LayoutScope.Group);
        Resolve(ErpInvoice, [group]).Selected!.Candidate.ShouldBe(group);

        var own = Layout("own-webshop", WebShopRule);
        var result = Resolve(ErpInvoice, [group, own]);
        result.Resolution.ShouldBe(LayoutResolution.NewLayout, "the supplier's own rules win, even when the group's would hold");
        result.Evaluated.ShouldNotContain(e => e.Candidate == group);
    }

    [Fact]
    public void Item_kinds_receiving_entities_and_a_fixed_type_filter_the_candidates()
    {
        var images = Layout("scans", ErpRule, kinds: ["image"]);
        Resolve(ErpInvoice, [images]).Resolution.ShouldBe(LayoutResolution.NewLayout);
        Resolve(ErpInvoice, [images], itemKind: "image").Resolution.ShouldBe(LayoutResolution.Identified);

        var entity = Guid.NewGuid();
        var forEntity = Layout("company-2000", ErpRule, entity: entity);
        Resolve(ErpInvoice, [forEntity]).Resolution.ShouldBe(LayoutResolution.NewLayout);
        Resolve(ErpInvoice, [forEntity], entity: entity).Resolution.ShouldBe(LayoutResolution.Identified);

        var credit = Layout("credit", """{ "all": [ { "text": "Rechnung" } ] }""", type: "CreditNote");
        Resolve(ErpInvoice, [credit], fixedType: "Invoice").Resolution.ShouldBe(LayoutResolution.NewLayout, "only the fixed type's layouts are searched");
        Resolve(ErpInvoice, [credit]).Selected!.Candidate.DocumentTypeKey.ShouldBe("CreditNote", "the layout decides the type when none is fixed");
    }

    [Fact]
    public void An_e_invoice_is_never_a_new_layout()
    {
        var erp = Layout("nordwind-invoice-erp", ErpRule);
        Resolve(WebShopInvoice, [erp], itemKind: LayoutResolver.EInvoiceItemKind).Resolution.ShouldBe(LayoutResolution.Exempt);
        var xmlOnly = Layout("extras", """{ "all": [ { "text": "Nordwind" } ] }""", kinds: [LayoutResolver.EInvoiceItemKind]);
        Resolve(WebShopInvoice, [xmlOnly], itemKind: LayoutResolver.EInvoiceItemKind).Resolution.ShouldBe(LayoutResolution.Identified);
    }

    [Fact]
    public void The_suggester_ranks_existing_layouts_by_page_one_words_and_says_what_failed()
    {
        var erp = Layout("nordwind-invoice-erp", ErpRule);
        var priceList = Layout("nordwind-price-list", """{ "all": [ { "text": "Preisliste" } ] }""", type: "price-list");
        var result = Resolve(WebShopInvoice, [erp, priceList]);
        var profiles = new Dictionary<Guid, SimilarityProfile?>
        {
            [erp.VersionId] = SimilarityProfile.Of(ErpInvoice),
            [priceList.VersionId] = new SimilarityProfile(["PREISLISTE", "ARTIKEL"]),
        };

        var suggestions = LayoutSuggester.Suggest(SimilarityProfile.Of(WebShopInvoice), result.Evaluated.Select(e => (e, profiles[e.Candidate.VersionId])));
        suggestions.Count.ShouldBe(1, "the price list shares no word with the document");
        suggestions[0].LayoutKey.ShouldBe("nordwind-invoice-erp");
        suggestions[0].Similarity.ShouldBeGreaterThan(0);
        suggestions[0].Differences.ShouldContain(d => d.Contains("VAT id", StringComparison.Ordinal), "the title still reads Rechnung; the VAT id is not on page 1");
    }

    [Fact]
    public void A_profile_round_trips_through_its_json()
    {
        var profile = SimilarityProfile.Of(ErpInvoice, "pdf");
        SimilarityProfile.Parse(profile.ToJson()).ShouldBe(profile with { }, new ProfileComparer());
        SimilarityProfile.Parse("not json").ShouldBeNull();
    }

    private sealed class ProfileComparer : IEqualityComparer<SimilarityProfile?>
    {
        public bool Equals(SimilarityProfile? x, SimilarityProfile? y) =>
            x is not null && y is not null && x.Tokens.SequenceEqual(y.Tokens) && x.PageCount == y.PageCount && x.ItemKind == y.ItemKind;

        public int GetHashCode(SimilarityProfile? obj) => obj?.Tokens.Count ?? 0;
    }
}
