using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Api.Tests;

/// <summary>
/// The outputs API of docs/11 §18 (P3 part A): platform templates and their copies, mappings with versions, draft
/// edits with If-Match and publish, the server-side preview over a document's values view, destinations with a
/// write-only secret, bindings, lookup tables; the roles and the <c>outputs</c> switch that gate them; tenant isolation.
/// </summary>
public sealed class OutputEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid OutputsTenant(string name)
    {
        var tenant = factory.CreateTenant(name);
        factory.Run(db => db.Tenants.Single(t => t.Id == tenant).Settings.Features!.Outputs = true);
        return tenant;
    }

    private static HttpRequestMessage Put(string url, object body, int revision) => new(HttpMethod.Put, url)
    {
        Content = JsonContent.Create(body),
        Headers = { IfMatch = { new EntityTagHeaderValue($"\"{revision}\"") } },
    };

    [Fact]
    public async Task Everything_needs_the_outputs_switch_and_changes_need_IntegrationAdmin()
    {
        var tenant = factory.CreateTenant("Outputs off");
        using var admin = factory.TenantAdminOf(tenant);
        var off = await admin.GetAsync("/api/v1/output-mappings", Ct);
        off.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await off.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail!.ShouldContain("features.outputs");
        (await admin.GetAsync("/api/v1/destinations", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.GetAsync("/api/v1/output-bindings", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.GetAsync("/api/v1/output-templates", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.GetAsync("/api/v1/lookup-tables", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        factory.Run(db => db.Tenants.Single(t => t.Id == tenant).Settings.Features!.Outputs = true);
        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], tenant.ToString()));
        (await reviewer.GetAsync("/api/v1/output-mappings", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "reading needs Read");
        (await reviewer.PostAsJsonAsync("/api/v1/output-mappings", new { from = new { template = "generic-ap-json" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reviewer.GetAsync("/api/v1/destinations", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "destinations hold integration details");
        (await reviewer.PostAsJsonAsync("/api/v1/output-mappings:preview", new { expression = "1", sampleValues = new { } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync("/api/v1/output-mappings", new { from = new { template = "generic-ap-json" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_platform_templates_are_listed_read_only_and_copied_as_a_draft()
    {
        var tenant = OutputsTenant("Templates");
        using var admin = factory.TenantAdminOf(tenant);

        var templates = (await admin.GetFromJsonAsync<List<OutputTemplateSummary>>("/api/v1/output-templates", ApiFactory.Json, Ct))!;
        templates.Select(t => t.Key).ShouldBe(["generic-ap-json", "std.event"]);
        templates.ShouldAllBe(t => t.CopiedAs.Count == 0 && t.Engine == "jsonata");

        var listed = (await admin.GetFromJsonAsync<List<OutputMappingDetail>>("/api/v1/output-mappings", ApiFactory.Json, Ct))!;
        listed.Where(m => m.Origin == "Platform").Select(m => m.Key).ShouldBe(["generic-ap-json", "std.event"], ignoreOrder: true);
        listed.ShouldAllBe(m => m.PublishedVersion == 1);
        (await admin.PatchAsJsonAsync("/api/v1/output-mappings/std.event", new { name = "Mine" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a template is copied, not changed");
        (await admin.PostAsync("/api/v1/output-mappings/std.event/versions", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var created = await admin.PostAsJsonAsync("/api/v1/output-mappings", new { from = new { template = "generic-ap-json" }, key = "erp-ap" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.ToString().ShouldBe("/api/v1/output-mappings/erp-ap");
        var copy = (await created.Content.ReadFromJsonAsync<OutputMappingDetail>(ApiFactory.Json, Ct))!;
        copy.Origin.ShouldBe("Copied");
        copy.BaseTemplate.ShouldBe("generic-ap-json");
        copy.PublishedVersion.ShouldBeNull();
        copy.DraftVersion.ShouldBe(1);
        var draft = (await admin.GetFromJsonAsync<OutputMappingVersionDetail>("/api/v1/output-mappings/erp-ap/versions/1", ApiFactory.Json, Ct))!;
        draft.Expression.ShouldContain("roles.documentNumber");
        draft.TargetSchema.ShouldNotBeNull();
        (await admin.GetFromJsonAsync<List<OutputTemplateSummary>>("/api/v1/output-templates", ApiFactory.Json, Ct))!.Single(t => t.Key == "generic-ap-json").CopiedAs.ShouldBe(["erp-ap"]);

        (await admin.PostAsJsonAsync("/api/v1/output-mappings", new { from = new { template = "generic-ap-json" }, key = "erp-ap" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/v1/output-mappings", new { from = new { template = "sap-s4" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/output-mappings", new { from = new { template = "std.event" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "std. keys are the platform's");
        (await admin.PostAsJsonAsync("/api/v1/output-mappings", new { from = new { template = "std.event" }, key = "event" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_draft_is_saved_with_if_match_and_published_once_it_parses()
    {
        var tenant = OutputsTenant("Drafts");
        using var admin = factory.TenantAdminOf(tenant);
        (await admin.PostAsJsonAsync("/api/v1/output-mappings", new { key = "article-price-import", name = "Article prices", sourceTypeKey = "price-list", expression = "{ \"a\": " }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var broken = await admin.PostAsync("/api/v1/output-mappings/article-price-import/versions/1:publish", null, Ct);
        broken.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var report = (await broken.Content.ReadFromJsonAsync<OutputMappingCheckReport>(ApiFactory.Json, Ct))!;
        report.Errors.ShouldHaveSingleItem().Kind.ShouldBe("syntax");

        var body = new { engine = "jsonata", expression = "{ \"vendorNo\": supplier.attributes.erpVendorId, \"rows\": [ doc.lines.{ \"sku\": supplierSku } ] }", targetSchema = new { type = "object", required = new[] { "vendorNo" } } };
        var noMatch = new HttpRequestMessage(HttpMethod.Put, "/api/v1/output-mappings/article-price-import/versions/1") { Content = JsonContent.Create(body) };
        (await admin.SendAsync(noMatch, Ct)).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        (await admin.SendAsync(Put("/api/v1/output-mappings/article-price-import/versions/1", body, 7), Ct)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var saved = await admin.SendAsync(Put("/api/v1/output-mappings/article-price-import/versions/1", body, 0), Ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        saved.Headers.ETag!.Tag.ShouldBe("\"1\"");
        (await admin.SendAsync(Put("/api/v1/output-mappings/article-price-import/versions/1", new { engine = "liquid", expression = "x" }, 1), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "liquid comes in P6");

        var published = await admin.PostAsJsonAsync("/api/v1/output-mappings/article-price-import/versions/1:publish", new { changeNote = "First" }, Ct);
        published.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await published.Content.ReadFromJsonAsync<OutputMappingDetail>(ApiFactory.Json, Ct))!.PublishedVersion.ShouldBe(1);
        var v1 = (await admin.GetFromJsonAsync<OutputMappingVersionDetail>("/api/v1/output-mappings/article-price-import/versions/1", ApiFactory.Json, Ct))!;
        v1.Version.State.ShouldBe("Published");
        v1.ReferencedPaths.ShouldBe(["doc", "doc.lines", "doc.lines.supplierSku", "supplier", "supplier.attributes", "supplier.attributes.erpVendorId"]);
        (await admin.SendAsync(Put("/api/v1/output-mappings/article-price-import/versions/1", body, 1), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "a published version never changes");

        (await admin.PostAsync("/api/v1/output-mappings/article-price-import/versions", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PostAsync("/api/v1/output-mappings/article-price-import/versions", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "one draft at a time");
        (await admin.PostAsync("/api/v1/output-mappings/article-price-import/versions/2:publish", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var mapping = (await admin.GetFromJsonAsync<OutputMappingDetail>("/api/v1/output-mappings/article-price-import", ApiFactory.Json, Ct))!;
        mapping.PublishedVersion.ShouldBe(2);
        mapping.Versions.Select(v => v.State).ShouldBe(["Deprecated", "Published"]);

        (await admin.PatchAsJsonAsync("/api/v1/output-mappings/article-price-import", new { name = "Article price import", sourceTypeKey = "" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetFromJsonAsync<OutputMappingDetail>("/api/v1/output-mappings/article-price-import", ApiFactory.Json, Ct))!.SourceTypeKey.ShouldBeNull();
    }

    [Fact]
    public async Task The_preview_renders_a_template_over_a_documents_values_view_on_the_server()
    {
        var tenant = OutputsTenant("Preview");
        using var admin = factory.TenantAdminOf(tenant);
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.Approved, SupplierName = "Nordwind Großhandel GmbH" });
        factory.Run(db => db.Suppliers.Single(s => s.TenantId == tenant).AttributesJson = """{"erpVendorId":"V-10042"}""");
        var template = factory.Query(db => db.OutputMappingVersions.Single(v => v.TenantId == null && v.Mapping!.Key == "generic-ap-json").Id);

        var response = await admin.PostAsJsonAsync("/api/v1/output-mappings:preview", new { mappingVersionId = template, documentId = seeded.DocumentId, includeValues = true }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = (await response.Content.ReadFromJsonAsync<OutputPreviewResponse>(ApiFactory.Json, Ct))!;
        preview.Errors.ShouldBeEmpty();
        preview.SchemaErrors.ShouldBeEmpty();
        var header = preview.Output!.Value.GetProperty("header");
        header.GetProperty("documentNumber").GetString().ShouldBe("RE-2026-0042");
        header.GetProperty("documentType").GetString().ShouldBe("Invoice");
        header.GetProperty("gross").GetDecimal().ShouldBe(166.50m);
        header.GetProperty("supplier").GetProperty("attributes").GetProperty("erpVendorId").GetString().ShouldBe("V-10042");
        header.GetProperty("entity").GetProperty("name").GetString().ShouldBe(factory.Query(db => db.Tenants.Single(t => t.Id == tenant).LegalName));
        header.GetProperty("event").GetString().ShouldBe("document.approved");
        var rows = preview.Output.Value.GetProperty("rows");
        rows.GetArrayLength().ShouldBe(2);
        rows[0].GetProperty("net").GetDecimal().ShouldBe(100.00m);
        rows[1].GetProperty("quantity").GetDecimal().ShouldBe(5m);
        preview.ReferencedPaths.ShouldContain("roles.documentNumber");
        var values = preview.Values!.Value;
        values.GetProperty("source").GetProperty("fileName").GetString().ShouldBe("invoice.pdf");
        values.GetProperty("source").GetProperty("channel").GetString().ShouldBe("API upload");
        values.GetProperty("links").GetProperty("api").GetString().ShouldBe($"/api/v1/documents/{seeded.DocumentId}");

        // A tenant's own expression with a schema and $required, over the same document and over sample values.
        var required = await admin.PostAsJsonAsync("/api/v1/output-mappings:preview", new { expression = "{ \"v\": $required(supplier.attributes.costCentre, \"Supplier has no cost centre\") }", documentId = seeded.DocumentId }, Ct);
        var failed = (await required.Content.ReadFromJsonAsync<OutputPreviewResponse>(ApiFactory.Json, Ct))!;
        failed.Errors.ShouldHaveSingleItem().Kind.ShouldBe("required");
        failed.Errors[0].Message.ShouldBe("Supplier has no cost centre");
        failed.Output.ShouldBeNull();

        var schema = await admin.PostAsJsonAsync("/api/v1/output-mappings:preview", new { expression = "{ \"n\": roles.documentNumber }", targetSchema = new { type = "object", properties = new { n = new { type = "integer" } } }, sampleValues = new { roles = new { documentNumber = "GS-1" } } }, Ct);
        var invalid = (await schema.Content.ReadFromJsonAsync<OutputPreviewResponse>(ApiFactory.Json, Ct))!;
        invalid.Errors.ShouldBeEmpty();
        invalid.SchemaErrors.ShouldHaveSingleItem().Path.ShouldBe("n");
        invalid.Output!.Value.GetProperty("n").GetString().ShouldBe("GS-1");

        var passthrough = (await (await admin.PostAsJsonAsync("/api/v1/output-mappings:preview", new { engine = "passthrough", documentId = seeded.DocumentId }, Ct)).Content.ReadFromJsonAsync<OutputPreviewResponse>(ApiFactory.Json, Ct))!;
        passthrough.Output!.Value.GetProperty("meta").GetProperty("documentId").GetString().ShouldBe(seeded.DocumentId.ToString());

        (await admin.PostAsJsonAsync("/api/v1/output-mappings:preview", new { expression = "1", documentId = seeded.DocumentId, sampleValues = new { } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/output-mappings:preview", new { expression = "1", documentId = Guid.NewGuid() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Table_in_a_preview_reads_the_tenants_lookup_table()
    {
        var tenant = OutputsTenant("Lookups");
        using var admin = factory.TenantAdminOf(tenant);
        (await admin.PostAsJsonAsync("/api/v1/lookup-tables", new { key = "erp-uom", description = "Units of the ERP" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var entries = new { entries = new[] { new { from = "KAR", to = "CT" }, new { from = "Kiste", to = "CR" } } };
        (await admin.SendAsync(Put("/api/v1/lookup-tables/erp-uom/entries", entries, 3), Ct)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await admin.SendAsync(Put("/api/v1/lookup-tables/erp-uom/entries", entries, 0), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var table = (await admin.GetFromJsonAsync<LookupTableDetail>("/api/v1/lookup-tables/erp-uom", ApiFactory.Json, Ct))!;
        table.Entries.Select(e => e.From).ShouldBe(["KAR", "Kiste"]);
        table.Table.Revision.ShouldBe(1);
        (await admin.GetFromJsonAsync<List<LookupTableSummary>>("/api/v1/lookup-tables", ApiFactory.Json, Ct))!.Select(t => (t.Key, t.Platform)).ShouldBe([("erp-uom", false), ("uom", true)]);

        var preview = (await (await admin.PostAsJsonAsync("/api/v1/output-mappings:preview", new { expression = "{ \"a\": $table(\"erp-uom\", \"kar\"), \"b\": $table(\"erp-uom\", \"?\", \"ST\"), \"c\": $table(\"uom\", \"kg\") }", sampleValues = new { } }, Ct))
            .Content.ReadFromJsonAsync<OutputPreviewResponse>(ApiFactory.Json, Ct))!;
        preview.Errors.ShouldBeEmpty();
        preview.Output!.Value.GetRawText().ShouldBe("""{"a":"CT","b":"ST","c":"KGM"}""");
        preview.Tables.ShouldBe(["erp-uom", "uom"]);

        // Another tenant neither sees nor reads the table.
        var other = OutputsTenant("Lookups other");
        using var otherAdmin = factory.TenantAdminOf(other);
        (await otherAdmin.GetAsync("/api/v1/lookup-tables/erp-uom", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var theirs = (await (await otherAdmin.PostAsJsonAsync("/api/v1/output-mappings:preview", new { expression = "$table(\"erp-uom\", \"KAR\")", sampleValues = new { } }, Ct)).Content.ReadFromJsonAsync<OutputPreviewResponse>(ApiFactory.Json, Ct))!;
        theirs.Errors.ShouldHaveSingleItem().Kind.ShouldBe("function");
    }

    [Fact]
    public async Task A_destinations_secret_is_write_only_and_rotates_with_an_overlap()
    {
        var tenant = OutputsTenant("Destinations");
        using var admin = factory.TenantAdminOf(tenant);
        var config = new { method = "POST", urlTemplate = "https://erp.example/api/purchase-invoices", headers = new Dictionary<string, string> { ["X-Client"] = "docproc" }, auth = new { type = "bearer" } };

        var created = await admin.PostAsJsonAsync("/api/v1/destinations", new { name = "erp-ap", transport = "http", config, secret = "s3cret-token" }, Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var text = await created.Content.ReadAsStringAsync(Ct);
        text.ShouldNotContain("s3cret-token");
        var destination = JsonSerializer.Deserialize<DestinationResponse>(text, ApiFactory.Json)!;
        destination.HasSecret.ShouldBeTrue();
        destination.HasPreviousSecret.ShouldBeFalse();
        destination.Config.GetProperty("urlTemplate").GetString().ShouldBe("https://erp.example/api/purchase-invoices");
        var secrets = factory.Services.GetRequiredService<ISecretStore>();
        var reference = factory.Query(db => db.OutputDestinations.Single(d => d.Id == destination.Id).SecretRef)!;
        reference.ShouldStartWith($"tenant/{tenant}/destination/{destination.Id}/");
        (await secrets.GetAsync(reference, Ct)).ShouldBe("s3cret-token");

        var rotated = await admin.PutAsJsonAsync($"/api/v1/destinations/{destination.Id}/secret", new { secret = "new-token" }, Ct);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await rotated.Content.ReadFromJsonAsync<DestinationResponse>(ApiFactory.Json, Ct))!.HasPreviousSecret.ShouldBeTrue();
        var row = factory.Query(db => db.OutputDestinations.Single(d => d.Id == destination.Id));
        (await secrets.GetAsync(row.SecretRef!, Ct)).ShouldBe("new-token");
        (await secrets.GetAsync(row.PreviousSecretRef!, Ct)).ShouldBe("s3cret-token", "the replaced secret stays for the overlap");

        (await admin.PostAsJsonAsync("/api/v1/destinations", new { name = "erp-ap", transport = "pull" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/v1/destinations", new { name = "drop", transport = "blob", config = new { path = "exports/ap", fileNameTemplate = "{{meta.documentId}}.json" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PatchAsJsonAsync($"/api/v1/destinations/{destination.Id}", new { status = "Disabled" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("http", """{"urlTemplate":"https://erp.example/x","headers":{"Authorization":"Bearer abc"}}""", "config.headers.Authorization")]
    [InlineData("http", """{"urlTemplate":"https://user:pw@erp.example/x"}""", "config.urlTemplate")]
    [InlineData("http", """{"urlTemplate":"ftp://erp.example/x"}""", "config.urlTemplate")]
    [InlineData("http", """{"urlTemplate":"https://erp.example/x","method":"DELETE"}""", "config.method")]
    [InlineData("http", """{"urlTemplate":"https://erp.example/x","password":"x"}""", "config.password")]
    [InlineData("http", """{"urlTemplate":"https://erp.example/x","auth":{"type":"apiKeyHeader"}}""", "config.auth.headerName")]
    [InlineData("http", """{"urlTemplate":"https://erp.example/x","timeoutSeconds":600}""", "config.timeoutSeconds")]
    [InlineData("blob", """{"path":"../etc","fileNameTemplate":"a.json"}""", "config.path")]
    [InlineData("sftp", """{}""", "transport")]
    [InlineData("ftp", """{}""", "transport")]
    public async Task A_destination_config_is_checked_per_transport(string transport, string config, string field)
    {
        var tenant = OutputsTenant($"Config {transport} {field}");
        using var admin = factory.TenantAdminOf(tenant);

        var response = await admin.PostAsJsonAsync("/api/v1/destinations", new { name = "d", transport, config = JsonDocument.Parse(config).RootElement }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldContain(field);
    }

    [Fact]
    public async Task A_response_mapping_is_compiled_when_the_destination_is_saved()
    {
        var tenant = OutputsTenant("Response mapping");
        using var admin = factory.TenantAdminOf(tenant);
        object Config(string mapping) => new { urlTemplate = "https://erp.example/x", responseMapping = mapping };

        foreach (var bad in new[] { "this is ((( not jsonata", new string('-', 7999) + "1", new string('[', 3000) + "1" + new string(']', 3000) })
        {
            var refused = await admin.PostAsJsonAsync("/api/v1/destinations", new { name = "d", transport = "http", config = Config(bad) }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, bad[..Math.Min(bad.Length, 30)]);
            (await refused.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors["config.responseMapping"].ShouldHaveSingleItem().ShouldContain("not a JSONata expression");
        }

        var created = await admin.PostAsJsonAsync("/api/v1/destinations", new { name = "d", transport = "http", config = Config("erpId") }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var destination = (await created.Content.ReadFromJsonAsync<DestinationResponse>(ApiFactory.Json, Ct))!;

        var patched = await admin.PatchAsJsonAsync($"/api/v1/destinations/{destination.Id}", new { config = Config("{ ") }, Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await patched.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldContain("config.responseMapping");
        (await admin.PatchAsJsonAsync($"/api/v1/destinations/{destination.Id}", new { config = Config("id.value") }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_binding_refers_to_the_tenants_mapping_destination_and_scope()
    {
        var tenant = OutputsTenant("Bindings");
        using var admin = factory.TenantAdminOf(tenant);
        var destination = (await (await admin.PostAsJsonAsync("/api/v1/destinations", new { name = "erp-articles", transport = "pull" }, Ct)).Content.ReadFromJsonAsync<DestinationResponse>(ApiFactory.Json, Ct))!;

        var created = await admin.PostAsJsonAsync("/api/v1/output-bindings", new
        {
            name = "article-prices",
            documentTypeKey = "price-list",
            mappingKey = "generic-ap-json",
            destinationId = destination.Id,
            condition = "roles.validUntil >= $today()",
            onReapproval = "Skip",
        }, Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var binding = (await created.Content.ReadFromJsonAsync<OutputBindingResponse>(ApiFactory.Json, Ct))!;
        binding.Events.ShouldBe(["document.approved"]);
        binding.MappingKey.ShouldBe("generic-ap-json");
        binding.Mode.ShouldBe("PerDocument");
        binding.OnReapproval.ShouldBe("Skip");
        (await admin.GetFromJsonAsync<List<OutputBindingResponse>>("/api/v1/output-bindings", ApiFactory.Json, Ct))!.ShouldHaveSingleItem().Id.ShouldBe(binding.Id);

        async Task<string[]> Refused(object body)
        {
            var response = await admin.PostAsJsonAsync("/api/v1/output-bindings", body, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            return [.. (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys];
        }

        (await Refused(new { name = "b", mappingKey = "generic-ap-json", destinationId = destination.Id, condition = "roles.validUntil >= " })).ShouldContain("condition");
        (await Refused(new { name = "b", mappingKey = "generic-ap-json", destinationId = destination.Id, mode = "Batch" })).ShouldContain("mode");
        (await Refused(new { name = "b", mappingKey = "nothing", destinationId = destination.Id })).ShouldContain("mappingKey");
        (await Refused(new { name = "b", mappingKey = "generic-ap-json", destinationId = Guid.NewGuid() })).ShouldContain("destinationId");
        (await Refused(new { name = "b", mappingKey = "generic-ap-json", destinationId = destination.Id, events = new[] { "document.deleted" } })).ShouldContain("events");
        (await Refused(new { name = "b", mappingKey = "generic-ap-json", destinationId = destination.Id, pinnedMappingVersion = 9 })).ShouldContain("pinnedMappingVersion");
        (await Refused(new { name = "b", mappingKey = "generic-ap-json", destinationId = destination.Id, onReapproval = "Update" })).ShouldContain("updateMappingKey");
        (await Refused(new { name = "b", mappingKey = "generic-ap-json", destinationId = destination.Id, supplierId = Guid.NewGuid() })).ShouldContain("supplierId");

        (await admin.PatchAsJsonAsync($"/api/v1/output-bindings/{binding.Id}", new { enabled = false, condition = "" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var patched = (await admin.GetFromJsonAsync<OutputBindingResponse>($"/api/v1/output-bindings/{binding.Id}", ApiFactory.Json, Ct))!;
        patched.Enabled.ShouldBeFalse();
        patched.Condition.ShouldBeNull();
        (await admin.DeleteAsync($"/api/v1/destinations/{destination.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "a binding refers to it");
        (await admin.DeleteAsync($"/api/v1/output-bindings/{binding.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"/api/v1/destinations/{destination.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task One_tenant_never_sees_or_uses_another_tenants_outputs()
    {
        var a = OutputsTenant("Isolation A");
        var b = OutputsTenant("Isolation B");
        using var adminA = factory.TenantAdminOf(a);
        using var adminB = factory.TenantAdminOf(b);
        (await adminA.PostAsJsonAsync("/api/v1/output-mappings", new { key = "mine", name = "Mine", expression = "meta.documentId" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await adminA.PostAsync("/api/v1/output-mappings/mine/versions/1:publish", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var destination = (await (await adminA.PostAsJsonAsync("/api/v1/destinations", new { name = "a-feed", transport = "pull" }, Ct)).Content.ReadFromJsonAsync<DestinationResponse>(ApiFactory.Json, Ct))!;
        var binding = (await (await adminA.PostAsJsonAsync("/api/v1/output-bindings", new { name = "a", mappingKey = "mine", destinationId = destination.Id }, Ct)).Content.ReadFromJsonAsync<OutputBindingResponse>(ApiFactory.Json, Ct))!;
        var documentA = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = a });
        var versionA = factory.Query(db => db.OutputMappingVersions.Single(v => v.TenantId == a).Id);

        (await adminB.GetAsync("/api/v1/output-mappings/mine", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.GetFromJsonAsync<List<OutputMappingDetail>>("/api/v1/output-mappings", ApiFactory.Json, Ct))!.ShouldAllBe(m => m.Origin == "Platform");
        (await adminB.GetAsync($"/api/v1/destinations/{destination.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.GetFromJsonAsync<List<DestinationResponse>>("/api/v1/destinations", ApiFactory.Json, Ct))!.ShouldBeEmpty();
        (await adminB.GetAsync($"/api/v1/output-bindings/{binding.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.PatchAsJsonAsync($"/api/v1/output-bindings/{binding.Id}", new { enabled = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.PutAsJsonAsync($"/api/v1/destinations/{destination.Id}/secret", new { secret = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var bound = await adminB.PostAsJsonAsync("/api/v1/output-bindings", new { name = "steal", mappingKey = "mine", destinationId = destination.Id }, Ct);
        bound.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bound.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldBe(["destinationId", "mappingKey"], ignoreOrder: true);
        (await adminB.PostAsJsonAsync("/api/v1/output-mappings:preview", new { expression = "meta.documentId", documentId = documentA.DocumentId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.PostAsJsonAsync("/api/v1/output-mappings:preview", new { mappingVersionId = versionA, sampleValues = new { } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Supplier_attributes_are_set_by_a_tenant_admin_and_refused_from_review()
    {
        var tenant = factory.CreateTenant("Attributes");
        using var admin = factory.TenantAdminOf(tenant);
        var created = await admin.PostAsJsonAsync("/api/v1/suppliers", new { name = "Nordwind Großhandel GmbH", attributes = new { erpVendorId = "V-10042", paymentBlock = false } }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var supplier = (await created.Content.ReadFromJsonAsync<SupplierResponse>(ApiFactory.Json, Ct))!;
        supplier.Attributes["erpVendorId"].GetString().ShouldBe("V-10042");
        supplier.Attributes["paymentBlock"].GetBoolean().ShouldBeFalse();

        var patched = await admin.PatchAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new { attributes = new { erpVendorId = "V-2" } }, Ct);
        (await patched.Content.ReadFromJsonAsync<SupplierResponse>(ApiFactory.Json, Ct))!.Attributes.Keys.ShouldBe(["erpVendorId"]);
        (await admin.PatchAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new { attributes = new { nested = new { a = 1 } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PatchAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new { attributes = new Dictionary<string, string> { ["1bad"] = "x" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PatchAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new { name = "Nordwind Großhandel" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Query(db => db.Suppliers.Single(s => s.Id == supplier.Id).AttributesJson).ShouldBe("""{"erpVendorId":"V-2"}""", "a write without attributes leaves them");

        var document = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, SupplierName = null });
        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], tenant.ToString()));
        var fromReview = await reviewer.PostAsJsonAsync($"/api/v1/documents/{document.DocumentId}:create-supplier", new { name = "Südhof Feinkost", attributes = new { erpVendorId = "V-9" } }, Ct);
        fromReview.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
