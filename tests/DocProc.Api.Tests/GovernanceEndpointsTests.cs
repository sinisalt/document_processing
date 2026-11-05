using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using Microsoft.AspNetCore.Mvc;

namespace DocProc.Api.Tests;

/// <summary>
/// Golden documents, sandbox and regression runs and the configuration bundle of P4 part C (docs/11 §6.9, §15.3,
/// §15.4): marking an approved document golden (and refusing one in review), a sandbox run of a draft layout that holds
/// and of one that does not (nothing written to the document), a regression run over the golden documents that passes
/// and one that reports the differences, the bundle exported with its signature, imported as a dry run and applied to
/// another tenant (a Candidate layout, a disabled source rule), and a tampered bundle refused.
/// </summary>
public sealed class GovernanceEndpointsTests : IClassFixture<ApiFactory>
{
    private const string Vat = "DE312456789";
    private const string Rule = """{ "all": [ { "text": "Rechnung", "page": 1, "region": "title" }, { "supplierValue": "vatId", "page": 1 } ] }""";
    private const string Mapping = """{ "fields": { "invoiceNumber": { "rules": [ { "strategy": "label-right", "labels": ["Rechnungs-Nr."] } ] } } }""";
    private static readonly JsonSerializerOptions TextLayerJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, IgnoreReadOnlyProperties = true };

    private readonly ApiFactory _factory;

    public GovernanceEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Run(db =>
        {
            foreach (var tenant in db.Tenants.Where(t => t.Id == ApiFactory.TenantA || t.Id == ApiFactory.TenantB))
            {
                tenant.Settings.Features!.SupplierLayouts = true;
                tenant.Settings.Features!.SourceRouting = true;
            }
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Admin(Guid? tenant = null) => _factory.ClientFor(new TokenSpec("tenant-admin", [Roles.TenantAdmin], (tenant ?? ApiFactory.TenantA).ToString()));

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

    private static string Page(string number) => Layer((40, "Nordwind USt-IdNr. DE 312 456 789"), (200, "Rechnung"), (240, $"Rechnungs-Nr. {number}"));

    private (Guid Supplier, Guid Layout) SeedLayout(string name, Guid tenantId, string? vat = Vat)
    {
        Guid supplierId = Guid.CreateVersion7(), layoutId = Guid.CreateVersion7();
        _factory.Run(db =>
        {
            db.Suppliers.Add(new Supplier { Id = supplierId, TenantId = tenantId, Name = name, NormalizedName = name.ToUpperInvariant(), VatId = vat, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            var parsed = IdentificationRule.Parse(Rule);
            db.SupplierLayouts.Add(new SupplierLayout { Id = layoutId, TenantId = tenantId, Scope = SupplierLayoutScope.Supplier, SupplierId = supplierId, OwnerId = supplierId, DocumentTypeKey = "Invoice", LayoutKey = "nordwind-invoice-erp", Name = "ERP", ActiveVersion = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.SupplierLayoutVersions.Add(new SupplierLayoutVersion { Id = Guid.CreateVersion7(), TenantId = tenantId, LayoutId = layoutId, Version = 1, Status = SupplierLayoutVersionStatus.Active, IdentificationJson = parsed.ToJson(), IdentificationHash = parsed.Hash, MappingJson = Mapping, CreatedAt = DateTimeOffset.UtcNow });
            db.SupplierSourceRules.Add(new SupplierSourceRule { Id = Guid.CreateVersion7(), TenantId = tenantId, SupplierId = supplierId, Sender = $"rechnung@{Guid.NewGuid():N}.example", ConditionHash = Guid.NewGuid().ToString("N")[..16], CreatedBy = "test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        });
        return (supplierId, layoutId);
    }

    private async Task<Guid> SeedAsync(string name, string number, DocumentStatus status = DocumentStatus.Approved)
    {
        var seeded = await _factory.SeedDocumentAsync(new DocumentSeed { SupplierName = name, TextLayerJson = Page(number), Status = status, Invoice = TestInvoices.Valid(number) });
        return seeded.DocumentId;
    }

    [Fact]
    public async Task An_approved_document_is_kept_golden_with_its_values_and_a_regression_run_compares_a_version_with_them()
    {
        var name = $"Golden {Guid.NewGuid():N}";
        var (_, layout) = SeedLayout(name, ApiFactory.TenantA);
        var approved = await SeedAsync(name, "RE-2026-0042");
        var inReview = await SeedAsync(name, "RE-2026-0043", DocumentStatus.NeedsReview);
        using var admin = Admin();

        var marked = await admin.PostAsJsonAsync($"/api/v1/documents/{approved}:golden", new MarkGoldenRequest("first ERP invoice"), Ct);
        marked.StatusCode.ShouldBe(HttpStatusCode.Created, await marked.Content.ReadAsStringAsync(Ct));
        var golden = (await marked.Content.ReadFromJsonAsync<GoldenDocumentDto>(ApiFactory.Json, Ct))!;
        golden.ExpectedValues.GetProperty("invoiceNumber").GetString().ShouldBe("RE-2026-0042");
        (await admin.PostAsJsonAsync($"/api/v1/documents/{approved}:golden", new MarkGoldenRequest(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "golden once");
        (await admin.PostAsJsonAsync($"/api/v1/documents/{inReview}:golden", new MarkGoldenRequest(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "only an approved document");
        (await admin.GetFromJsonAsync<List<GoldenDocumentDto>>($"/api/v1/golden-documents?typeKey=Invoice&supplierId={golden.SupplierId}", ApiFactory.Json, Ct))!.ShouldHaveSingleItem().Id.ShouldBe(golden.Id);

        var passing = await admin.PostAsJsonAsync("/api/v1/regression-runs", new RegressionRunRequest(layout, 1), Ct);
        passing.StatusCode.ShouldBe(HttpStatusCode.Created, await passing.Content.ReadAsStringAsync(Ct));
        (await passing.Content.ReadFromJsonAsync<RegressionRunDto>(ApiFactory.Json, Ct))!.Passed.ShouldBeTrue();

        // A version whose mapping reads another value fails, and names the field.
        _factory.Run(db => db.SupplierLayoutVersions.Single(v => v.LayoutId == layout).MappingJson = """{ "fields": { "invoiceNumber": { "rules": [ { "strategy": "constant", "value": "X-1" } ] } } }""");
        var failing = (await (await admin.PostAsJsonAsync("/api/v1/regression-runs", new RegressionRunRequest(layout, 1), Ct)).Content.ReadFromJsonAsync<RegressionRunDto>(ApiFactory.Json, Ct))!;
        failing.Passed.ShouldBeFalse();
        failing.Result.GetRawText().ShouldContain("invoiceNumber");
        (await admin.GetFromJsonAsync<RegressionRunDto>($"/api/v1/regression-runs/{failing.Id}", ApiFactory.Json, Ct))!.Kind.ShouldBe(nameof(RegressionRunKind.Regression));

        (await admin.DeleteAsync($"/api/v1/golden-documents/{golden.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_sandbox_run_shows_what_a_draft_layout_would_do_without_writing_anything()
    {
        var name = $"Sandbox {Guid.NewGuid():N}";
        SeedLayout(name, ApiFactory.TenantA);
        var document = await SeedAsync(name, "RE-2026-0050", DocumentStatus.NeedsReview);
        string? before = null;
        _factory.Run(db => before = db.Documents.Single(d => d.Id == document).CanonicalJson);
        using var admin = Admin();

        var holds = await admin.PostAsJsonAsync("/api/v1/sandbox/runs", new SandboxRunRequest(document, Identification: JsonDocument.Parse(Rule).RootElement, Mapping: JsonDocument.Parse(Mapping).RootElement), Ct);
        holds.StatusCode.ShouldBe(HttpStatusCode.Created, await holds.Content.ReadAsStringAsync(Ct));
        var run = (await holds.Content.ReadFromJsonAsync<RegressionRunDto>(ApiFactory.Json, Ct))!;
        run.Kind.ShouldBe(nameof(RegressionRunKind.Sandbox));
        run.Result.GetProperty("resolution").GetString().ShouldBe("identified");
        run.Result.GetProperty("values").GetProperty("invoiceNumber").GetString().ShouldBe("RE-2026-0050");
        run.Passed.ShouldBeTrue();

        var newLayout = (await (await admin.PostAsJsonAsync("/api/v1/sandbox/runs", new SandboxRunRequest(document, Identification: JsonDocument.Parse("""{ "all": [ { "text": "INVOICE" } ] }""").RootElement, Mapping: JsonDocument.Parse(Mapping).RootElement), Ct)).Content.ReadFromJsonAsync<RegressionRunDto>(ApiFactory.Json, Ct))!;
        newLayout.Result.GetProperty("resolution").GetString().ShouldBe("newLayout");
        newLayout.Result.GetProperty("failed").GetArrayLength().ShouldBeGreaterThan(0);
        newLayout.Passed.ShouldBeFalse();
        (await admin.GetFromJsonAsync<RegressionRunDto>($"/api/v1/sandbox/runs/{newLayout.Id}", ApiFactory.Json, Ct))!.DocumentId.ShouldBe(document);

        _factory.Run(db => db.Documents.Single(d => d.Id == document).CanonicalJson.ShouldBe(before, "a sandbox run writes nothing to the document"));
        (await admin.PostAsJsonAsync("/api/v1/sandbox/runs", new SandboxRunRequest(document), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var reviewer = _factory.ClientFor(new TokenSpec("tenant-a-reviewer", [Roles.Reviewer], ApiFactory.TenantA.ToString()));
        (await reviewer.PostAsJsonAsync("/api/v1/sandbox/runs", new SandboxRunRequest(document), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_configuration_bundle_is_signed_exported_imported_as_a_dry_run_and_applied_to_another_tenant()
    {
        var name = $"Bundle {Guid.NewGuid():N}";
        var vat = $"DE{Random.Shared.Next(100_000_000, 999_999_999)}";
        SeedLayout(name, ApiFactory.TenantA, vat);
        using var a = Admin();
        var exported = await a.GetAsync("/api/v1/config/bundle?include=layouts,sourceRules", Ct);
        exported.StatusCode.ShouldBe(HttpStatusCode.OK, await exported.Content.ReadAsStringAsync(Ct));
        var bundle = JsonNode.Parse(await exported.Content.ReadAsStringAsync(Ct))!.AsObject();
        bundle["format"]!.GetValue<string>().ShouldBe(GovernanceEndpoints.BundleFormat);
        bundle["version"]!.GetValue<int>().ShouldBe(GovernanceEndpoints.BundleVersion);
        bundle["signature"]!.GetValue<string>().Length.ShouldBe(64);
        bundle["layouts"]!.AsArray().ShouldContain(l => l!["supplier"]!["vatId"]!.GetValue<string>() == vat);
        (await a.GetAsync("/api/v1/config/bundle?include=secrets", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Tenant B has the supplier (same VAT id), no layout: the dry run reports, the apply creates a Candidate and a disabled rule.
        _factory.Run(db => db.Suppliers.Add(new Supplier { Id = Guid.CreateVersion7(), TenantId = ApiFactory.TenantB, Name = "Nordwind in B", NormalizedName = "NORDWIND IN B", VatId = vat, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }));
        using var b = Admin(ApiFactory.TenantB);
        var element = JsonDocument.Parse(bundle.ToJsonString()).RootElement;
        var dry = (await (await b.PostAsJsonAsync("/api/v1/config/bundle:import", new BundleImportRequest(element), Ct)).Content.ReadFromJsonAsync<BundleImportResponse>(ApiFactory.Json, Ct))!;
        dry.Mode.ShouldBe("dry-run");
        dry.SignatureValid.ShouldBeTrue();
        dry.Items.ShouldContain(i => i.Kind == "layout" && i.Key == "nordwind-invoice-erp" && i.Action == "create");
        _factory.Run(db => db.SupplierLayouts.Any(l => l.TenantId == ApiFactory.TenantB && l.LayoutKey == "nordwind-invoice-erp").ShouldBeFalse("a dry run writes nothing"));

        var applied = await b.PostAsJsonAsync("/api/v1/config/bundle:import", new BundleImportRequest(element, "apply"), Ct);
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        _factory.Run(db =>
        {
            var layout = db.SupplierLayouts.Single(l => l.TenantId == ApiFactory.TenantB && l.LayoutKey == "nordwind-invoice-erp");
            var version = db.SupplierLayoutVersions.Single(v => v.LayoutId == layout.Id);
            version.Status.ShouldBe(SupplierLayoutVersionStatus.Candidate);
            version.Origin.ShouldBe(SupplierLayoutOrigin.Imported);
            db.SupplierSourceRules.Where(r => r.TenantId == ApiFactory.TenantB && r.SupplierId == layout.SupplierId).ShouldAllBe(r => !r.Enabled);
        });
        var again = (await (await b.PostAsJsonAsync("/api/v1/config/bundle:import", new BundleImportRequest(element), Ct)).Content.ReadFromJsonAsync<BundleImportResponse>(ApiFactory.Json, Ct))!;
        again.Items.ShouldContain(i => i.Kind == "layout" && i.Action == "skip", "the same rule and mapping again is nothing new");

        // A bundle changed after export does not verify.
        bundle["layouts"]![0]!["layoutKey"] = "tampered";
        var tampered = await b.PostAsJsonAsync("/api/v1/config/bundle:import", new BundleImportRequest(JsonDocument.Parse(bundle.ToJsonString()).RootElement), Ct);
        tampered.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tampered.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Extensions["code"]!.ToString().ShouldBe("BUNDLE_SIGNATURE_INVALID");
    }
    [Fact]
    public async Task An_imported_layout_passes_the_checks_of_the_layout_endpoint_and_a_member_of_the_wrong_type_is_no_500()
    {
        var vat = $"DE{Random.Shared.Next(100_000_000, 999_999_999)}";
        _factory.Run(db => db.Suppliers.Add(new Supplier { Id = Guid.CreateVersion7(), TenantId = ApiFactory.TenantB, Name = $"Import {vat}", NormalizedName = $"IMPORT {vat}", VatId = vat, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }));
        using var b = Admin(ApiFactory.TenantB);
        JsonObject Layout(string key, string type, string? name = null) => new()
        {
            ["layoutKey"] = key,
            ["documentTypeKey"] = type,
            ["name"] = name ?? key,
            ["supplier"] = new JsonObject { ["vatId"] = vat },
            ["identification"] = JsonNode.Parse("""{ "all": [ { "text": "Rechnung" }, { "supplierValue": "vatId" } ] }"""),
            ["mapping"] = JsonNode.Parse("""{ "fields": { "currency": { "rules": [ { "strategy": "constant", "value": "EUR" } ] } } }"""),
        };
        async Task<HttpResponseMessage> ImportAsync(JsonArray layouts, string mode = "dry-run")
        {
            var bundle = new JsonObject { ["format"] = GovernanceEndpoints.BundleFormat, ["version"] = GovernanceEndpoints.BundleVersion, ["layouts"] = layouts };
            bundle["signature"] = GovernanceEndpoints.Sign(bundle, GovernanceEndpoints.DevelopmentSigningKey);
            return await b.PostAsJsonAsync("/api/v1/config/bundle:import", new BundleImportRequest(JsonDocument.Parse(bundle.ToJsonString()).RootElement, mode), Ct);
        }

        var checkedItems = await ImportAsync([Layout("Not A Key!", "Invoice"), Layout("nordwind-x", "NoSuchType"), Layout("nordwind-y", "Invoice", new string('n', 500)), Layout("nordwind-ok", "Invoice")], "apply");
        checkedItems.StatusCode.ShouldBe(HttpStatusCode.OK, await checkedItems.Content.ReadAsStringAsync(Ct));
        var items = (await checkedItems.Content.ReadFromJsonAsync<BundleImportResponse>(ApiFactory.Json, Ct))!.Items;
        items.Single(i => i.Key == "Not A Key!").Action.ShouldBe("error");
        items.Single(i => i.Key == "Not A Key!").Detail!.ShouldContain("layout key");
        items.Single(i => i.Key == "nordwind-x").Action.ShouldBe("error");
        items.Single(i => i.Key == "nordwind-x").Detail!.ShouldContain("NoSuchType");
        items.Single(i => i.Key == "nordwind-y").Detail!.ShouldContain("name is at most");
        items.Single(i => i.Key == "nordwind-ok").Action.ShouldBe("create");
        _factory.Run(db =>
        {
            var stored = db.SupplierLayouts.Where(l => l.TenantId == ApiFactory.TenantB && l.Supplier!.VatId == vat).Select(l => l.LayoutKey).ToList();
            stored.ShouldBe(["nordwind-ok"], "only the layout that passes the checks is stored");
        });

        // A member of the wrong JSON type is an item error or a 400, never a 500.
        var numberKey = Layout("ignored", "Invoice");
        numberKey["layoutKey"] = 5;
        var wrongKey = await ImportAsync([numberKey]);
        wrongKey.StatusCode.ShouldBe(HttpStatusCode.OK, await wrongKey.Content.ReadAsStringAsync(Ct));
        (await wrongKey.Content.ReadFromJsonAsync<BundleImportResponse>(ApiFactory.Json, Ct))!.Items.ShouldHaveSingleItem().Action.ShouldBe("error");

        var objectSupplier = Layout("nordwind-z", "Invoice");
        objectSupplier["supplier"] = "not an object";
        objectSupplier["itemKinds"] = new JsonArray(1, 2);
        var wrongShapes = await ImportAsync([objectSupplier]);
        ((int)wrongShapes.StatusCode).ShouldBeLessThan(500, await wrongShapes.Content.ReadAsStringAsync(Ct));
        var objectDestination = new JsonObject { ["format"] = GovernanceEndpoints.BundleFormat, ["version"] = GovernanceEndpoints.BundleVersion, ["bindings"] = new JsonArray(new JsonObject { ["name"] = "x", ["destination"] = "flat", ["mappingKey"] = 7 }) };
        objectDestination["signature"] = GovernanceEndpoints.Sign(objectDestination, GovernanceEndpoints.DevelopmentSigningKey);
        var wrongBinding = await b.PostAsJsonAsync("/api/v1/config/bundle:import", new BundleImportRequest(JsonDocument.Parse(objectDestination.ToJsonString()).RootElement), Ct);
        ((int)wrongBinding.StatusCode).ShouldBeLessThan(500, await wrongBinding.Content.ReadAsStringAsync(Ct));
    }
}
