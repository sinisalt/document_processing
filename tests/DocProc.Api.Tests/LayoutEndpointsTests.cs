using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Domain.Fields;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using Microsoft.AspNetCore.Mvc;

namespace DocProc.Api.Tests;

/// <summary>
/// The layout API of P4 part B (docs/11 §18): the supplier's and a group's layouts with their versions, one version in
/// full, the identification rule test (validation, per-document evidence, overlaps with other layouts, tenant scope
/// without supplier values), the resolver dry run and the suggestions, the <c>supplierLayouts</c> switch, roles, tenant
/// isolation and the tenant's layout warm-up setting.
/// </summary>
public sealed class LayoutEndpointsTests : IClassFixture<ApiFactory>
{
    private const string Vat = "DE312456789";
    private const string ErpRule = """{ "all": [ { "text": "Rechnung", "page": 1, "region": "title" }, { "supplierValue": "vatId", "page": 1 } ] }""";
    private static readonly JsonSerializerOptions TextLayerJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, IgnoreReadOnlyProperties = true };

    private readonly ApiFactory _factory;

    public LayoutEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Run(db =>
        {
            var features = db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.Features!;
            features.SupplierLayouts = true;
            features.SourceRouting = true;
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Admin(Guid? tenant = null) => _factory.ClientFor(new TokenSpec("tenant-admin", [Roles.TenantAdmin], (tenant ?? ApiFactory.TenantA).ToString()));

    private HttpClient Reviewer() => _factory.ClientFor(new TokenSpec("tenant-a-reviewer", [Roles.Reviewer], ApiFactory.TenantA.ToString()));

    /// <summary>A page whose words are printed at the given lines (y in 1000 units, x 60, 6 units per character).</summary>
    private static string Layer(params (double Y, string Text)[] lines)
    {
        var words = new List<PositionedWord>();
        foreach (var (y, text) in lines)
        {
            var x = 60.0;
            foreach (var word in text.Split(' '))
            {
                words.Add(new PositionedWord(word, new BoundingBox(x, y, x + (word.Length * 6), y + 10)));
                x += (word.Length * 6) + 4;
            }
        }

        return JsonSerializer.Serialize(new TextLayer(TextLayerSource.PdfText, [new TextPage(1, 1000, 1000, words)]), TextLayerJson);
    }

    private static readonly string ErpPage = Layer((40, "Nordwind Großhandel GmbH USt-IdNr. DE 312 456 789"), (200, "Rechnung"), (240, "Rechnungs-Nr. NW-1"));
    private static readonly string WebShopPage = Layer((40, "RECHNUNG / INVOICE"), (900, "Nordwind Webshop"));

    private (Guid Supplier, Guid Layout, Guid Version) SeedLayout(string supplierName, Guid? tenant = null, string rule = ErpRule, SupplierLayoutVersionStatus status = SupplierLayoutVersionStatus.Active)
    {
        var tenantId = tenant ?? ApiFactory.TenantA;
        Guid supplierId = default, layoutId = Guid.CreateVersion7(), versionId = Guid.CreateVersion7();
        _factory.Run(db =>
        {
            var supplier = db.Suppliers.FirstOrDefault(s => s.TenantId == tenantId && s.Name == supplierName);
            if (supplier is null)
            {
                supplier = new Supplier { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = supplierName, NormalizedName = supplierName.ToUpperInvariant(), VatId = Vat, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                db.Suppliers.Add(supplier);
            }

            supplierId = supplier.Id;
            var parsed = IdentificationRule.Parse(rule);
            db.SupplierLayouts.Add(new SupplierLayout
            {
                Id = layoutId,
                TenantId = tenantId,
                Scope = SupplierLayoutScope.Supplier,
                SupplierId = supplier.Id,
                OwnerId = supplier.Id,
                DocumentTypeKey = "Invoice",
                LayoutKey = $"layout-{layoutId:N}"[..20],
                Name = "Nordwind ERP",
                ActiveVersion = status == SupplierLayoutVersionStatus.Active ? 1 : null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.SupplierLayoutVersions.Add(new SupplierLayoutVersion
            {
                Id = versionId,
                TenantId = tenantId,
                LayoutId = layoutId,
                Version = 1,
                Status = status,
                IdentificationJson = parsed.ToJson(),
                IdentificationHash = parsed.Hash,
                MappingJson = """{ "fields": { "invoiceNumber": { "rules": [ { "strategy": "label-right", "labels": ["Rechnungs-Nr."] } ] } } }""",
                ReviewWarmup = 5,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        });
        return (supplierId, layoutId, versionId);
    }

    private async Task<Guid> SeedDocumentAsync(string supplierName, string layer, Guid? identifiedBy = null)
    {
        var seeded = await _factory.SeedDocumentAsync(new DocumentSeed { SupplierName = supplierName, TextLayerJson = layer });
        if (identifiedBy is { } version)
        {
            _factory.Run(db => db.Documents.Single(d => d.Id == seeded.DocumentId).SupplierLayoutVersionId = version);
        }

        return seeded.DocumentId;
    }

    [Fact]
    public async Task Without_supplierLayouts_every_layout_route_is_forbidden()
    {
        using var b = Admin(ApiFactory.TenantB);
        var (supplier, layout, _) = SeedLayout($"Off {Guid.NewGuid():N}", ApiFactory.TenantB);
        foreach (var response in new[]
        {
            await b.GetAsync($"/api/v1/suppliers/{supplier}/layouts", Ct),
            await b.GetAsync($"/api/v1/supplier-layouts/{layout}", Ct),
            await b.PostAsJsonAsync("/api/v1/identification-rules:test", new IdentificationTestRequest(JsonDocument.Parse(ErpRule).RootElement, new IdentificationTestScope(SupplierId: supplier)), Ct),
            await b.PutAsJsonAsync("/api/v1/settings", new TenantSettingsDto(0.8, [], 10, true, LayoutReviewWarmup: 0), Ct),
        })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, response.RequestMessage!.RequestUri!.ToString());
            (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail!.ShouldContain("supplierLayouts");
        }
    }

    [Fact]
    public async Task A_suppliers_layouts_are_listed_with_their_versions_and_one_version_reads_in_full()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        var (supplier, layout, version) = SeedLayout(name);
        using var reviewer = Reviewer();

        var list = await reviewer.GetFromJsonAsync<List<SupplierLayoutResponse>>($"/api/v1/suppliers/{supplier}/layouts", ApiFactory.Json, Ct);
        var item = list.ShouldHaveSingleItem();
        item.Id.ShouldBe(layout);
        item.Scope.ShouldBe("Supplier");
        item.ActiveVersion.ShouldBe(1);
        var summary = item.Versions.ShouldHaveSingleItem();
        summary.Id.ShouldBe(version);
        summary.Status.ShouldBe("Active");
        summary.ReviewWarmup.ShouldBe(5);
        summary.Describe.ShouldContain("text \"Rechnung\" (page 1, title area)");
        (await reviewer.GetFromJsonAsync<List<SupplierLayoutResponse>>($"/api/v1/suppliers/{supplier}/layouts?type=CreditNote", ApiFactory.Json, Ct)).ShouldBeEmpty();

        var full = await reviewer.GetFromJsonAsync<SupplierLayoutVersionResponse>($"/api/v1/supplier-layouts/{layout}/versions/1", ApiFactory.Json, Ct);
        full!.Identification.GetProperty("all").GetArrayLength().ShouldBe(2);
        full.Coverage.ShouldBe(["invoiceNumber"]);
        (await reviewer.GetAsync($"/api/v1/supplier-layouts/{layout}/versions/9", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await reviewer.GetAsync($"/api/v1/suppliers/{Guid.NewGuid()}/layouts", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Another tenant's layout is not found.
        using var b = Admin(ApiFactory.TenantB);
        _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!.SupplierLayouts = true);
        try
        {
            (await b.GetAsync($"/api/v1/supplier-layouts/{layout}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!.SupplierLayouts = false);
        }
    }

    [Fact]
    public async Task The_rule_test_reports_per_document_evidence_and_the_overlaps_with_other_layouts()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        var (supplier, layout, version) = SeedLayout(name);
        var erp = await SeedDocumentAsync(name, ErpPage, identifiedBy: version);
        var web = await SeedDocumentAsync(name, WebShopPage);
        using var reviewer = Reviewer();

        // A broad rule ("Rechnung" anywhere) holds on both designs: it overlaps the ERP layout's document.
        var response = await reviewer.PostAsJsonAsync("/api/v1/identification-rules:test", new IdentificationTestRequest(JsonDocument.Parse("""{ "all": [ { "text": "Rechnung" } ] }""").RootElement, new IdentificationTestScope(SupplierId: supplier)), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var result = (await response.Content.ReadFromJsonAsync<IdentificationTestResponse>(ApiFactory.Json, Ct))!;
        result.Tested.ShouldBe(2);
        result.Matched.ShouldBe(2);
        result.Overlaps.ShouldHaveSingleItem().LayoutId.ShouldBe(layout);
        result.Overlaps[0].DocumentIds.ShouldBe([erp]);
        result.Hash.Length.ShouldBe(8);

        // The web shop's rule holds on its own document only, with evidence, and overlaps nothing.
        var webRule = """{ "all": [ { "text": "RECHNUNG / INVOICE", "page": 1, "region": "header" } ] }""";
        var precise = (await (await reviewer.PostAsJsonAsync("/api/v1/identification-rules:test", new IdentificationTestRequest(JsonDocument.Parse(webRule).RootElement, new IdentificationTestScope(SupplierId: supplier), DocumentIds: [erp, web]), Ct))
            .Content.ReadFromJsonAsync<IdentificationTestResponse>(ApiFactory.Json, Ct))!;
        precise.Matched.ShouldBe(1);
        precise.Overlaps.ShouldBeEmpty();
        var held = precise.Documents.Single(d => d.Matched);
        held.DocumentId.ShouldBe(web);
        held.Evidence.ShouldHaveSingleItem().Text.ShouldBe("RECHNUNG / INVOICE");
        held.Evidence[0].BBox.ShouldNotBeNull().Count.ShouldBe(4);
        precise.Documents.Single(d => !d.Matched).Failed.ShouldNotBeEmpty();

        // The supplier's VAT id is master data: it holds in supplier scope, and a tenant rule may not name it.
        var vat = (await (await reviewer.PostAsJsonAsync("/api/v1/identification-rules:test", new IdentificationTestRequest(JsonDocument.Parse(ErpRule).RootElement, new IdentificationTestScope(SupplierId: supplier)), Ct))
            .Content.ReadFromJsonAsync<IdentificationTestResponse>(ApiFactory.Json, Ct))!;
        vat.Documents.Single(d => d.DocumentId == erp).Matched.ShouldBeTrue();
        var tenantScope = await reviewer.PostAsJsonAsync("/api/v1/identification-rules:test", new IdentificationTestRequest(JsonDocument.Parse(ErpRule).RootElement, new IdentificationTestScope(Tenant: true)), Ct);
        tenantScope.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tenantScope.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldContain("rule.all[1]");
    }

    [Theory]
    [InlineData("""{ "none": [ { "text": "Gutschrift" } ] }""", "rule.(root)")]
    [InlineData("""{ "all": [ { "regex": "(a)\\1" } ] }""", "rule.all[0]")]
    [InlineData("""{ "all": [ { "text": "x", "colour": "red" } ] }""", "rule./all/0")]
    public async Task A_rule_that_does_not_pass_the_checks_at_save_is_refused_with_its_place(string rule, string key)
    {
        var (supplier, _, _) = SeedLayout($"Checks {Guid.NewGuid():N}");
        using var reviewer = Reviewer();
        var response = await reviewer.PostAsJsonAsync("/api/v1/identification-rules:test", new IdentificationTestRequest(JsonDocument.Parse(rule).RootElement, new IdentificationTestScope(SupplierId: supplier)), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!;
        problem.Errors.Keys.ShouldContain(k => k.StartsWith(key, StringComparison.Ordinal), string.Join(", ", problem.Errors.Keys));
        (await reviewer.PostAsJsonAsync("/api/v1/identification-rules:test", new IdentificationTestRequest(JsonDocument.Parse(ErpRule).RootElement, new IdentificationTestScope()), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a scope is required");
    }

    [Fact]
    public async Task The_resolver_dry_run_explains_every_candidate_and_suggests_layouts_for_a_new_design()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        var (supplier, layout, version) = SeedLayout(name);
        var erp = await SeedDocumentAsync(name, ErpPage);
        var web = await SeedDocumentAsync(name, WebShopPage);
        using var reviewer = Reviewer();

        var identified = await reviewer.GetFromJsonAsync<LayoutResolveResponse>($"/api/v1/suppliers/{supplier}/layouts:resolve?documentId={erp}", ApiFactory.Json, Ct);
        identified!.Resolution.ShouldBe("Identified");
        identified.Selected!.VersionId.ShouldBe(version);
        identified.Selected.Evidence.Count.ShouldBe(2);

        var newLayout = await reviewer.GetFromJsonAsync<LayoutResolveResponse>($"/api/v1/suppliers/{supplier}/layouts:resolve?documentId={web}", ApiFactory.Json, Ct);
        newLayout!.Resolution.ShouldBe("NewLayout");
        newLayout.LayoutManaged.ShouldBeTrue();
        newLayout.Evaluated.ShouldHaveSingleItem().Holds.ShouldBeFalse();
        newLayout.Suggestions.ShouldHaveSingleItem().LayoutId.ShouldBe(layout);

        var suggestions = await reviewer.GetFromJsonAsync<List<LayoutSuggestionDto>>($"/api/v1/documents/{web}/layout-suggestions", ApiFactory.Json, Ct);
        suggestions.ShouldHaveSingleItem().LayoutId.ShouldBe(layout);
        (await reviewer.GetAsync($"/api/v1/suppliers/{supplier}/layouts:resolve", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_layout_warm_up_is_5_by_default_and_a_tenant_admin_can_set_it()
    {
        using var admin = Admin();
        var settings = await admin.GetFromJsonAsync<TenantSettingsResponse>("/api/v1/settings", ApiFactory.Json, Ct);
        var current = settings!.Settings;
        _factory.Query(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.LayoutReviewWarmup).ShouldBeNull();
        current.LayoutReviewWarmup.ShouldBe(TenantSettings.DefaultLayoutReviewWarmup, "decision 2 of docs/11 §24");

        try
        {
            var set = await admin.PutAsJsonAsync("/api/v1/settings", current with { LayoutReviewWarmup = 0 }, Ct);
            set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
            (await set.Content.ReadFromJsonAsync<TenantSettingsResponse>(ApiFactory.Json, Ct))!.Settings.LayoutReviewWarmup.ShouldBe(0);
            _factory.Query(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.LayoutReviewWarmup).ShouldBe(0);
            (await admin.PutAsJsonAsync("/api/v1/settings", current with { LayoutReviewWarmup = 101 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            using var reviewer = Reviewer();
            (await reviewer.PutAsJsonAsync("/api/v1/settings", current with { LayoutReviewWarmup = 3 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.LayoutReviewWarmup = null);
        }
    }

    [Fact]
    public async Task A_config_admin_adds_a_layout_as_a_Candidate_and_activates_it_with_the_tenants_warm_up_after_the_identification_test()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        var (supplier, erpLayout, erpVersion) = SeedLayout(name);
        var approved = await SeedDocumentAsync(name, WebShopPage, identifiedBy: erpVersion);
        _factory.Run(db => db.Documents.Single(d => d.Id == approved).Status = Domain.Documents.DocumentStatus.Approved);
        using var admin = Admin();
        var webRule = JsonDocument.Parse("""{ "all": [ { "text": "RECHNUNG / INVOICE", "page": 1, "region": "header" } ] }""").RootElement;
        var mapping = JsonDocument.Parse("""{ "fields": { "invoiceNumber": { "rules": [ { "strategy": "regex-anywhere", "pattern": "WS-\\d+" } ] } } }""").RootElement;
        var url = $"/api/v1/suppliers/{supplier}/layouts";

        using (var reviewer = Reviewer())
        {
            (await reviewer.PostAsJsonAsync(url, new CreateLayoutRequest("nordwind-invoice-webshop", "Web shop", "Invoice", webRule, mapping), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var invalid = await admin.PostAsJsonAsync(url, new CreateLayoutRequest("Not A Key", "x", "Invoice", JsonDocument.Parse("""{ "none": [ { "text": "x" } ] }""").RootElement, JsonDocument.Parse("""{ "fields": { "a": { "rules": [ { "strategy": "guess" } ] } } }""").RootElement), Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldBe(["layoutKey", "identification.(root)", "mapping"], ignoreOrder: true);

        var created = await admin.PostAsJsonAsync(url, new CreateLayoutRequest("nordwind-invoice-webshop", "Web shop", "Invoice", webRule, mapping, Priority: 120, ItemKinds: ["pdf"]), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var layout = (await created.Content.ReadFromJsonAsync<SupplierLayoutResponse>(ApiFactory.Json, Ct))!;
        layout.ActiveVersion.ShouldBeNull();
        layout.Versions.ShouldHaveSingleItem().Status.ShouldBe("Candidate");
        (await admin.PostAsJsonAsync(url, new CreateLayoutRequest("nordwind-invoice-webshop", "Again", "Invoice", webRule, mapping), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Its rule holds on an approved document the ERP layout identified: the identification test blocks activation.
        var overlap = await admin.PostAsJsonAsync($"/api/v1/supplier-layouts/{layout.Id}/versions/1:activate", new ActivateLayoutRequest(), Ct);
        overlap.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await overlap.Content.ReadAsStringAsync(Ct)).ShouldContain(approved.ToString());

        // Once that document is the ERP layout's no more, it is the version's replay set (its rule holds on it) and the
        // field replay runs over it: the mapping's pattern is printed nowhere on it, so the field agreement fails.
        _factory.Run(db => db.Documents.Single(d => d.Id == approved).SupplierLayoutVersionId = null);
        var replayFailed = await admin.PostAsJsonAsync($"/api/v1/supplier-layouts/{layout.Id}/versions/1:activate", new ActivateLayoutRequest(), Ct);
        replayFailed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var refusal = (await replayFailed.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!;
        refusal.Extensions["code"]!.ToString().ShouldBe("LAYOUT_REPLAY_FAILED");
        refusal.Detail!.ShouldContain("invoiceNumber");

        // A reviewer without the MappingAuthor grant may not activate; with it, a reviewer may. The grant alone, or with
        // Viewer, gives no access (it adds to a role that may review, docs/05 "Roles").
        using (var reviewer = Reviewer())
        {
            (await reviewer.PostAsJsonAsync($"/api/v1/supplier-layouts/{layout.Id}/versions/1:activate", new ActivateLayoutRequest(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        foreach (var roles in new[] { new[] { Roles.MappingAuthor }, [Roles.Viewer, Roles.MappingAuthor] })
        {
            using var grantOnly = _factory.ClientFor(new TokenSpec("grant-only", roles, ApiFactory.TenantA.ToString()));
            (await grantOnly.PostAsJsonAsync($"/api/v1/supplier-layouts/{layout.Id}/versions/1:activate", new ActivateLayoutRequest(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, string.Join("+", roles));
            (await grantOnly.PostAsJsonAsync($"/api/v1/supplier-layouts/{Guid.NewGuid()}:retire", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, string.Join("+", roles));
        }

        // A new version whose mapping reads the document activates with the tenant's warm-up, and the old one is retired.
        var fixedMapping = JsonDocument.Parse("""{ "fields": { "currency": { "rules": [ { "strategy": "constant", "value": "EUR" } ] } } }""").RootElement;
        var added = await admin.PostAsJsonAsync($"/api/v1/supplier-layouts/{layout.Id}/versions", new CreateLayoutVersionRequest(webRule, fixedMapping), Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync(Ct));
        using var author = _factory.ClientFor(new TokenSpec("tenant-a-author", [Roles.Reviewer, Roles.MappingAuthor], ApiFactory.TenantA.ToString()));
        var activated = await author.PostAsJsonAsync($"/api/v1/supplier-layouts/{layout.Id}/versions/2:activate", new ActivateLayoutRequest(), Ct);
        activated.StatusCode.ShouldBe(HttpStatusCode.OK, await activated.Content.ReadAsStringAsync(Ct));
        var outcome = (await activated.Content.ReadFromJsonAsync<ActivateLayoutResponse>(ApiFactory.Json, Ct))!;
        outcome.Replay.Passed.ShouldBeTrue();
        outcome.Replay.Documents.ShouldHaveSingleItem().DocumentId.ShouldBe(approved);
        var active = outcome.Layout;
        active.ActiveVersion.ShouldBe(2);
        var version = active.Versions.First(v => v.Version == 2);
        version.Status.ShouldBe("Active");
        version.ReviewWarmup.ShouldBe(TenantSettings.DefaultLayoutReviewWarmup);
        version.ApprovedBy.ShouldBe("tenant-a-author");
        (await admin.PostAsJsonAsync($"/api/v1/supplier-layouts/{layout.Id}/versions/2:activate", new ActivateLayoutRequest(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "already Active");

        var retired = await admin.PostAsJsonAsync($"/api/v1/supplier-layouts/{erpLayout}:retire", new { }, Ct);
        (await retired.Content.ReadFromJsonAsync<SupplierLayoutResponse>(ApiFactory.Json, Ct))!.Status.ShouldBe("Retired");
    }

    [Fact]
    public async Task Activation_takes_an_explicit_warm_up_and_otherwise_the_tenants()
    {
        var name = $"Warmup {Guid.NewGuid():N}";
        var (supplier, _, _) = SeedLayout(name);
        // The replay set: an approved credit note of the supplier on which the rule holds.
        await _factory.SeedDocumentAsync(new DocumentSeed { SupplierName = name, TextLayerJson = Layer((200, "Gutschrift")), Type = Domain.Documents.DocumentTypeKey.CreditNote, Status = Domain.Documents.DocumentStatus.Approved });
        _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.LayoutReviewWarmup = 0);
        try
        {
            using var admin = Admin();
            var rule = JsonDocument.Parse("""{ "all": [ { "text": "Gutschrift" } ] }""").RootElement;
            var mapping = JsonDocument.Parse("""{ "fields": {} }""").RootElement;
            var first = (await (await admin.PostAsJsonAsync($"/api/v1/suppliers/{supplier}/layouts", new CreateLayoutRequest("credit-a", "A", "CreditNote", rule, mapping), Ct)).Content.ReadFromJsonAsync<SupplierLayoutResponse>(ApiFactory.Json, Ct))!;
            var tenantDefault = (await (await admin.PostAsJsonAsync($"/api/v1/supplier-layouts/{first.Id}/versions/1:activate", new ActivateLayoutRequest(), Ct)).Content.ReadFromJsonAsync<ActivateLayoutResponse>(ApiFactory.Json, Ct))!.Layout;
            tenantDefault.Versions[0].ReviewWarmup.ShouldBe(0, "the demo tenant's warm-up 0 (docs/11 §23 P4)");

            var second = (await (await admin.PostAsJsonAsync($"/api/v1/suppliers/{supplier}/layouts", new CreateLayoutRequest("credit-b", "B", "CreditNote", rule, mapping), Ct)).Content.ReadFromJsonAsync<SupplierLayoutResponse>(ApiFactory.Json, Ct))!;
            var explicitly = (await (await admin.PostAsJsonAsync($"/api/v1/supplier-layouts/{second.Id}/versions/1:activate", new ActivateLayoutRequest(ReviewWarmup: 3), Ct)).Content.ReadFromJsonAsync<ActivateLayoutResponse>(ApiFactory.Json, Ct))!.Layout;
            explicitly.Versions[0].ReviewWarmup.ShouldBe(3);
        }
        finally
        {
            _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.LayoutReviewWarmup = null);
        }
    }

    [Fact]
    public async Task Only_a_tenant_admin_activates_a_version_with_a_warm_up_below_the_tenants()
    {
        // Decision 2 of docs/11 §24: the first 5 documents under every new version go to review, and only a tenant admin
        // lowers that (the settings are 403 for anyone else), so an author's activation cannot lower it either.
        var name = $"Warmup {Guid.NewGuid():N}";
        var (supplier, _, _) = SeedLayout(name);
        await _factory.SeedDocumentAsync(new DocumentSeed { SupplierName = name, TextLayerJson = Layer((200, "Gutschrift")), Type = Domain.Documents.DocumentTypeKey.CreditNote, Status = Domain.Documents.DocumentStatus.Approved });
        using var admin = Admin();
        using var author = _factory.ClientFor(new TokenSpec("tenant-a-author", [Roles.Reviewer, Roles.MappingAuthor], ApiFactory.TenantA.ToString()));
        var rule = JsonDocument.Parse("""{ "all": [ { "text": "Gutschrift" } ] }""").RootElement;
        var mapping = JsonDocument.Parse("""{ "fields": {} }""").RootElement;
        async Task<SupplierLayoutResponse> AddAsync(string key) =>
            (await (await admin.PostAsJsonAsync($"/api/v1/suppliers/{supplier}/layouts", new CreateLayoutRequest(key, key, "CreditNote", rule, mapping), Ct)).Content.ReadFromJsonAsync<SupplierLayoutResponse>(ApiFactory.Json, Ct))!;

        var lowered = await AddAsync("credit-low");
        var refused = await author.PostAsJsonAsync($"/api/v1/supplier-layouts/{lowered.Id}/versions/1:activate", new ActivateLayoutRequest(ReviewWarmup: 0), Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("LAYOUT_WARMUP_BELOW_TENANT");
        _factory.Query(db => db.SupplierLayoutVersions.Single(v => v.LayoutId == lowered.Id).Status).ShouldBe(SupplierLayoutVersionStatus.Candidate, "nothing was activated");
        (await author.PostAsJsonAsync($"/api/v1/supplier-layouts/{lowered.Id}/versions/1:activate", new ActivateLayoutRequest(ReviewWarmup: TenantSettings.DefaultLayoutReviewWarmup - 1), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The tenant's own warm-up, or more, is the author's to choose.
        var same = await author.PostAsJsonAsync($"/api/v1/supplier-layouts/{lowered.Id}/versions/1:activate", new ActivateLayoutRequest(ReviewWarmup: TenantSettings.DefaultLayoutReviewWarmup), Ct);
        same.StatusCode.ShouldBe(HttpStatusCode.OK, await same.Content.ReadAsStringAsync(Ct));
        var raised = await AddAsync("credit-high");
        var higher = await author.PostAsJsonAsync($"/api/v1/supplier-layouts/{raised.Id}/versions/1:activate", new ActivateLayoutRequest(ReviewWarmup: 8), Ct);
        higher.StatusCode.ShouldBe(HttpStatusCode.OK, await higher.Content.ReadAsStringAsync(Ct));
        (await higher.Content.ReadFromJsonAsync<ActivateLayoutResponse>(ApiFactory.Json, Ct))!.Layout.Versions[0].ReviewWarmup.ShouldBe(8);

        // A tenant admin may lower it for one version.
        var byAdmin = await AddAsync("credit-admin");
        var adminActivated = await admin.PostAsJsonAsync($"/api/v1/supplier-layouts/{byAdmin.Id}/versions/1:activate", new ActivateLayoutRequest(ReviewWarmup: 0), Ct);
        adminActivated.StatusCode.ShouldBe(HttpStatusCode.OK, await adminActivated.Content.ReadAsStringAsync(Ct));
        (await adminActivated.Content.ReadFromJsonAsync<ActivateLayoutResponse>(ApiFactory.Json, Ct))!.Layout.Versions[0].ReviewWarmup.ShouldBe(0);
    }

    [Fact]
    public async Task The_document_detail_names_the_layout_version_and_the_type_source()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        var (_, _, version) = SeedLayout(name);
        var erp = await SeedDocumentAsync(name, ErpPage, identifiedBy: version);
        _factory.Run(db => db.Documents.Single(d => d.Id == erp).TypeSource = Domain.Documents.TypeSource.Identification);
        using var reviewer = Reviewer();
        var detail = await reviewer.GetFromJsonAsync<DocumentDetail>($"/api/v1/documents/{erp}", ApiFactory.Json, Ct);
        detail!.SupplierLayoutVersionId.ShouldBe(version);
        detail.TypeSource.ShouldBe(Domain.Documents.TypeSource.Identification);
    }
}
