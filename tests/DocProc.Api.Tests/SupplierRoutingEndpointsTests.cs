using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Application.Outputs;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Configuration;
using DocProc.Infrastructure.Outputs;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocProc.Api.Tests;

/// <summary>
/// The supplier configuration API of P4 part A (docs/11 §6.4, §6.5, §7, §18): source rules and the routing test,
/// supplier groups, receiving entities and per-entity attributes, supplier settings and the attribute schema, channel
/// defaults and connector configs, <c>:assign-supplier</c> with <c>remember</c>, the values view's entity, mailbox and
/// folder, the <c>sourceRouting</c> switch, the roles and tenant isolation.
/// </summary>
public sealed class SupplierRoutingEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SupplierRoutingEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.Features!.SourceRouting = true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Admin(Guid? tenant = null) => _factory.ClientFor(new TokenSpec("tenant-admin", [Roles.TenantAdmin], (tenant ?? ApiFactory.TenantA).ToString()));

    private HttpClient Reviewer() => _factory.ClientFor(new TokenSpec("tenant-a-reviewer", [Roles.Reviewer], ApiFactory.TenantA.ToString()));

    private static async Task<SupplierResponse> SupplierAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/suppliers", new CreateSupplierRequest(name, null, null, null, null, null, null, null), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SupplierResponse>(ApiFactory.Json, Ct))!;
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    [Fact]
    public async Task Without_sourceRouting_every_route_of_the_supplier_configuration_is_forbidden()
    {
        using var b = Admin(ApiFactory.TenantB);
        var supplier = await SupplierAsync(b, Unique("Off"));
        foreach (var response in new[]
        {
            await b.GetAsync("/api/v1/supplier-groups", Ct),
            await b.GetAsync("/api/v1/receiving-entities", Ct),
            await b.GetAsync($"/api/v1/suppliers/{supplier.Id}/source-rules", Ct),
            await b.PostAsJsonAsync($"/api/v1/suppliers/{supplier.Id}/source-rules", new SourceRuleRequest(Sender: "a@b.example"), Ct),
            await b.PostAsJsonAsync("/api/v1/routing:test", new RoutingTestRequest(Sender: "a@b.example"), Ct),
            await b.PatchAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new PatchSupplierRequest(null, null, null, null, null, null, null, null) { Settings = JsonDocument.Parse("{}").RootElement }, Ct),
            await b.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest("x", "WatchedFolder", null, null) { DefaultSupplierId = supplier.Id }, Ct),
        })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, response.RequestMessage!.RequestUri!.ToString());
            (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail!.ShouldContain("sourceRouting");
        }

        // A free channel config is accepted as before the switch.
        (await b.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Free"), "Mailbox", JsonDocument.Parse("""{"anything":true}""").RootElement, null), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Source_rules_are_stored_normalised_described_refused_when_duplicate_and_written_by_config_admins_only()
    {
        using var admin = Admin();
        var nordwind = await SupplierAsync(admin, Unique("Nordwind"));
        var other = await SupplierAsync(admin, Unique("Suedhof"));
        var rules = $"/api/v1/suppliers/{nordwind.Id}/source-rules";
        var sender = $"{Unique("rv")}@Nordwind.Example";

        var created = await admin.PostAsJsonAsync(rules, new SourceRuleRequest(Sender: $"Rechnungen <{sender}>", Priority: 10), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var rule = (await created.Content.ReadFromJsonAsync<SourceRuleResponse>(ApiFactory.Json, Ct))!;
        rule.Sender.ShouldBe(sender.ToLowerInvariant());
        rule.Mode.ShouldBe("Exclusive");
        rule.RequireSenderAuth.ShouldBeTrue("decision 1 of docs/11 §24: DMARC by default");
        rule.Describe.ShouldContain("DMARC required");
        rule.CreatedBy.ShouldBe("tenant-admin");

        (await admin.PostAsJsonAsync(rules, new SourceRuleRequest(Sender: sender), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync($"/api/v1/suppliers/{other.Id}/source-rules", new SourceRuleRequest(Sender: sender.ToUpperInvariant()), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "another supplier cannot claim the same conditions");

        foreach (var (bad, field) in new (SourceRuleRequest, string)[]
        {
            (new SourceRuleRequest(SenderDomain: "gmail.com"), "senderDomain"),
            (new SourceRuleRequest(SubjectRegex: "^x"), "conditions"),
            (new SourceRuleRequest(FolderPrefix: "a/"), "folderPrefix"),
            (new SourceRuleRequest(Sender: "x@y.example", SubjectRegex: "(?<=a)b"), "subjectRegex"),
            (new SourceRuleRequest(Sender: "not an address"), "sender"),
            (new SourceRuleRequest(Sender: "x@y.example", Mode: "Sometimes"), "mode"),
            (new SourceRuleRequest(Sender: "x@y.example", PinTypeKey: "not a key!"), "pinTypeKey"),
            (new SourceRuleRequest(ChannelId: Guid.NewGuid()), "channelId"),
        })
        {
            var refused = await admin.PostAsJsonAsync(rules, bad, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, field);
            (await refused.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldContain(field);
        }

        using var reviewer = Reviewer();
        (await reviewer.PostAsJsonAsync(rules, new SourceRuleRequest(Sender: "z@y.example"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reviewer.GetFromJsonAsync<List<SourceRuleResponse>>(rules, ApiFactory.Json, Ct))!.ShouldContain(r => r.Id == rule.Id);

        var replaced = await admin.PutAsJsonAsync($"{rules}/{rule.Id}", new SourceRuleRequest(Sender: sender, Mode: "DefaultWhenNoContentMatch", RequireSenderAuth: false, PinTypeKey: "invoice"), Ct);
        replaced.StatusCode.ShouldBe(HttpStatusCode.OK, await replaced.Content.ReadAsStringAsync(Ct));
        var after = (await replaced.Content.ReadFromJsonAsync<SourceRuleResponse>(ApiFactory.Json, Ct))!;
        after.Mode.ShouldBe("DefaultWhenNoContentMatch");
        after.RequireSenderAuth.ShouldBeFalse();
        after.PinTypeKey.ShouldBe("Invoice");

        (await admin.DeleteAsync($"{rules}/{rule.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetAsync($"{rules}/{rule.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_routing_test_explains_the_winner_the_unverified_sender_the_pinned_type_and_a_tie()
    {
        using var admin = Admin();
        var nordwind = await SupplierAsync(admin, Unique("Nordwind"));
        var suedhof = await SupplierAsync(admin, Unique("Suedhof"));
        var channel = (await (await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Scanner"), "WatchedFolder", JsonDocument.Parse("""{"path":"scanner-x"}""").RootElement, null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(ApiFactory.Json, Ct))!;
        var sender = $"{Unique("rv")}@nordwind.example";
        var domain = $"{Unique("tie")}.example";
        await admin.PostAsJsonAsync($"/api/v1/suppliers/{nordwind.Id}/source-rules", new SourceRuleRequest(Sender: sender), Ct);
        await admin.PostAsJsonAsync($"/api/v1/suppliers/{nordwind.Id}/source-rules", new SourceRuleRequest(ChannelId: channel.Id, FolderPrefix: "Lieferanten/Nordwind/Preislisten", PinTypeKey: "price-list"), Ct);
        (await admin.PostAsJsonAsync($"/api/v1/suppliers/{nordwind.Id}/source-rules", new SourceRuleRequest(Recipient: $"orders@{domain}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PostAsJsonAsync($"/api/v1/suppliers/{suedhof.Id}/source-rules", new SourceRuleRequest(Recipient: $"desk@{domain}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var verified = await Test(admin, new RoutingTestRequest(Sender: sender, SenderAuth: new RoutingTestSenderAuth(Dmarc: "pass")));
        verified.SupplierId.ShouldBe(nordwind.Id);
        verified.Method.ShouldBe("sourceRule");
        verified.Confidence.ShouldBe(0.99);
        verified.SenderVerified.ShouldBeTrue();
        verified.Explanation.ShouldContain(nordwind.Name);

        var unverified = await Test(admin, new RoutingTestRequest(Sender: sender, SenderAuth: new RoutingTestSenderAuth(Dmarc: "fail")));
        unverified.SupplierId.ShouldBeNull();
        unverified.Flags.ShouldBe([FlagCodes.SupplierSourceUnverified]);
        unverified.Candidates.ShouldHaveSingleItem().Failed.ShouldContain("senderAuth");
        unverified.Explanation.ShouldContain("SUPPLIER_SOURCE_UNVERIFIED");

        var folder = await Test(admin, new RoutingTestRequest(ChannelId: channel.Id, FolderPath: "lieferanten/nordwind/preislisten/2027"));
        folder.SupplierId.ShouldBe(nordwind.Id);
        folder.PinTypeKey.ShouldBe("price-list");
        folder.Explanation.ShouldContain("pinned to price-list");

        var tie = await Test(admin, new RoutingTestRequest(Sender: "a@else.example", Recipients: [$"orders@{domain}", $"desk@{domain}"]));
        tie.Ambiguous.ShouldBeTrue();
        tie.AmbiguousSupplierIds.ShouldBe(new[] { nordwind.Id, suedhof.Id }.Order(), ignoreOrder: true);
        tie.Flags.ShouldContain(FlagCodes.SupplierAmbiguous);

        // Tenant B, on the switch too, sees none of A's rules.
        _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!.SourceRouting = true);
        try
        {
            using var b = Admin(ApiFactory.TenantB);
            var other = await Test(b, new RoutingTestRequest(Sender: sender, SenderAuth: new RoutingTestSenderAuth(Dmarc: "pass")));
            other.SupplierId.ShouldBeNull();
            other.Candidates.ShouldBeEmpty();
            (await b.GetAsync($"/api/v1/suppliers/{nordwind.Id}/source-rules", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!.SourceRouting = false);
        }
    }

    [Fact]
    public async Task Groups_entities_and_entity_attributes_are_managed_and_shown_with_the_supplier()
    {
        using var admin = Admin();
        var group = await admin.PostAsJsonAsync("/api/v1/supplier-groups", new CreateSupplierGroupRequest(Unique("Gruppe"), new Dictionary<string, JsonElement> { ["erpGroup"] = JsonDocument.Parse("\"NW\"").RootElement }, JsonDocument.Parse("""{"review":{"mode":"always"}}""").RootElement), Ct);
        group.StatusCode.ShouldBe(HttpStatusCode.Created, await group.Content.ReadAsStringAsync(Ct));
        var groupRow = (await group.Content.ReadFromJsonAsync<SupplierGroupResponse>(ApiFactory.Json, Ct))!;
        groupRow.Settings.GetProperty("review").GetProperty("mode").GetString().ShouldBe("always");

        var code = Unique("C");
        var entity = await admin.PostAsJsonAsync("/api/v1/receiving-entities", new CreateReceivingEntityRequest(code, "Acme Gastro Süd GmbH", ["DE 123 456 789"], JsonDocument.Parse("""{"city":"München"}""").RootElement), Ct);
        entity.StatusCode.ShouldBe(HttpStatusCode.Created, await entity.Content.ReadAsStringAsync(Ct));
        var entityRow = (await entity.Content.ReadFromJsonAsync<ReceivingEntityResponse>(ApiFactory.Json, Ct))!;
        entityRow.VatIds.ShouldBe(["DE123456789"]);
        (await admin.PostAsJsonAsync("/api/v1/receiving-entities", new CreateReceivingEntityRequest(code, "again"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var supplier = await SupplierAsync(admin, Unique("Mitglied"));
        var patched = await admin.PatchAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new PatchSupplierRequest(null, null, null, null, null, null, null, null)
        {
            GroupId = groupRow.Id,
            Settings = JsonDocument.Parse("""{"allowedTypeKeys":["Invoice"],"layouts":{"required":true}}""").RootElement,
        }, Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK, await patched.Content.ReadAsStringAsync(Ct));
        var put = await admin.PutAsJsonAsync($"/api/v1/suppliers/{supplier.Id}/entity-attributes/{entityRow.Id}", new SupplierEntityAttributesRequest(new Dictionary<string, JsonElement> { ["erpVendorId"] = JsonDocument.Parse("\"V-2000-7\"").RootElement }), Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));

        var shown = (await admin.GetFromJsonAsync<SupplierResponse>($"/api/v1/suppliers/{supplier.Id}", ApiFactory.Json, Ct))!;
        shown.GroupId.ShouldBe(groupRow.Id);
        shown.Settings.GetProperty("layouts").GetProperty("required").GetBoolean().ShouldBeTrue("docs/05: required is true, false or \"inherit\"");
        shown.EntityAttributes.ShouldHaveSingleItem().ReceivingEntityCode.ShouldBe(code);

        (await admin.DeleteAsync($"/api/v1/supplier-groups/{groupRow.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "it has a member");
        (await admin.DeleteAsync($"/api/v1/receiving-entities/{entityRow.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "supplier attributes name it");
        (await admin.PatchAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new PatchSupplierRequest(null, null, null, null, null, null, null, null) { GroupId = Guid.Empty }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/v1/supplier-groups/{groupRow.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"/api/v1/suppliers/{supplier.Id}/entity-attributes/{entityRow.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"/api/v1/receiving-entities/{entityRow.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        foreach (var settings in new[] { """{"layoutz":{}}""", """{"review":{"autoApproveMinConfidence":2}}""", """{"defaults":{"currency":"euro"}}""", """{"locale":{"dateOrder":"XYZ"}}""", """{"allowedTypeKeys":["no key!"]}""" })
        {
            var refused = await admin.PatchAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new PatchSupplierRequest(null, null, null, null, null, null, null, null) { Settings = JsonDocument.Parse(settings).RootElement }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, settings);
        }

        using var reviewer = Reviewer();
        (await reviewer.PostAsJsonAsync("/api/v1/supplier-groups", new CreateSupplierGroupRequest("x"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reviewer.GetAsync("/api/v1/receiving-entities", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_tenants_attribute_schema_types_the_attributes_of_suppliers_groups_and_entities()
    {
        using var admin = Admin();
        var current = (await admin.GetFromJsonAsync<TenantSettingsResponse>("/api/v1/settings", ApiFactory.Json, Ct))!.Settings;
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"erpVendorId":{"type":"string","pattern":"^V-[0-9]+$"},"paymentBlock":{"type":"boolean"}}}""").RootElement;
        (await admin.PutAsJsonAsync("/api/v1/settings", current with { SupplierAttributeSchema = JsonDocument.Parse("""{"properties":{"a":{"type":"date"}}}""").RootElement }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var freeMail = await admin.PutAsJsonAsync("/api/v1/settings", current with { TrustedForwarderDomains = ["bureau.example", "GMAIL.com"] }, Ct);
        freeMail.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a free-mail provider is no trusted forwarder");
        (await freeMail.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors["trustedForwarderDomains"].ShouldContain(e => e.Contains("free-mail", StringComparison.Ordinal));
        var saved = await admin.PutAsJsonAsync("/api/v1/settings", current with { SupplierAttributeSchema = schema, TrustedForwarderDomains = ["Bureau.Example"], LayoutsRequiredByDefault = true }, Ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        var settings = (await saved.Content.ReadFromJsonAsync<TenantSettingsResponse>(ApiFactory.Json, Ct))!.Settings;
        settings.TrustedForwarderDomains.ShouldBe(["bureau.example"]);
        settings.LayoutsRequiredByDefault.ShouldBe(true);
        settings.SupplierAttributeSchema!.Value.GetProperty("properties").TryGetProperty("erpVendorId", out _).ShouldBeTrue();

        try
        {
            var bad = await admin.PostAsJsonAsync("/api/v1/suppliers", new CreateSupplierRequest(Unique("Typed"), null, null, null, null, null, null, null) { Attributes = new Dictionary<string, JsonElement> { ["erpVendorId"] = JsonDocument.Parse("\"10042\"").RootElement } }, Ct);
            bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await bad.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldContain("attributes.erpVendorId");
            var good = await admin.PostAsJsonAsync("/api/v1/suppliers", new CreateSupplierRequest(Unique("Typed"), null, null, null, null, null, null, null) { Attributes = new Dictionary<string, JsonElement> { ["erpVendorId"] = JsonDocument.Parse("\"V-10042\"").RootElement, ["free"] = JsonDocument.Parse("1").RootElement } }, Ct);
            good.StatusCode.ShouldBe(HttpStatusCode.Created, await good.Content.ReadAsStringAsync(Ct));
            (await admin.PostAsJsonAsync("/api/v1/supplier-groups", new CreateSupplierGroupRequest(Unique("G"), new Dictionary<string, JsonElement> { ["paymentBlock"] = JsonDocument.Parse("\"no\"").RootElement }), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            // Tenant B (switch off) cannot change its schema, but sending back what GET gave is fine.
            using var b = Admin(ApiFactory.TenantB);
            var bSettings = (await b.GetFromJsonAsync<TenantSettingsResponse>("/api/v1/settings", ApiFactory.Json, Ct))!.Settings;
            (await b.PutAsJsonAsync("/api/v1/settings", bSettings, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await b.PutAsJsonAsync("/api/v1/settings", bSettings with { SupplierAttributeSchema = schema }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/v1/settings", current with { SupplierAttributeSchema = JsonDocument.Parse("{}").RootElement, TrustedForwarderDomains = [], LayoutsRequiredByDefault = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        _factory.Query(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.SupplierAttributeSchema).ShouldBeNull("an empty object removes the schema");
    }

    [Fact]
    public async Task Mailbox_and_folder_channels_are_checked_get_a_source_key_and_cannot_read_a_source_twice()
    {
        using var admin = Admin();
        var supplier = await SupplierAsync(admin, Unique("Default"));
        var address = $"{Unique("box")}@tenant-a.local";
        var invalid = await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Mail"), "Mailbox", JsonDocument.Parse("""{"address":"ap@acme.example","host":"10.0.0.5"}""").RootElement, null), Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var freeMailForwarder = await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Mail"), "Mailbox", JsonDocument.Parse($$"""{"address":"{{address}}","trustedForwarderDomains":["web.de"]}""").RootElement, null), Ct);
        freeMailForwarder.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await freeMailForwarder.Content.ReadAsStringAsync(Ct)).ShouldContain("free-mail");

        var created = await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Mail"), "Mailbox", JsonDocument.Parse($$"""{"address":"{{address}}","trustedForwarderDomains":["bureau.example"]}""").RootElement, "secret") { DefaultSupplierId = supplier.Id, DefaultDocumentTypeKey = "invoice" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>(ApiFactory.Json, Ct))!;
        channel.SourceKey.ShouldBeNull("a mailbox's key is claimed by the poller once the channel has logged in");
        channel.DefaultSupplierId.ShouldBe(supplier.Id);
        channel.DefaultDocumentTypeKey.ShouldBe("Invoice");
        channel.HasSecret.ShouldBeTrue();

        var twin = await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Twin"), "Mailbox", JsonDocument.Parse($$"""{"address":"{{address.ToUpperInvariant()}}"}""").RootElement, null), Ct);
        twin.StatusCode.ShouldBe(HttpStatusCode.Conflict, "another channel of this tenant is configured to read it");
        (await twin.Content.ReadAsStringAsync(Ct)).ShouldContain("of this tenant");
        (await admin.DeleteAsync($"/api/v1/suppliers/{supplier.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "the channel names it as its default");

        var cleared = await admin.PatchAsJsonAsync($"/api/v1/channels/{channel.Id}", new PatchChannelRequest("renamed", null, null, null) { DefaultSupplierId = Guid.Empty, DefaultDocumentTypeKey = string.Empty }, Ct);
        cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync(Ct));
        var after = (await cleared.Content.ReadFromJsonAsync<ChannelResponse>(ApiFactory.Json, Ct))!;
        after.DefaultSupplierId.ShouldBeNull();
        after.DefaultDocumentTypeKey.ShouldBeNull();
        after.SourceKey.ShouldBe(channel.SourceKey);

        var folder = await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Drop"), "WatchedFolder", JsonDocument.Parse("""{"path":"../tenant-b"}""").RootElement, null), Ct);
        folder.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task No_tenant_can_lock_or_probe_a_mailbox_of_another_tenant_by_create_or_patch()
    {
        // Tenant B names tenant A's mailbox, without a secret: the channel is saved, but it claims no key (only the
        // poller does, after a login with the channel's own secret), so tenant A's channel is not refused and the answer
        // tells neither tenant whether the other reads the mailbox.
        using var a = Admin();
        using var b = Admin(ApiFactory.TenantB);
        var address = $"{Unique("shared")}@tenant-a.local";
        var config = JsonDocument.Parse($$"""{"address":"{{address}}"}""").RootElement;
        var squatter = await b.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Twin"), "Mailbox", config, null), Ct);
        squatter.StatusCode.ShouldBe(HttpStatusCode.Created, await squatter.Content.ReadAsStringAsync(Ct));
        var squatterChannel = (await squatter.Content.ReadFromJsonAsync<ChannelResponse>(ApiFactory.Json, Ct))!;
        squatterChannel.SourceKey.ShouldBeNull();

        var owner = await a.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Mail"), "Mailbox", config, "invoices"), Ct);
        owner.StatusCode.ShouldBe(HttpStatusCode.Created, "another tenant's channel neither locks the mailbox nor is revealed");
        var ownerChannel = (await owner.Content.ReadFromJsonAsync<ChannelResponse>(ApiFactory.Json, Ct))!;
        ownerChannel.SourceKey.ShouldBeNull();

        // A key the poller claimed stays through a save that keeps the source; a patch to another source drops it.
        var key = $"imap:platform/{address}/inbox";
        _factory.Run(db => db.IngestionChannels.Single(c => c.Id == ownerChannel.Id).SourceKey = key);
        var renamed = (await (await a.PatchAsJsonAsync($"/api/v1/channels/{ownerChannel.Id}", new PatchChannelRequest("renamed", null, null, null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(ApiFactory.Json, Ct))!;
        renamed.SourceKey.ShouldBe(key);
        var same = (await (await a.PatchAsJsonAsync($"/api/v1/channels/{ownerChannel.Id}", new PatchChannelRequest(null, null, JsonDocument.Parse($$"""{"address":"{{address.ToUpperInvariant()}}"}""").RootElement, null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(ApiFactory.Json, Ct))!;
        same.SourceKey.ShouldBe(key, "the same mailbox, written otherwise");

        // Tenant B's create and patch towards the mailbox tenant A's channel holds are no 409 and claim nothing.
        var second = await b.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Again"), "Mailbox", config, null), Ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Created, "without the switch a twin of the tenant's own channel is saved as before");
        var free = $"{Unique("own")}@tenant-b.local";
        var own = (await (await b.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Own"), "Mailbox", JsonDocument.Parse($$"""{"address":"{{free}}"}""").RootElement, null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(ApiFactory.Json, Ct))!;
        own.SourceKey.ShouldBeNull();
        var moved = await b.PatchAsJsonAsync($"/api/v1/channels/{own.Id}", new PatchChannelRequest(null, null, config, null), Ct);
        moved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await moved.Content.ReadFromJsonAsync<ChannelResponse>(ApiFactory.Json, Ct))!.SourceKey.ShouldBeNull();
        (await b.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Loose"), "Mailbox", JsonDocument.Parse("""{"provider":"imap"}""").RootElement, null), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created, "a config that is not a mailbox's shape is still accepted without the switch");

        // With the switch on, tenant B is still not told that tenant A reads the mailbox (only its own channels count).
        _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!.SourceRouting = true);
        try
        {
            var other = $"{Unique("other")}@tenant-a.local";
            _factory.Run(db => db.IngestionChannels.Single(c => c.Id == ownerChannel.Id).ConfigJson = $$"""{"address":"{{other}}"}""");
            _factory.Run(db => db.IngestionChannels.Single(c => c.Id == ownerChannel.Id).SourceKey = $"imap:platform/{other}/inbox");
            var probe = await b.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Probe"), "Mailbox", JsonDocument.Parse($$"""{"address":"{{other}}"}""").RootElement, null), Ct);
            probe.StatusCode.ShouldBe(HttpStatusCode.Created, "tenant A's channel is not looked at");
            (await probe.Content.ReadFromJsonAsync<ChannelResponse>(ApiFactory.Json, Ct))!.SourceKey.ShouldBeNull();
            var ownTwin = await b.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Probe2"), "Mailbox", JsonDocument.Parse($$"""{"address":"{{other}}"}""").RootElement, null), Ct);
            ownTwin.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the tenant's own channel is configured to read it");
            _factory.Query(db => db.IngestionChannels.Single(c => c.Id == ownerChannel.Id).SourceKey).ShouldBe($"imap:platform/{other}/inbox", "tenant A keeps its key");
        }
        finally
        {
            _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!.SourceRouting = false);
        }
    }

    [Fact]
    public async Task A_starttls_mailbox_with_and_without_its_port_is_one_source()
    {
        // MailboxChannelConfig.SourceKey names the port the poller connects to: 143 for starttls without a port.
        using var admin = Admin();
        var host = "imap.example.com";
        var implicitPort = JsonDocument.Parse($$"""{"address":"ap@acme.example","host":"{{host}}","security":"starttls","username":"{{Unique("ap")}}"}""").RootElement;
        var created = await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Own"), "Mailbox", implicitPort, "pw"), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var username = implicitPort.GetProperty("username").GetString();
        var explicitPort = JsonDocument.Parse($$"""{"address":"ap@acme.example","host":"{{host}}","port":143,"security":"starttls","username":"{{username}}"}""").RootElement;
        (await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Twin"), "Mailbox", explicitPort, "pw"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var tls = JsonDocument.Parse($$"""{"address":"ap@acme.example","host":"{{host}}","port":993,"username":"{{username}}"}""").RootElement;
        (await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest(Unique("Tls"), "Mailbox", tls, "pw"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created, "port 993 is another source");
    }

    [Fact]
    public async Task Assign_supplier_remembers_a_source_rule_a_reviewer_proposes_and_a_config_admin_enables()
    {
        using var admin = Admin();
        var supplier = await SupplierAsync(admin, Unique("Gedacht"));
        var sender = $"{Unique("buchhaltung")}@gedacht.example";
        var first = await SeedRoutedDocumentAsync(sender);

        using var reviewer = Reviewer();
        var missing = await reviewer.PostAsJsonAsync($"/api/v1/documents/{first}:assign-supplier", new AssignSupplierRequest(supplier.Id) { Remember = new AssignSupplierRemember(["folder"]) }, Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the mail came from no folder");

        var proposed = await reviewer.PostAsJsonAsync($"/api/v1/documents/{first}:assign-supplier", new AssignSupplierRequest(supplier.Id) { Remember = new AssignSupplierRemember(["sender"], PinType: true) }, Ct);
        proposed.StatusCode.ShouldBe(HttpStatusCode.OK, await proposed.Content.ReadAsStringAsync(Ct));
        var detail = (await proposed.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        detail.SupplierId.ShouldBe(supplier.Id);
        detail.Flags.Where(f => f.Code == FlagCodes.SupplierConflict).ShouldAllBe(f => f.ResolvedAt != null, "the reviewer decided");
        var rule = (await admin.GetFromJsonAsync<List<SourceRuleResponse>>($"/api/v1/suppliers/{supplier.Id}/source-rules", ApiFactory.Json, Ct))!.ShouldHaveSingleItem();
        rule.Sender.ShouldBe(sender);
        rule.PinTypeKey.ShouldBe("Invoice");
        rule.Enabled.ShouldBeFalse("a reviewer's rule is a proposal");
        rule.CreatedFromDocumentId.ShouldBe(first);

        var second = await SeedRoutedDocumentAsync(sender);
        var again = await admin.PostAsJsonAsync($"/api/v1/documents/{second}:assign-supplier", new AssignSupplierRequest(supplier.Id) { Remember = new AssignSupplierRemember(["sender"]) }, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.OK, "the same conditions for the same supplier are the rule that exists");
        (await admin.GetFromJsonAsync<List<SourceRuleResponse>>($"/api/v1/suppliers/{supplier.Id}/source-rules", ApiFactory.Json, Ct))!.Count.ShouldBe(1);

        var third = await SeedRoutedDocumentAsync(sender);
        var enabled = await admin.PostAsJsonAsync($"/api/v1/documents/{third}:assign-supplier", new AssignSupplierRequest(supplier.Id) { Remember = new AssignSupplierRemember(["recipient"], Mode: "DefaultWhenNoContentMatch") }, Ct);
        enabled.StatusCode.ShouldBe(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync(Ct));
        var rules = (await admin.GetFromJsonAsync<List<SourceRuleResponse>>($"/api/v1/suppliers/{supplier.Id}/source-rules", ApiFactory.Json, Ct))!;
        rules.Single(r => r.Recipient is not null).Enabled.ShouldBeTrue("a config admin's rule is enabled at once");

        var otherSupplier = await SupplierAsync(admin, Unique("Anders"));
        var fourth = await SeedRoutedDocumentAsync(sender);
        (await admin.PostAsJsonAsync($"/api/v1/documents/{fourth}:assign-supplier", new AssignSupplierRequest(otherSupplier.Id) { Remember = new AssignSupplierRemember(["sender"]) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_values_view_reads_the_receiving_entity_the_group_the_merged_attributes_and_the_mailbox_and_folder()
    {
        var tenant = _factory.CreateTenant("Routed view");
        var seeded = await _factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.Approved, SupplierName = "Nordwind Großhandel GmbH" });
        var entity = new ReceivingEntity { Id = Guid.CreateVersion7(), TenantId = tenant, Code = "2000", Name = "Acme Gastro Nord GmbH", VatIds = ["DE999999999"], AttributesJson = """{"companyCode":"2000"}""", CreatedAt = _factory.Clock.GetUtcNow(), UpdatedAt = _factory.Clock.GetUtcNow() };
        var group = new SupplierGroup { Id = Guid.CreateVersion7(), TenantId = tenant, Name = "Nordwind-Gruppe", AttributesJson = """{"erpVendorId":"V-GROUP","paymentTerms":"30"}""", CreatedAt = _factory.Clock.GetUtcNow(), UpdatedAt = _factory.Clock.GetUtcNow() };
        _factory.Run(db =>
        {
            db.ReceivingEntities.Add(entity);
            db.SupplierGroups.Add(group);
            var supplier = db.Suppliers.Single(s => s.TenantId == tenant);
            supplier.GroupId = group.Id;
            supplier.AttributesJson = """{"erpVendorId":"V-10042"}""";
            db.SupplierEntityAttributes.Add(new SupplierEntityAttributes { TenantId = tenant, SupplierId = supplier.Id, ReceivingEntityId = entity.Id, AttributesJson = """{"erpVendorId":"V-2000-7"}""", UpdatedAt = _factory.Clock.GetUtcNow() });
            db.Documents.Single(d => d.Id == seeded.DocumentId).ReceivingEntityId = entity.Id;
            var ingestion = db.Ingestions.Single(i => i.Id == seeded.IngestionId);
            ingestion.SourceMailbox = "invoices@tenant-a.local";
            ingestion.SourceFolderPath = "lieferanten/nordwind/";
        });
        await using var db = _factory.PlatformDb();
        var builder = new DocumentDataViewBuilder(db, new CatalogueConfigResolver(db, new EfTenantFeatures(db), new PublishedDefinitionCache()), _factory.Clock, Options.Create(new OutputOptions()));

        var view = (await builder.BuildAsync(new DataViewRequest(seeded.DocumentId), Ct))!.View;

        view["entity"]!["code"]!.GetValue<string>().ShouldBe("2000");
        view["entity"]!["name"]!.GetValue<string>().ShouldBe("Acme Gastro Nord GmbH");
        view["entity"]!["vatId"]!.GetValue<string>().ShouldBe("DE999999999");
        view["entity"]!["attributes"]!["companyCode"]!.GetValue<string>().ShouldBe("2000");
        view["supplier"]!["group"]!.GetValue<string>().ShouldBe("Nordwind-Gruppe");
        view["supplier"]!["attributes"]!["erpVendorId"]!.GetValue<string>().ShouldBe("V-2000-7", "the entity's over the supplier's over the group's");
        view["supplier"]!["attributes"]!["paymentTerms"]!.GetValue<string>().ShouldBe("30");
        view["source"]!["mailbox"]!.GetValue<string>().ShouldBe("invoices@tenant-a.local");
        view["source"]!["folderPath"]!.GetValue<string>().ShouldBe("lieferanten/nordwind/");
    }

    [Fact]
    public async Task A_reprocess_can_start_at_Match()
    {
        var seeded = await _factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.NeedsReview });
        _factory.RecordPipelineProgress(seeded.DocumentId, DocumentStatus.Validated);
        using var admin = Admin();

        var response = await admin.PostAsJsonAsync($"/api/v1/documents/{seeded.DocumentId}:reprocess", new { fromStage = "Match" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        _factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId).Status).ShouldBe(DocumentStatus.Classified);
    }

    /// <summary>A document in review from a mail of <paramref name="sender"/> to the tenant's mailbox, with a SUPPLIER_CONFLICT the review answers.</summary>
    private async Task<Guid> SeedRoutedDocumentAsync(string sender)
    {
        var seeded = await _factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true, SupplierName = null, Flags = [FlagCodes.Create(FlagCodes.SupplierConflict, "conflict")] });
        _factory.Run(db =>
        {
            var ingestion = db.Ingestions.Single(i => i.Id == seeded.IngestionId);
            ingestion.SourceSender = sender;
            ingestion.SourceMailbox = "invoices@tenant-a.local";
        });
        return seeded.DocumentId;
    }

    private static async Task<RoutingTestResponse> Test(HttpClient client, RoutingTestRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/routing:test", request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RoutingTestResponse>(ApiFactory.Json, Ct))!;
    }
}
