using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace DocProc.Api.Tests;

/// <summary>
/// The checks of a configuration bundle import (docs/11 §15.4, P4 review fixes): a part whose feature is off for the
/// tenant is reported and not imported; output mappings pass the checks of <c>POST /output-mappings</c>; an apply that
/// fails part-way leaves nothing behind; a hand-posted layout exported and imported again is skipped (its mapping
/// compared as JSON); a folder rule carries its channel; and outside Development no bundle is signed with the public
/// development key.
/// </summary>
public sealed class GovernanceImportChecksTests : IClassFixture<ApiFactory>
{
    private const string Rule = """{ "all": [ { "text": "Rechnung", "page": 1, "region": "title" }, { "supplierValue": "vatId", "page": 1 } ] }""";

    private readonly ApiFactory _factory;

    public GovernanceImportChecksTests(ApiFactory factory)
    {
        _factory = factory;

        // Tenant A has every part's feature on; tenant B has them all off (as a tenant before P1).
        _factory.Run(db =>
        {
            var features = db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.Features!;
            features.ConfigurableTypes = true;
            features.CustomTypes = true;
            features.SupplierLayouts = true;
            features.SourceRouting = true;
            features.Outputs = true;
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Admin(Guid tenant) => _factory.ClientFor(new TokenSpec("tenant-admin", [Roles.TenantAdmin], tenant.ToString()));

    private static JsonObject Signed(JsonObject parts)
    {
        var bundle = new JsonObject { ["format"] = GovernanceEndpoints.BundleFormat, ["version"] = GovernanceEndpoints.BundleVersion };
        foreach (var (name, value) in parts)
        {
            bundle[name] = value?.DeepClone();
        }

        bundle["signature"] = GovernanceEndpoints.Sign(bundle, GovernanceEndpoints.DevelopmentSigningKey);
        return bundle;
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, JsonObject bundle, string mode = "apply") =>
        client.PostAsJsonAsync("/api/v1/config/bundle:import", new BundleImportRequest(JsonDocument.Parse(bundle.ToJsonString()).RootElement, mode), Ct);

    private Supplier AddSupplier(Guid tenant, string vat)
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(), TenantId = tenant, Name = $"Nordwind {vat}", NormalizedName = $"NORDWIND {vat}", VatId = vat, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        _factory.Run(db => db.Suppliers.Add(supplier));
        return supplier;
    }

    private static string NewVat() => $"DE{Random.Shared.Next(100_000_000, 999_999_999)}";

    private static JsonObject Everything(string vat, string typeKey, string outputKey) => new()
    {
        ["types"] = new JsonArray(new JsonObject { ["key"] = typeKey, ["version"] = 1, ["definition"] = JsonNode.Parse(DocumentTypeEndpointsTests.ReturnSlip().Replace("\"return-slip\"", $"\"{typeKey}\"", StringComparison.Ordinal)) }),
        ["layouts"] = new JsonArray(new JsonObject
        {
            ["layoutKey"] = "nordwind-off",
            ["documentTypeKey"] = "Invoice",
            ["supplier"] = new JsonObject { ["vatId"] = vat },
            ["identification"] = JsonNode.Parse(Rule),
            ["mapping"] = JsonNode.Parse("""{ "fields": { "currency": { "rules": [ { "strategy": "constant", "value": "EUR" } ] } } }"""),
        }),
        ["sourceRules"] = new JsonArray(new JsonObject { ["supplier"] = new JsonObject { ["vatId"] = vat }, ["sender"] = $"rechnung@{vat.ToLowerInvariant()}.example" }),
        ["outputs"] = new JsonArray(new JsonObject { ["key"] = outputKey, ["name"] = "ERP", ["engine"] = "jsonata", ["expression"] = "{ \"n\": invoiceNumber }" }),
        ["bindings"] = new JsonArray(new JsonObject { ["name"] = $"bind-{outputKey}", ["events"] = new JsonArray("document.approved"), ["mappingKey"] = outputKey, ["destination"] = new JsonObject { ["name"] = "nowhere" } }),
    };

    [Fact]
    public async Task A_tenant_with_the_features_off_gets_nothing_written_through_an_import()
    {
        var vat = NewVat();
        var supplier = AddSupplier(ApiFactory.TenantB, vat);
        using var b = Admin(ApiFactory.TenantB);
        (await b.PostAsJsonAsync("/api/v1/output-mappings", new { key = "off-output", name = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the regular endpoint refuses it");

        var response = await ImportAsync(b, Signed(Everything(vat, "probe-slip-off", "off-output")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var items = (await response.Content.ReadFromJsonAsync<BundleImportResponse>(ApiFactory.Json, Ct))!.Items;
        items.Select(i => i.Kind).ShouldBe(["type", "layout", "sourceRule", "output", "binding"]);
        items.ShouldAllBe(i => i.Action == "error" && i.Detail!.Contains("feature is off"));
        items.Single(i => i.Kind == "layout").Detail!.ShouldContain("supplierLayouts");
        items.Single(i => i.Kind == "sourceRule").Detail!.ShouldContain("sourceRouting");
        items.Single(i => i.Kind == "output").Detail!.ShouldContain("outputs");
        items.Single(i => i.Kind == "type").Detail!.ShouldContain("configurableTypes");
        _factory.Run(db =>
        {
            db.DocumentTypeDefinitions.Any(d => d.TenantId == ApiFactory.TenantB && d.Key == "probe-slip-off").ShouldBeFalse();
            db.SupplierLayouts.Any(l => l.SupplierId == supplier.Id).ShouldBeFalse();
            db.SupplierSourceRules.Any(r => r.SupplierId == supplier.Id).ShouldBeFalse();
            db.OutputMappings.Any(m => m.TenantId == ApiFactory.TenantB && m.Key == "off-output").ShouldBeFalse();
        });
    }

    [Fact]
    public async Task Output_mappings_pass_the_checks_of_the_output_mapping_endpoint()
    {
        using var a = Admin(ApiFactory.TenantA);
        var badKey = "Not a key!! " + new string('k', 100);
        var outputs = new JsonArray(
            new JsonObject { ["key"] = badKey, ["name"] = "bad key", ["expression"] = "$" },
            new JsonObject { ["key"] = "imp-engine", ["name"] = "bad engine", ["engine"] = "liquid-or-anything", ["expression"] = "$" },
            new JsonObject { ["key"] = "imp-long", ["name"] = "long", ["expression"] = new string('x', 300_000) },
            new JsonObject { ["key"] = "std.mine", ["name"] = "platform key", ["expression"] = "$" },
            new JsonObject { ["key"] = "imp-schema", ["name"] = "schema", ["expression"] = "$", ["targetSchema"] = "not a schema" },
            new JsonObject { ["key"] = "imp-ok", ["name"] = "fine", ["expression"] = "{ \"n\": invoiceNumber }", ["contentType"] = "application/json" });

        var response = await ImportAsync(a, Signed(new JsonObject { ["outputs"] = outputs }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var items = (await response.Content.ReadFromJsonAsync<BundleImportResponse>(ApiFactory.Json, Ct))!.Items;
        items.Count.ShouldBe(6);
        items[0].Action.ShouldBe("error");
        items[0].Key.Length.ShouldBeLessThanOrEqualTo(64);
        items[0].Detail!.ShouldContain("key is a letter");
        items.Single(i => i.Key == "imp-engine").Detail!.ShouldContain("engine is one of");
        items.Single(i => i.Key == "imp-long").Detail!.ShouldContain("more than");
        items.Single(i => i.Key == "std.mine").Detail!.ShouldContain("platform");
        items.Single(i => i.Key == "imp-schema").Detail!.ShouldContain("targetSchema");
        items.Single(i => i.Key == "imp-ok").Action.ShouldBe("create");
        _factory.Run(db => db.OutputMappings.Where(m => m.TenantId == ApiFactory.TenantA && (m.Key.StartsWith("imp-") || m.Key == "std.mine" || m.Key == badKey)).Select(m => m.Key).ToList().ShouldBe(["imp-ok"]));
    }

    [Fact]
    public async Task An_apply_that_fails_part_way_leaves_nothing_behind()
    {
        var vat = NewVat();
        AddSupplier(ApiFactory.TenantA, vat);
        using var a = Admin(ApiFactory.TenantA);
        var parts = new JsonObject
        {
            ["types"] = new JsonArray(new JsonObject { ["key"] = "probe-slip", ["definition"] = JsonNode.Parse(DocumentTypeEndpointsTests.ReturnSlip().Replace("\"return-slip\"", "\"probe-slip\"", StringComparison.Ordinal)) }),
            ["sourceRules"] = new JsonArray(new JsonObject { ["supplier"] = new JsonObject { ["vatId"] = vat }, ["folderPrefix"] = "lieferanten/x/", ["channel"] = "flat" }),
            ["bindings"] = new JsonArray(new JsonObject { ["name"] = "x", ["destination"] = "flat", ["mappingKey"] = 7, ["events"] = new JsonArray("document.approved") }),
        };

        var dry = await ImportAsync(a, Signed(parts), "dry-run");
        dry.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var response = await ImportAsync(a, Signed(parts));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors["bundle"].ShouldHaveSingleItem().ShouldContain("nothing was imported");
        _factory.Run(db => db.DocumentTypeDefinitions.Any(d => d.TenantId == ApiFactory.TenantA && d.Key == "probe-slip").ShouldBeFalse("the type created before the failing member is rolled back"));

        // A destination given as a string is an item error now, not a failure of the whole bundle.
        parts.Remove("sourceRules");
        var applied = await ImportAsync(a, Signed(parts));
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        var items = (await applied.Content.ReadFromJsonAsync<BundleImportResponse>(ApiFactory.Json, Ct))!.Items;
        items.Single(i => i.Kind == "type").Action.ShouldBe("create");
        items.Single(i => i.Kind == "binding").Action.ShouldBe("error");
        _factory.Run(db => db.DocumentTypeDefinitions.Any(d => d.TenantId == ApiFactory.TenantA && d.Key == "probe-slip").ShouldBeTrue());
    }

    [Fact]
    public async Task A_hand_posted_layout_and_a_folder_rule_round_trip_through_export_and_import()
    {
        var vat = NewVat();
        var supplier = AddSupplier(ApiFactory.TenantA, vat);
        using var a = Admin(ApiFactory.TenantA);

        // The layout as POST /suppliers/{id}/layouts stores it: the sender's whitespace and member order.
        var parsed = IdentificationRule.Parse(Rule);
        var handPosted = """
            {
              "fields": {
                "currency":  { "rules": [ { "value": "EUR", "strategy": "constant" } ] },
                "supplierName": { "rules": [ { "strategy": "constant", "value": "Nordwind Großhandel" } ] }
              }
            }
            """;
        var channel = new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = ApiFactory.TenantA, Name = $"Drop {vat}", Type = ChannelType.WatchedFolder, ConfigJson = $$"""{"path":"drop-{{vat.ToLowerInvariant()}}"}""", SourceKey = $"folder:tenant-a/drop-{vat.ToLowerInvariant()}/", CreatedAt = DateTimeOffset.UtcNow };
        _factory.Run(db =>
        {
            var layout = new SupplierLayout { Id = Guid.CreateVersion7(), TenantId = ApiFactory.TenantA, Scope = SupplierLayoutScope.Supplier, SupplierId = supplier.Id, OwnerId = supplier.Id, DocumentTypeKey = "Invoice", LayoutKey = "nordwind-invoice-erp", Name = "ERP", ActiveVersion = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.SupplierLayouts.Add(layout);
            db.SupplierLayoutVersions.Add(new SupplierLayoutVersion { Id = Guid.CreateVersion7(), TenantId = ApiFactory.TenantA, LayoutId = layout.Id, Version = 1, Status = SupplierLayoutVersionStatus.Active, IdentificationJson = parsed.ToJson(), IdentificationHash = parsed.Hash, MappingJson = handPosted, CreatedAt = DateTimeOffset.UtcNow });
            db.IngestionChannels.Add(channel);
        });
        var rule = await a.PostAsJsonAsync($"/api/v1/suppliers/{supplier.Id}/source-rules", new SourceRuleRequest(ChannelId: channel.Id, FolderPrefix: "lieferanten/nordwind/"), Ct);
        rule.StatusCode.ShouldBe(HttpStatusCode.Created, await rule.Content.ReadAsStringAsync(Ct));

        var adopted = await a.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.price-list", mode = "adopt" } }, Ct);
        adopted.StatusCode.ShouldBe(HttpStatusCode.Created, await adopted.Content.ReadAsStringAsync(Ct));

        var exported = JsonNode.Parse(await a.GetStringAsync("/api/v1/config/bundle?include=types,layouts,sourceRules", Ct))!.AsObject();
        var exportedRule = exported["sourceRules"]!.AsArray().Single(r => r!["folderPrefix"]?.GetValue<string>() == "lieferanten/nordwind/" && r["supplier"]!["vatId"]!.GetValue<string>() == vat)!;
        exportedRule["channel"]!["name"]!.GetValue<string>().ShouldBe(channel.Name);
        exportedRule["channel"]!["type"]!.GetValue<string>().ShouldBe("WatchedFolder");

        var dry = await ImportAsync(a, exported, "dry-run");
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, await dry.Content.ReadAsStringAsync(Ct));
        var items = (await dry.Content.ReadFromJsonAsync<BundleImportResponse>(ApiFactory.Json, Ct))!.Items;
        items.Where(i => i.Kind == "type").ShouldNotBeEmpty();
        items.Where(i => i.Kind == "type").ShouldAllBe(i => i.Action == "skip", "an unchanged type is no new draft");
        items.Where(i => i.Kind == "layout").ShouldNotBeEmpty();
        items.Where(i => i.Kind == "layout").ShouldAllBe(i => i.Action == "skip", "the same rule and mapping, whatever their whitespace and member order");
        var folderItem = items.Single(i => i.Kind == "sourceRule" && i.Key == "lieferanten/nordwind/");
        folderItem.Action.ShouldBe("skip", folderItem.Detail);
        folderItem.Detail!.ShouldContain("already exists");

        // In another tenant the channel is found by its name; without it the item says which channel to create.
        AddSupplier(ApiFactory.TenantB, vat);
        _factory.Run(db =>
        {
            var features = db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!;
            features.SourceRouting = true;
        });
        try
        {
            using var b = Admin(ApiFactory.TenantB);
            var missing = (await (await ImportAsync(b, exported, "dry-run")).Content.ReadFromJsonAsync<BundleImportResponse>(ApiFactory.Json, Ct))!.Items.Single(i => i.Kind == "sourceRule" && i.Key == "lieferanten/nordwind/");
            missing.Action.ShouldBe("error");
            missing.Detail!.ShouldContain(channel.Name);
            missing.Detail!.ShouldContain("create the channel first");
            _factory.Run(db => db.IngestionChannels.Add(new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = ApiFactory.TenantB, Name = channel.Name, Type = ChannelType.WatchedFolder, ConfigJson = "{}", CreatedAt = DateTimeOffset.UtcNow }));
            var found = (await (await ImportAsync(b, exported, "dry-run")).Content.ReadFromJsonAsync<BundleImportResponse>(ApiFactory.Json, Ct))!.Items.Single(i => i.Kind == "sourceRule" && i.Key == "lieferanten/nordwind/");
            found.Action.ShouldBe("create", found.Detail);
        }
        finally
        {
            _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!.SourceRouting = false);
        }
    }

    [Fact]
    public void The_development_signing_key_is_used_in_Development_and_Testing_only()
    {
        var unset = new ConfigurationBuilder().Build();
        var set = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [GovernanceEndpoints.SigningKeySetting] = "a-real-key" }).Build();
        static IHostEnvironment Env(string name) => new HostingEnvironment { EnvironmentName = name };

        GovernanceEndpoints.KeyOf(unset, Env(Environments.Development)).ShouldBe(GovernanceEndpoints.DevelopmentSigningKey);
        GovernanceEndpoints.KeyOf(unset, Env("Testing")).ShouldBe(GovernanceEndpoints.DevelopmentSigningKey);
        GovernanceEndpoints.KeyOf(unset, Env(Environments.Production)).ShouldBeNull();
        GovernanceEndpoints.KeyOf(unset, Env(Environments.Staging)).ShouldBeNull();
        GovernanceEndpoints.KeyOf(set, Env(Environments.Production)).ShouldBe("a-real-key");
    }
}
