using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Mvc;

namespace DocProc.Api.Tests;

/// <summary>
/// The routes P5 part D added for the screens of docs/11 §19: <c>GET /rule-kinds</c> (the rule editor's forms), the
/// type editor's rule test against a document (<c>:test-rules</c>, nothing written), the processing UI's test call of an
/// <c>http-lookup</c> (<c>POST /pipelines/lookups:test</c>: the save-time checks, the SSRF guard, nothing stored) and
/// Insights &gt; Onboarding (<c>GET /insights/automation</c>, docs/11 §17).
/// </summary>
public sealed class ProcessingUiEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid Tenant(string name, bool configurableTypes = true, bool configurableSteps = false)
    {
        var tenant = factory.CreateTenant(name);
        factory.Run(db =>
        {
            var features = db.Tenants.Single(t => t.Id == tenant).Settings.Features!;
            features.ConfigurableTypes = configurableTypes;
            features.ConfigurableSteps = configurableSteps;
        });
        using var db = factory.PlatformDb();
        TypeCatalogueSeeder.AddMissingAdoptedTypesAsync(db, tenant, factory.Clock.GetUtcNow(), Ct).GetAwaiter().GetResult();
        db.SaveChanges();
        return tenant;
    }

    private HttpClient Reviewer(Guid tenant) => factory.ClientFor(new TokenSpec("rev-" + tenant.ToString("N")[..6], [Roles.Reviewer], tenant.ToString()));

    [Fact]
    public async Task Rule_kinds_list_every_kind_with_its_schema_and_expression_parameters()
    {
        var tenant = Tenant("Rule kinds");
        using var reviewer = Reviewer(tenant);

        var kinds = (await reviewer.GetFromJsonAsync<List<RuleKindResponse>>("/api/v1/rule-kinds", ApiFactory.Json, Ct))!;

        kinds.Select(k => k.Kind).ShouldBe(DocProc.Definitions.RuleKinds.Keys, ignoreOrder: true);
        var expression = kinds.Single(k => k.Kind == "expression");
        expression.Phase.ShouldBe("P5");
        expression.ExpressionParams.ShouldBe(["predicate"]);
        expression.ParamsSchema.GetProperty("properties").TryGetProperty("predicate", out _).ShouldBeTrue();
        kinds.Single(k => k.Kind == "deltaToPrevious").PathParams.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_rule_test_reports_each_rule_on_a_document_and_writes_nothing()
    {
        var tenant = Tenant("Rule test");
        var clean = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant });
        var wrong = TestInvoices.Valid() with { TotalGross = FieldValue.Extracted(TestInvoices.Eur(999m), new FieldSource(SourceMethod.Llm), 0.99) };
        var mismatched = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Invoice = wrong });
        using var admin = factory.TenantAdminOf(tenant);

        var passed = await TestAsync(admin, "Invoice", 1, clean.DocumentId);
        passed.Rules.ShouldNotBeEmpty();
        passed.Rules.Single(r => r.Kind == "invoice.totals").Outcome.ShouldBe("passed");

        var failed = await TestAsync(admin, "Invoice", 1, mismatched.DocumentId);
        var totals = failed.Rules.Single(r => r.Kind == "invoice.totals");
        totals.Outcome.ShouldNotBe("passed");
        totals.Flags.ShouldContain(f => f.Code == FlagCodes.TotalsMismatch);
        failed.Flags.ShouldContain(f => f.Code == FlagCodes.TotalsMismatch);
        factory.Query(db => db.DocumentFlags.Count(f => f.DocumentId == mismatched.DocumentId)).ShouldBe(0, "a test changes nothing on the document");

        // A document of another type, a version that does not exist, another tenant's document and a reviewer are refused.
        var note = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Type = DocumentTypeKey.DeliveryNote, Invoice = null });
        (await admin.PostAsJsonAsync($"/api/v1/document-types/Invoice/versions/1:test-rules", new { documentId = note.DocumentId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/v1/document-types/Invoice/versions/9:test-rules", new { documentId = clean.DocumentId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var stranger = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = Tenant("Rule test stranger") });
        (await admin.PostAsJsonAsync($"/api/v1/document-types/Invoice/versions/1:test-rules", new { documentId = stranger.DocumentId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var reviewer = Reviewer(tenant);
        (await reviewer.PostAsJsonAsync($"/api/v1/document-types/Invoice/versions/1:test-rules", new { documentId = clean.DocumentId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var off = Tenant("Rule test off", configurableTypes: false);
        var offDocument = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = off });
        using var offAdmin = factory.TenantAdminOf(off);
        (await offAdmin.PostAsJsonAsync($"/api/v1/document-types/Invoice/versions/1:test-rules", new { documentId = offDocument.DocumentId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<RuleTestResponse> TestAsync(HttpClient client, string key, int version, Guid documentId)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/document-types/{key}/versions/{version}:test-rules", new { documentId }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RuleTestResponse>(ApiFactory.Json, Ct))!;
    }

    [Fact]
    public async Task A_lookup_test_call_is_checked_like_an_override_guarded_and_stores_nothing()
    {
        var tenant = Tenant("Lookup test", configurableSteps: true);
        var document = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant });
        var destination = Guid.CreateVersion7();
        factory.Run(db => db.OutputDestinations.Add(new OutputDestination
        {
            Id = destination,
            TenantId = tenant,
            Name = "erp-vendors",
            CreatedBy = "test",
            Transport = OutputTransports.Http,
            ConfigJson = """{"urlTemplate":"https://127.0.0.1/vendors?vat={{output.vat}}"}""",
            CreatedAt = factory.Clock.GetUtcNow(),
            UpdatedAt = factory.Clock.GetUtcNow(),
        }));
        using var admin = factory.TenantAdminOf(tenant);

        // The save-time checks of an override: the parameters against the kind's schema, the destination, the expressions.
        var invalid = await admin.PostAsJsonAsync("/api/v1/pipelines/lookups:test", new { documentId = document.DocumentId, @params = new { destinationId = Guid.NewGuid(), response = "{ \"a\": " } }, Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await invalid.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors;
        errors.Keys.ShouldContain("params.destinationId");
        errors.Keys.ShouldContain("params.response");
        (await admin.PostAsJsonAsync("/api/v1/pipelines/lookups:test", new { documentId = document.DocumentId, @params = new { response = "{}" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "destinationId is required");

        // A loopback address is refused by the guard before any request: an outcome, not an error.
        var response = await admin.PostAsJsonAsync("/api/v1/pipelines/lookups:test", new { documentId = document.DocumentId, @params = new { destinationId = destination, request = "{ \"vat\": \"DE811177936\" }", response = "{ \"ext.vendorId\": response.id }" } }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var outcome = (await response.Content.ReadFromJsonAsync<LookupTestResponse>(ApiFactory.Json, Ct))!;
        outcome.Succeeded.ShouldBeFalse();
        outcome.Reason.ShouldBe("blocked", outcome.Message);
        factory.Query(db => db.LookupResults.Count(r => r.DocumentId == document.DocumentId)).ShouldBe(0, "a test call stores nothing");

        (await admin.PostAsJsonAsync("/api/v1/pipelines/lookups:test", new { documentId = Guid.NewGuid(), @params = new { destinationId = destination, response = "{}" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var reviewer = Reviewer(tenant);
        (await reviewer.PostAsJsonAsync("/api/v1/pipelines/lookups:test", new { documentId = document.DocumentId, @params = new { destinationId = destination, response = "{}" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var off = Tenant("Lookup test off");
        using var offAdmin = factory.TenantAdminOf(off);
        (await offAdmin.PostAsJsonAsync("/api/v1/pipelines/lookups:test", new { documentId = document.DocumentId, @params = new { destinationId = destination, response = "{}" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "configurableSteps is off");
    }

    [Fact]
    public async Task Insights_rank_suppliers_without_layouts_by_volume_and_list_what_needs_attention()
    {
        var tenant = Tenant("Insights");
        for (var i = 0; i < 3; i++)
        {
            await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, SupplierName = "Südhof Feinkost", Status = i == 0 ? DocumentStatus.AutoApproved : DocumentStatus.NeedsReview });
        }

        await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, SupplierName = "Bergquell" });
        await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, SupplierName = null, Type = DocumentTypeKey.Unknown, Invoice = null });
        var withLayout = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, SupplierName = "Nordwind", Flags = [FlagCodes.Create(FlagCodes.NewLayout, "No layout of the supplier holds.")] });
        var old = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, SupplierName = "Altbestand", CreatedAt = factory.Clock.GetUtcNow().AddDays(-60) });
        var nordwind = factory.Query(db => db.Documents.Single(d => d.Id == withLayout.DocumentId).SupplierId!.Value);
        factory.Run(db =>
        {
            var layout = new SupplierLayout { Id = Guid.CreateVersion7(), TenantId = tenant, Scope = SupplierLayoutScope.Supplier, SupplierId = nordwind, OwnerId = nordwind, DocumentTypeKey = "Invoice", LayoutKey = "nordwind-invoice", Name = "Nordwind invoice", ActiveVersion = 1, CreatedAt = factory.Clock.GetUtcNow(), UpdatedAt = factory.Clock.GetUtcNow() };
            db.SupplierLayouts.Add(layout);
            db.SupplierLayoutVersions.Add(new SupplierLayoutVersion { Id = Guid.CreateVersion7(), TenantId = tenant, LayoutId = layout.Id, Version = 1, Status = SupplierLayoutVersionStatus.Degraded, IdentificationJson = "{}", IdentificationHash = "00000000", MappingJson = "{}", CreatedAt = factory.Clock.GetUtcNow() });
        });
        using var reviewer = Reviewer(tenant);

        var insights = (await reviewer.GetFromJsonAsync<AutomationInsights>("/api/v1/insights/automation", ApiFactory.Json, Ct))!;

        insights.Documents.ShouldBe(6, "the 60-day-old document is outside the default 30 days");
        insights.Unknown.ShouldBe(1);
        insights.SuppliersWithoutLayouts.Select(s => s.Name).ShouldBe(["Südhof Feinkost", "Bergquell"]);
        var suedhof = insights.SuppliersWithoutLayouts[0];
        suedhof.Documents.ShouldBe(3);
        suedhof.AutoApproved.ShouldBe(1);
        suedhof.SampleDocumentId.ShouldNotBeNull();
        var nordwindRow = insights.Suppliers.Single(s => s.SupplierId == nordwind);
        nordwindRow.OpenNewLayout.ShouldBe(1);
        nordwindRow.Layouts.ShouldHaveSingleItem().State.ShouldBe("Degraded");
        insights.DegradedLayouts.ShouldHaveSingleItem().LayoutKey.ShouldBe("nordwind-invoice");
        insights.StaleLayouts.ShouldHaveSingleItem("never matched").SupplierName.ShouldBe("Nordwind");
        insights.UnknownByChannel.ShouldHaveSingleItem().Unknown.ShouldBe(1);

        var wider = (await reviewer.GetFromJsonAsync<AutomationInsights>("/api/v1/insights/automation?from=" + DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime.AddDays(-80)).ToString("yyyy-MM-dd"), ApiFactory.Json, Ct))!;
        wider.Suppliers.ShouldContain(s => s.Name == "Altbestand");
        _ = old;
        (await reviewer.GetAsync("/api/v1/insights/automation?from=2026-01-01&to=2026-12-31", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "at most 92 days");

        // Another tenant sees none of it.
        using var stranger = Reviewer(factory.CreateTenant("Insights stranger"));
        (await stranger.GetFromJsonAsync<AutomationInsights>("/api/v1/insights/automation", ApiFactory.Json, Ct))!.Documents.ShouldBe(0);
    }
}
