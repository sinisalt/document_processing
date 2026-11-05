using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Application.Configuration;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Configuration;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Mvc;

namespace DocProc.Api.Tests;

/// <summary>
/// The type catalogue API (docs/11 §18 "Types", §5.1, §5.7): templates, adopt, copy (also in place), new, draft edits
/// with If-Match, validate and publish with the publish checks, the roles and the feature switches that gate it, and
/// the resolver that makes a published version the one documents of the type are processed with.
/// </summary>
public sealed class DocumentTypeEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_role_reads_the_templates_including_the_price_lists()
    {
        var tenant = factory.CreateTenant("Templates");
        using var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], tenant.ToString()));

        var templates = (await viewer.GetFromJsonAsync<List<DocumentTemplateSummary>>("/api/v1/document-templates", ApiFactory.Json, Ct))!;

        templates.Select(t => t.Key).ShouldBe(["std.credit-note", "std.daily-specials", "std.delivery-note", "std.generic", "std.invoice", "std.price-list", "std.purchase-order", "std.reminder", "std.statement"]);
        templates.Single(t => t.Key == "std.price-list").DefaultKey.ShouldBe("price-list");
        templates.Single(t => t.Key == "std.invoice").DefaultKey.ShouldBe("Invoice");
        templates.ShouldAllBe(t => t.AdoptedAs.Count == 0);
        (await viewer.GetFromJsonAsync<List<DocumentTypeDetail>>("/api/v1/document-types", ApiFactory.Json, Ct))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Changing_the_catalogue_needs_ConfigAdmin_and_the_configurable_types_switch()
    {
        var tenant = factory.CreateTenant("Gated");
        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], tenant.ToString()));
        (await reviewer.PostAsJsonAsync("/api/v1/document-types", Adopt("std.price-list"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var admin = factory.TenantAdminOf(tenant);
        var off = await admin.PostAsJsonAsync("/api/v1/document-types", Adopt("std.price-list"), Ct);
        off.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await off.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail!.ShouldContain("configurableTypes");

        SetFeatures(tenant, configurable: true);
        (await admin.PostAsJsonAsync("/api/v1/document-types", Adopt("std.price-list"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // A type of the tenant's own needs customTypes too.
        var custom = await admin.PostAsJsonAsync("/api/v1/document-types", new { definition = JsonNode.Parse(ReturnSlip()) }, Ct);
        custom.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await custom.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail!.ShouldContain("customTypes");
    }

    [Fact]
    public async Task Adopting_the_price_list_publishes_it_at_once_with_its_compiled_artifacts()
    {
        var tenant = factory.CreateTenant("Acme");
        SetFeatures(tenant, configurable: true);
        using var admin = factory.TenantAdminOf(tenant);

        var created = await admin.PostAsJsonAsync("/api/v1/document-types", Adopt("std.price-list"), Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.ToString().ShouldBe("/api/v1/document-types/price-list");
        var type = (await created.Content.ReadFromJsonAsync<DocumentTypeDetail>(ApiFactory.Json, Ct))!;
        type.Key.ShouldBe("price-list");
        type.Origin.ShouldBe("Adopted");
        type.BaseTemplate.ShouldBe("std.price-list");
        type.PublishedVersion.ShouldBe(1);
        type.DraftVersion.ShouldBeNull();
        type.Versions.Single().State.ShouldBe("Published");
        type.Name["de"].ShouldBe("Preisliste");

        var version = (await admin.GetFromJsonAsync<DocumentTypeVersionDetail>("/api/v1/document-types/price-list/versions/1?include=formSpec,llmSchema,jsonSchema,descriptors", ApiFactory.Json, Ct))!;
        version.Definition.GetProperty("key").GetString().ShouldBe("price-list");
        version.Definition.GetProperty("base").GetProperty("mode").GetString().ShouldBe("adopt");
        version.FormSpec!.Value.GetProperty("sections").GetArrayLength().ShouldBe(3);
        version.LlmSchema!.Value.GetProperty("properties").TryGetProperty("unreadableRegions", out _).ShouldBeTrue();
        version.JsonSchema!.Value.GetProperty("properties").GetProperty("lines").GetProperty("maxItems").GetInt32().ShouldBe(10_000);
        version.Descriptors.ShouldNotBeNull();
        version.Version.LlmSchema.ShouldStartWith("doc_price-list@");
        var plain = (await admin.GetFromJsonAsync<DocumentTypeVersionDetail>("/api/v1/document-types/price-list/versions/1", ApiFactory.Json, Ct))!;
        plain.FormSpec.ShouldBeNull("artifacts only on request");

        var templates = (await admin.GetFromJsonAsync<List<DocumentTemplateSummary>>("/api/v1/document-templates", ApiFactory.Json, Ct))!;
        templates.Single(t => t.Key == "std.price-list").AdoptedAs.ShouldBe(["price-list"]);
        (await admin.PostAsJsonAsync("/api/v1/document-types", Adopt("std.price-list"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/v1/document-types", Adopt("std.nothing"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.price-list", mode = "adopt" }, key = "Unknown" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.price-list", mode = "merge" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsync("/api/v1/document-types/price-list/versions", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "an adopted type follows its template; copy it to edit it");

        // The adopted type is what its documents resolve to.
        var resolved = await ResolveAsync(tenant, "price-list");
        resolved.ShouldNotBeNull().Origin.ShouldBe(DefinitionOrigin.Adopted);
        resolved.TemplateKey.ShouldBe("std.price-list");
        resolved.VersionId.ShouldBe(factory.Query(db => db.DocumentTypeVersions.Single(v => v.TenantId == tenant).Id));
        resolved.AutoApprove.ShouldBeFalse("std.price-list v2 auto-approves, but a tenant opts in (docs/11 §5.8, §23)");

        // A ConfigAdmin switches auto-approval on: a new published version of the adopted type, the old one deprecated.
        var upgraded = await admin.PostAsJsonAsync("/api/v1/document-types/price-list:upgrade-base", new { autoApprove = true }, Ct);
        upgraded.StatusCode.ShouldBe(HttpStatusCode.OK, await upgraded.Content.ReadAsStringAsync(Ct));
        var detail = (await upgraded.Content.ReadFromJsonAsync<DocumentTypeDetail>(ApiFactory.Json, Ct))!;
        detail.PublishedVersion.ShouldBe(2);
        detail.BaseVersion.ShouldBe(2);
        detail.Versions.Single(v => v.Version == 1).State.ShouldBe("Deprecated");
        (await ResolveAsync(tenant, "price-list"))!.AutoApprove.ShouldBeTrue("v2 names the rules that stand in for a totals check");

        // Asking again changes nothing; switching it off publishes a version without auto-approval.
        var again = (await (await admin.PostAsJsonAsync("/api/v1/document-types/price-list:upgrade-base", new { autoApprove = true }, Ct)).Content.ReadFromJsonAsync<DocumentTypeDetail>(ApiFactory.Json, Ct))!;
        again.PublishedVersion.ShouldBe(2);
        (await admin.PostAsJsonAsync("/api/v1/document-types/price-list:upgrade-base", new { autoApprove = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ResolveAsync(tenant, "price-list"))!.AutoApprove.ShouldBeFalse();

        // Only an adopted type follows its template, and a template without auto-approval cannot get it.
        (await admin.PostAsJsonAsync("/api/v1/document-types/nothing:upgrade-base", new { autoApprove = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_published_version_runs_on_the_artifacts_it_stored_not_on_a_new_compile()
    {
        // docs/11 §5.5, §5.7: a later build's compiler or template must not change a published, pinned version.
        var tenant = factory.CreateTenant("Stored artifacts");
        SetFeatures(tenant, configurable: true);
        using var admin = factory.TenantAdminOf(tenant);
        (await admin.PostAsJsonAsync("/api/v1/document-types", Adopt("std.price-list"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var compiledNow = (await ResolveAsync(tenant, "price-list"))!.Compiled.ShouldNotBeNull();

        // What an older compile stored: another form spec and schema version than this build would produce.
        factory.Run(db =>
        {
            var row = db.DocumentTypeVersions.Single(v => v.TenantId == tenant && v.Definition!.Key == "price-list");
            var form = JsonNode.Parse(row.FormSpecJson!)!.AsObject();
            form["storedMarker"] = true;
            row.FormSpecJson = form.ToJsonString();
            row.LlmSchemaVersion = "stored-0001";
        });

        var stored = (await ResolveAsync(tenant, "price-list"))!.Compiled.ShouldNotBeNull();
        stored.FormSpec["storedMarker"]!.GetValue<bool>().ShouldBeTrue();
        stored.LlmSchema.Version.ShouldBe("stored-0001");
        stored.Descriptors.Fields.Select(f => f.Path).ShouldBe(compiledNow.Descriptors.Fields.Select(f => f.Path));
        stored.JsonSchemaJson.ShouldBe(compiledNow.JsonSchemaJson);

        // A row without its artifacts is compiled, as before.
        factory.Run(db => db.DocumentTypeVersions.Single(v => v.TenantId == tenant && v.Definition!.Key == "price-list").DescriptorsJson = null);
        (await ResolveAsync(tenant, "price-list"))!.Compiled.ShouldNotBeNull().LlmSchema.Version.ShouldBe(compiledNow.LlmSchema.Version);
    }

    [Fact]
    public async Task Forking_the_credit_note_in_place_adds_a_custom_field_and_publishes_version_2()
    {
        var tenant = factory.CreateTenant("Acme fork");
        Seed(tenant);
        SetFeatures(tenant, configurable: true);
        using var admin = factory.TenantAdminOf(tenant);
        var adoptedV1 = (await ResolveAsync(tenant, "CreditNote"))!.VersionId;

        // docs/11 §5.1: the type keeps its key, so CreditNote is forked in place.
        var fork = await admin.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.credit-note", mode = "copy" } }, Ct);
        fork.StatusCode.ShouldBe(HttpStatusCode.OK);
        var forked = (await fork.Content.ReadFromJsonAsync<DocumentTypeDetail>(ApiFactory.Json, Ct))!;
        forked.Origin.ShouldBe("Copied");
        forked.PublishedVersion.ShouldBe(1);
        forked.DraftVersion.ShouldBe(2);

        var draft = await admin.GetAsync("/api/v1/document-types/CreditNote/versions/2", Ct);
        draft.Headers.ETag!.Tag.ShouldBe("\"0\"");
        var detail = (await draft.Content.ReadFromJsonAsync<DocumentTypeVersionDetail>(ApiFactory.Json, Ct))!;
        detail.Definition.GetProperty("base").GetProperty("mode").GetString().ShouldBe("copy");

        // The custom field of docs/11 §5.3, indexed so it is searchable later.
        var edited = JsonNode.Parse(detail.Definition.GetRawText())!.AsObject();
        edited["fields"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "fld_c_scr", "key": "supplierCustomerRef", "kind": "identifier", "indexed": true, "section": "document",
              "label": { "en": "Our customer no. at supplier", "de": "Unsere Kundennummer beim Lieferanten" },
              "description": "The account number the supplier assigned to us, printed as Kunden-Nr. or Kd.-Nr.",
              "extraction": { "labels": { "de": ["Kunden-Nr.", "Kd.-Nr."] }, "pattern": "^[0-9]{3}\\s?[0-9]{6}$" } }
            """));

        (await Put(admin, "CreditNote", 2, edited, ifMatch: null)).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        var saved = await Put(admin, "CreditNote", 2, edited, ifMatch: "\"0\"");
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        saved.Headers.ETag!.Tag.ShouldBe("\"1\"");
        (await Put(admin, "CreditNote", 2, edited, ifMatch: "\"0\"")).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, "the draft moved on");

        var renamed = edited.DeepClone().AsObject();
        renamed["key"] = "Gutschrift";
        (await Put(admin, "CreditNote", 2, renamed, ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var broken = edited.DeepClone().AsObject();
        broken["fields"]![0]!["kind"] = "colour";
        var refused = await Put(admin, "CreditNote", 2, broken, ifMatch: "\"1\"");
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("/fields/0/kind");

        var report = (await (await admin.PostAsync("/api/v1/document-types/CreditNote/versions/2:validate", null, Ct)).Content.ReadFromJsonAsync<PublishReportDto>(ApiFactory.Json, Ct))!;
        report.CanPublish.ShouldBeTrue(string.Join("; ", report.Problems.Select(p => $"{p.Where}: {p.Message}")));
        report.Changes.ShouldContain(c => c.Change == "Added" && c.NewPath == "supplierCustomerRef");
        report.LlmSchema!.ShouldStartWith("invoice_extraction@1+x.");

        var published = await admin.PostAsJsonAsync("/api/v1/document-types/CreditNote/versions/2:publish", new { changeNote = "Customer number at supplier" }, Ct);
        published.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = (await published.Content.ReadFromJsonAsync<DocumentTypeDetail>(ApiFactory.Json, Ct))!;
        after.PublishedVersion.ShouldBe(2);
        after.Versions.Select(v => (v.Version, v.State)).ShouldBe([(1, "Deprecated"), (2, "Published")]);
        after.Versions[1].ChangeNote.ShouldBe("Customer number at supplier");
        (await Put(admin, "CreditNote", 2, edited, ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.Conflict, "a published version is immutable");

        // New documents resolve to version 2 with the x_custom extension; a document pinned to v1 keeps v1.
        var current = (await ResolveAsync(tenant, "CreditNote"))!;
        current.Version.ShouldBe(2);
        current.Origin.ShouldBe(DefinitionOrigin.Copied);
        current.Definition.Fields.ShouldContain(f => f.Key == "supplierCustomerRef");
        current.Rules.Select(r => r.Kind).ShouldBe(StandardTemplateConfigResolver.TypeFor(DocumentTypeKey.CreditNote)!.Rules.Select(r => r.Kind), "the fork keeps the invoice rules");
        var pinned = (await ResolveAsync(tenant, "CreditNote", adoptedV1))!;
        pinned.Version.ShouldBe(1);
        pinned.VersionId.ShouldBe(adoptedV1);

        // With the switch off again, the fork no longer counts: the tenant is back on the standard template.
        SetFeatures(tenant, configurable: false);
        var off = (await ResolveAsync(tenant, "CreditNote"))!;
        off.Origin.ShouldBe(DefinitionOrigin.Adopted);
        off.VersionId.ShouldBeNull();
        off.Definition.ShouldBeSameAs(StandardTemplateConfigResolver.TypeFor(DocumentTypeKey.CreditNote)!.Definition);
    }

    [Fact]
    public async Task A_new_type_is_a_draft_that_publishes_only_when_the_checks_pass()
    {
        var tenant = factory.CreateTenant("Returns");
        SetFeatures(tenant, configurable: true, custom: true);
        using var admin = factory.TenantAdminOf(tenant);

        // A rule kind this deployment does not have yet: the draft saves, the publish is refused.
        var withP5Rule = JsonNode.Parse(ReturnSlip())!.AsObject();
        withP5Rule["rules"]!.AsArray().Add(JsonNode.Parse("""{ "id": "r_table", "kind": "columnSum", "params": { "path": "items" } }"""));
        var created = await admin.PostAsJsonAsync("/api/v1/document-types", new { definition = withP5Rule }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var type = (await created.Content.ReadFromJsonAsync<DocumentTypeDetail>(ApiFactory.Json, Ct))!;
        type.Origin.ShouldBe("Custom");
        type.PublishedVersion.ShouldBeNull();
        type.DraftVersion.ShouldBe(1);
        (await ResolveAsync(tenant, "return-slip")).ShouldBeNull("a type with no published version resolves to nothing");

        var blocked = await admin.PostAsync("/api/v1/document-types/return-slip/versions/1:publish", null, Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await blocked.Content.ReadAsStringAsync(Ct));
        var report = (await blocked.Content.ReadFromJsonAsync<PublishReportDto>(ApiFactory.Json, Ct))!;
        report.CanPublish.ShouldBeFalse();
        report.Problems.ShouldContain(p => p.Message.Contains("rule kind 'columnSum' is not available yet"));

        var fixedDefinition = JsonNode.Parse(ReturnSlip())!.AsObject();
        (await Put(admin, "return-slip", 1, fixedDefinition, ifMatch: "\"0\"")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsync("/api/v1/document-types/return-slip/versions/1:publish", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var resolved = (await ResolveAsync(tenant, "return-slip"))!;
        resolved.Origin.ShouldBe(DefinitionOrigin.Custom);
        resolved.AutoApprove.ShouldBeFalse("a tenant-defined type defaults to always-review (docs/11 §11)");

        // The next version starts from the published one; removing the indexed return number is blocked by its index.
        var next = await admin.PostAsync("/api/v1/document-types/return-slip/versions", null, Ct);
        next.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PostAsync("/api/v1/document-types/return-slip/versions", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "one draft at a time");
        var v2 = JsonNode.Parse(ReturnSlip(version: 2))!.AsObject();
        v2["roles"]!.AsObject().Remove("documentNumber");
        v2["roles"]!["duplicateKey"] = new JsonArray("$supplierId", "returnDate");
        v2["fields"]!.AsArray().RemoveAt(0);
        (await Put(admin, "return-slip", 2, v2, ifMatch: "\"0\"")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var removal = await admin.PostAsync("/api/v1/document-types/return-slip/versions/2:publish", null, Ct);
        removal.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var removalReport = (await removal.Content.ReadFromJsonAsync<PublishReportDto>(ApiFactory.Json, Ct))!;
        removalReport.BlockingDependents.ShouldContain(d => d.Kind == "indexedKey" && d.Path == "returnNumber");
        removalReport.Changes.ShouldContain(c => c.Change == "Removed" && c.FieldId == "fld_rn");

        // Without customTypes the tenant's own type can be neither edited nor published, and its documents do not use it.
        SetFeatures(tenant, configurable: true, custom: false);
        (await Put(admin, "return-slip", 2, v2, ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.PostAsync("/api/v1/document-types/return-slip/versions/2:publish", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ResolveAsync(tenant, "return-slip")).ShouldBeNull("a type of the tenant's own counts only with customTypes");
        SetFeatures(tenant, configurable: true, custom: true);
        (await ResolveAsync(tenant, "return-slip")).ShouldNotBeNull().Origin.ShouldBe(DefinitionOrigin.Custom);

        // A key that is taken, a key of the platform, and a definition that is not one.
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { definition = JsonNode.Parse(ReturnSlip()) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { definition = JsonNode.Parse(ReturnSlip().Replace("\"return-slip\"", "\"std.mine\"", StringComparison.Ordinal)) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { definition = new { key = "x" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync("/api/v1/document-types/nope", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync("/api/v1/document-types/return-slip/versions/9", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_tenant_sees_only_its_own_types()
    {
        var tenant = factory.CreateTenant("Mine");
        var other = factory.CreateTenant("Theirs");
        SetFeatures(tenant, configurable: true);
        using (var admin = factory.TenantAdminOf(tenant))
        {
            (await admin.PostAsJsonAsync("/api/v1/document-types", Adopt("std.daily-specials"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var theirs = factory.TenantAdminOf(other);
        (await theirs.GetFromJsonAsync<List<DocumentTypeDetail>>("/api/v1/document-types", ApiFactory.Json, Ct))!.ShouldBeEmpty();
        (await theirs.GetAsync("/api/v1/document-types/daily-specials", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_a_tenant_adopts_the_legacy_types()
    {
        using var platform = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));
        var slug = "adopts-" + Guid.NewGuid().ToString("N")[..8];
        var created = await platform.PostAsJsonAsync("/api/v1/tenants", new { name = "Adopts", slug, legalName = "Adopts GmbH", defaultCurrency = "EUR", countryCode = "DE", timeZone = "Europe/Berlin" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = factory.Query(db => db.Tenants.Single(t => t.Slug == slug).Id);

        var keys = factory.Query(db => db.DocumentTypeDefinitions.Where(d => d.TenantId == id).Select(d => d.Key).ToList());
        keys.Order(StringComparer.Ordinal).ShouldBe(["CreditNote", "DeliveryNote", "Invoice", "Other", "PurchaseOrder", "Reminder", "Statement"]);
        factory.Query(db => db.DocumentTypeDefinitions.Count(d => d.TenantId == null)).ShouldBe(9, "the platform templates are there for the adopted types to refer to");
        var invoice = (await ResolveAsync(id, "Invoice"))!;
        invoice.Origin.ShouldBe(DefinitionOrigin.Adopted);
        invoice.VersionId.ShouldNotBeNull();
        invoice.Rules.ShouldBe(StandardTemplateConfigResolver.TypeFor(DocumentTypeKey.Invoice)!.Rules, "the adopted v1 runs exactly the standard rules");
        invoice.AutoApprove.ShouldBeTrue();
    }

    [Fact]
    public async Task A_correction_records_the_type_version_of_its_document_and_refreshes_the_columns_by_its_roles()
    {
        var tenant = factory.CreateTenant("Corrections");
        Seed(tenant);
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant });
        var version = factory.Query(db => db.DocumentTypeVersions.Single(v => v.TenantId == tenant && v.Definition!.Key == "Invoice").Id);
        factory.Run(db => db.Documents.Single(d => d.Id == seeded.DocumentId).DocumentTypeVersionId = version);
        using var reviewer = factory.ClientFor(new TokenSpec("rev-" + tenant.ToString("N")[..6], [Roles.Reviewer], tenant.ToString()));

        var response = await reviewer.PatchAsJsonAsync(
            $"/api/v1/documents/{seeded.DocumentId}/fields",
            new CorrectFieldsRequest([new FieldCorrection("invoiceNumber", System.Text.Json.JsonSerializer.SerializeToElement("RE-2026-0099"), null)]),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Query(db => db.Corrections.Single(c => c.DocumentId == seeded.DocumentId).DocumentTypeVersionId).ShouldBe(version);
        factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId).DocumentNumber).ShouldBe("RE-2026-0099", "the adopted invoice binds documentNumber to invoiceNumber");
    }

    [Fact]
    public async Task The_back_fill_gives_corrections_a_version_only_when_the_catalogue_arrives()
    {
        var tenant = factory.CreateTenant("Back-fill");
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant });
        Correction Made(string by) => new() { TenantId = tenant, DocumentId = seeded.DocumentId, FieldPath = "invoiceNumber", By = by, At = factory.Clock.GetUtcNow() };
        factory.Run(db => db.Corrections.Add(Made("before the catalogue")));

        // The start that adds the tenant's adopted types pins its documents and their earlier corrections.
        await using (var db = factory.PlatformDb())
        {
            await TypeCatalogueSeeder.AddMissingAdoptedTypesAsync(db, tenant, factory.Clock.GetUtcNow(), Ct);
            await db.SaveChangesAsync(Ct);
            await TypeCatalogueSeeder.BackfillTypeVersionsAsync(db, [tenant], Ct);
        }

        var version = factory.Query(db => db.DocumentTypeVersions.Single(v => v.TenantId == tenant && v.Definition!.Key == "Invoice").Id);
        factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId).DocumentTypeVersionId).ShouldBe(version);
        factory.Query(db => db.Corrections.Single(c => c.DocumentId == seeded.DocumentId).DocumentTypeVersionId).ShouldBe(version);

        // A correction made later without a version (the document was not pinned then) keeps none on every later start.
        factory.Run(db => db.Corrections.Add(Made("without a pin")));
        await using (var db = factory.PlatformDb())
        {
            await TypeCatalogueSeeder.BackfillTypeVersionsAsync(db, [], Ct);
        }

        factory.Query(db => db.Corrections.Single(c => c.DocumentId == seeded.DocumentId && c.By == "without a pin").DocumentTypeVersionId).ShouldBeNull();
    }

    [Fact]
    public async Task An_invoice_type_keeps_an_invoice_key_and_an_oversized_draft_is_not_stored()
    {
        var tenant = factory.CreateTenant("Limits");
        SetFeatures(tenant, configurable: true, custom: true);
        using var admin = factory.TenantAdminOf(tenant);

        // Under another key the invoice extraction and rules would not run (they follow the key until P2).
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.invoice", mode = "copy" }, key = "Rechnung" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.credit-note", mode = "adopt" }, key = "Gutschrift" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var withAdapter = JsonNode.Parse(ReturnSlip())!.AsObject();
        withAdapter["llm"] = new JsonObject { ["adapter"] = "invoice_extraction@1" };
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { definition = withAdapter }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The size caps of §5.7 apply on save, not only on publish.
        var many = JsonNode.Parse(ReturnSlip())!.AsObject();
        for (var i = 0; i < DocProc.Definitions.Compilation.DefinitionCompiler.MaxFields; i++)
        {
            many["fields"]!.AsArray().Add(new JsonObject { ["id"] = $"fld_x{i}", ["key"] = $"extra{i}", ["kind"] = "string" });
        }

        var tooMany = await admin.PostAsJsonAsync("/api/v1/document-types", new { definition = many }, Ct);
        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooMany.Content.ReadAsStringAsync(Ct)).ShouldContain($"at most {DocProc.Definitions.Compilation.DefinitionCompiler.MaxFields}");

        // A definition beyond the length cap is refused before it is read (400 from the catalogue, or 413 from the server).
        var huge = JsonNode.Parse(ReturnSlip())!.AsObject();
        huge["description"] = new string('x', DocumentTypeCatalogue.MaxDefinitionLength);
        var refused = await admin.PostAsJsonAsync("/api/v1/document-types", new { definition = huge }, Ct);
        refused.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge);
        factory.Query(db => db.DocumentTypeDefinitions.Count(d => d.TenantId == tenant && d.Origin == DefinitionOrigin.Custom)).ShouldBe(0);

        // The same through a draft save.
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { definition = JsonNode.Parse(ReturnSlip()) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var put = await Put(admin, "return-slip", 1, many.DeepClone().AsObject(), ifMatch: "\"0\"");
        put.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.Query(db => db.DocumentTypeVersions.Single(v => v.TenantId == tenant && v.Definition!.Key == "return-slip").Revision).ShouldBe(0);
    }

    /// <summary>
    /// A type published several times: the list, the type and the templates answer from version summaries (the catalogue
    /// loads the definition of the one version the indexed fields come from, never the compiled artifacts), one version
    /// is read in full on its own; publishing reads the keys of every earlier version, so a field renamed twice names
    /// both former keys, and a field may not take another field's earlier key. A <c>pattern</c> rule the engine cannot
    /// run and sibling keys that differ only by case are refused at publish.
    /// </summary>
    [Fact]
    public async Task A_type_with_many_versions_lists_summaries_and_publishing_knows_every_earlier_key()
    {
        var tenant = factory.CreateTenant("Many versions");
        SetFeatures(tenant, configurable: true, custom: true);
        using var admin = factory.TenantAdminOf(tenant);
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { definition = JsonNode.Parse(ReturnSlip()) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PostAsync("/api/v1/document-types/return-slip/versions/1:publish", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        async Task<HttpResponseMessage> PublishNext(int version, Action<JsonObject> edit, int revision = 0)
        {
            if (revision == 0)
            {
                (await admin.PostAsync("/api/v1/document-types/return-slip/versions", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
            }

            var draft = JsonNode.Parse((await admin.GetFromJsonAsync<DocumentTypeVersionDetail>($"/api/v1/document-types/return-slip/versions/{version}", ApiFactory.Json, Ct))!.Definition.GetRawText())!.AsObject();
            edit(draft);
            (await Put(admin, "return-slip", version, draft, ifMatch: $"\"{revision}\"")).StatusCode.ShouldBe(HttpStatusCode.OK);
            return await admin.PostAsync($"/api/v1/document-types/return-slip/versions/{version}:publish", null, Ct);
        }

        static JsonNode FieldOf(JsonObject definition, string id) => definition["fields"]!.AsArray().Single(f => f!["id"]!.GetValue<string>() == id)!;

        void RenameDate(JsonObject d, string key)
        {
            FieldOf(d, "fld_rd")["key"] = key;
            d["roles"]!["documentDate"] = key;
        }

        (await PublishNext(2, d => RenameDate(d, "returnedOn"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Version 3: a lookahead in a pattern rule, a key only differing by case, and the date's first key on another field.
        var refused = await PublishNext(3, d =>
        {
            RenameDate(d, "dateOfReturn");
            d["rules"]!.AsArray().Add(JsonNode.Parse("""{ "id": "r_rma", "kind": "pattern", "params": { "path": "returnNumber", "pattern": "(?=RMA)RMA-[0-9]+" } }"""));
            d["fields"]!.AsArray().Add(JsonNode.Parse("""{ "id": "fld_rn2", "key": "returnnumber", "kind": "identifier" }"""));
            d["fields"]!.AsArray().Add(JsonNode.Parse("""{ "id": "fld_new", "key": "returnDate", "kind": "text" }"""));
        });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var report = (await refused.Content.ReadFromJsonAsync<PublishReportDto>(ApiFactory.Json, Ct))!;
        report.Problems.ShouldContain(p => p.Where == "rules[2] (r_rma).params" && p.Message.Contains("not a valid regular expression", StringComparison.Ordinal));
        report.Problems.ShouldContain(p => p.Message.Contains("differs from the key 'returnNumber' at this level only by case", StringComparison.Ordinal));
        report.Problems.ShouldContain(p => p.Where == "returnDate" && p.Message.Contains("'fld_rd' (now 'dateOfReturn') had in version 1", StringComparison.Ordinal));

        var fixedV3 = await PublishNext(3, d =>
        {
            d["rules"]!.AsArray().Remove(d["rules"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == "r_rma"));
            d["fields"]!.AsArray().Remove(FieldOf(d, "fld_rn2"));
            d["fields"]!.AsArray().Remove(FieldOf(d, "fld_new"));
        }, revision: 1);
        fixedV3.StatusCode.ShouldBe(HttpStatusCode.OK, await fixedV3.Content.ReadAsStringAsync(Ct));
        var descriptors = factory.Query(db => db.DocumentTypeVersions.Single(v => v.TenantId == tenant && v.Version == 3).DescriptorsJson).ShouldNotBeNull();
        descriptors.ShouldContain("\"formerKeys\":[\"returnDate\",\"returnedOn\"]");

        // The answers are the same as before; the catalogue reads the versions as summaries.
        var type = (await admin.GetFromJsonAsync<DocumentTypeDetail>("/api/v1/document-types/return-slip", ApiFactory.Json, Ct))!;
        type.Versions.Select(v => (v.Version, v.State)).ShouldBe([(1, "Deprecated"), (2, "Deprecated"), (3, "Published")]);
        type.IndexedFields.Select(f => f.Path).ShouldBe(["returnNumber"]);
        (await admin.GetFromJsonAsync<List<DocumentTypeDetail>>("/api/v1/document-types", ApiFactory.Json, Ct))!.Single().Versions.Count.ShouldBe(3);
        (await admin.GetFromJsonAsync<List<DocumentTypeVersionSummary>>("/api/v1/document-types/return-slip/versions", ApiFactory.Json, Ct))!.Select(v => v.Version).ShouldBe([1, 2, 3]);
        var v1 = (await admin.GetFromJsonAsync<DocumentTypeVersionDetail>("/api/v1/document-types/return-slip/versions/1?include=descriptors", ApiFactory.Json, Ct))!;
        v1.Definition.GetProperty("fields")[1].GetProperty("key").GetString().ShouldBe("returnDate");
        v1.Descriptors.ShouldNotBeNull();

        await using var db = factory.PlatformDb();
        var catalogue = new DocumentTypeCatalogue(db, factory.Clock);
        var listed = (await catalogue.ListAsync(tenant, Ct)).Single();
        listed.Versions.Where(v => v.Version < 3).ShouldAllBe(v => v.DefinitionJson == null, "only the version the indexed fields come from is read");
        listed.IndexedFieldsVersion.ShouldNotBeNull().Version.ShouldBe(3);
        listed.IndexedFieldsVersion.DefinitionJson.ShouldNotBeNull();
        (await catalogue.ListKeysAsync(tenant, Ct)).ShouldBe([new CatalogueTypeKey("return-slip", DefinitionOrigin.Custom, "std.generic")]);
        (await catalogue.FindKeyAsync(tenant, "nope", Ct)).ShouldBeNull();
        (await catalogue.FindVersionAsync(tenant, "return-slip", 2, Ct)).ShouldNotBeNull().CompiledJsonSchema.ShouldNotBeNull();
    }

    private static object Adopt(string template) => new { from = new { template, mode = "adopt" } };

    private static Task<HttpResponseMessage> Put(HttpClient client, string key, int version, JsonObject definition, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/document-types/{key}/versions/{version}")
        {
            Content = new StringContent(definition.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
        }

        return client.SendAsync(request, Ct);
    }

    private void SetFeatures(Guid tenant, bool configurable, bool custom = false) => factory.Run(db =>
    {
        var row = db.Tenants.Single(t => t.Id == tenant);
        row.Settings.Features!.ConfigurableTypes = configurable;
        row.Settings.Features.CustomTypes = custom;
    });

    private void Seed(Guid tenant)
    {
        using var db = factory.PlatformDb();
        TypeCatalogueSeeder.AddMissingAdoptedTypesAsync(db, tenant, factory.Clock.GetUtcNow(), Ct).GetAwaiter().GetResult();
        db.SaveChanges();
    }

    private async Task<EffectiveType?> ResolveAsync(Guid tenant, string key, Guid? pinned = null)
    {
        await using var db = factory.PlatformDb();
        var resolver = new CatalogueConfigResolver(db, new EfTenantFeatures(db), new PublishedDefinitionCache());
        return (await resolver.ResolveAsync(new EffectiveConfigRequest(tenant, DocumentTypeKey.Parse(key), PinnedVersionId: pinned), Ct)).Type;
    }

    /// <summary>The return slip of docs/11 §5.4 (with the return number indexed, as its duplicate key needs nothing else).</summary>
    internal static string ReturnSlip(int version = 1) => $$$"""
        {"key":"return-slip","version":{{{version}}},"base":{"template":"std.generic","version":1,"mode":"copy"},
         "name":{"en":"Return slip","de":"Retourenschein"},
         "roles":{"documentNumber":"returnNumber","documentDate":"returnDate","supplierParty":"supplier","lineItems":"items","duplicateKey":["$supplierId","returnNumber"]},
         "fields":[
           {"id":"fld_rn","key":"returnNumber","kind":"identifier","required":true,"indexed":true,"extraction":{"labels":{"de":["Retouren-Nr.","RMA"] } } },
           {"id":"fld_rd","key":"returnDate","kind":"date","required":true},
           {"id":"fld_sup","key":"supplier","kind":"party"},
           {"id":"fld_dn","key":"deliveryNoteReference","kind":"identifier"},
           {"id":"fld_items","key":"items","kind":"array","anchor":"description","item":{"fields":[
             {"id":"fld_i_sku","key":"supplierSku","kind":"identifier"},
             {"id":"fld_i_desc","key":"description","kind":"text","required":true},
             {"id":"fld_i_qty","key":"quantity","kind":"decimal","min":0},
             {"id":"fld_i_reason","key":"reason","kind":"enum","options":[{"code":"Damaged"},{"code":"Wrong"},{"code":"Surplus"}]}
           ] } }
         ],
         "rules":[{"id":"r_dup","kind":"duplicate"},{"id":"r_qty","kind":"range","params":{"path":"items[*].quantity","min":0.001} }],
         "processing":{"extract":[{"id":"map","kind":"supplier-mapping"},{"id":"llm","kind":"llm","params":{"mode":"fill-missing"} }],"review":{"autoApprove":false} },
         "classification":{"description":"Goods returned to a supplier","keywords":{"de":["Retourenschein","Rücksendung"] } } }
        """;
}
