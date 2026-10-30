using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Tests;

public sealed class ChannelEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri ChannelsUri = new("/api/v1/channels", UriKind.Relative);
    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient TenantAdminClient() => factory.ClientFor(new TokenSpec("tenant-a-admin", [Roles.TenantAdmin], TenantA));

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task Channels_store_config_as_json_and_only_reveal_that_a_secret_exists()
    {
        using var admin = TenantAdminClient();

        var response = await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Invoices mailbox", "mailbox", Json("""{"address":"invoices@tenant-a.test","provider":"imap","port":993}"""), "kv-mailbox-tenant-a"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        created.Type.ShouldBe(nameof(ChannelType.Mailbox));
        created.Status.ShouldBe(nameof(ChannelStatus.Active));
        created.HasSecret.ShouldBeTrue();
        created.Config.GetProperty("address").GetString().ShouldBe("invoices@tenant-a.test");
        created.Config.GetProperty("port").GetInt32().ShouldBe(993);

        var stored = factory.Query(db => db.IngestionChannels.Single(c => c.Id == created.Id));
        stored.TenantId.ShouldBe(ApiFactory.TenantA);
        stored.SecretRef.ShouldBe("kv-mailbox-tenant-a");
        Json(stored.ConfigJson).GetProperty("provider").GetString().ShouldBe("imap");

        var listJson = await admin.GetStringAsync(ChannelsUri, Ct);
        listJson.ShouldContain(created.Id.ToString());
        listJson.ShouldNotContain("kv-mailbox-tenant-a");

        var noConfig = (await (await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Drop folder", "WatchedFolder", null, null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        noConfig.HasSecret.ShouldBeFalse();
        noConfig.Config.ValueKind.ShouldBe(JsonValueKind.Object);
        noConfig.Config.EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task Patch_changes_name_status_and_config_and_delete_refuses_a_channel_with_ingestions()
    {
        using var admin = TenantAdminClient();
        var created = (await (await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("To edit", "ApiUpload", Json("""{"a":1}"""), null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        var uri = new Uri($"/api/v1/channels/{created.Id}", UriKind.Relative);

        var patched = await admin.PatchAsJsonAsync(uri, new PatchChannelRequest("Edited", "paused", Json("""{"b":2}""")), Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = (await patched.Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        after.Name.ShouldBe("Edited");
        after.Status.ShouldBe(nameof(ChannelStatus.Paused));
        after.Config.TryGetProperty("a", out _).ShouldBeFalse();
        after.Config.GetProperty("b").GetInt32().ShouldBe(2);

        (await admin.PatchAsJsonAsync(uri, new PatchChannelRequest(null, "broken", null), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PatchAsJsonAsync(uri, new PatchChannelRequest(null, null, Json("[1,2]")), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        factory.Run(db => db.Ingestions.Add(TestData.Ingestion(ApiFactory.TenantA, created.Id, factory.Clock.GetUtcNow())));
        (await admin.DeleteAsync(uri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var unused = (await (await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Unused", "PortalUpload", null, null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        (await admin.DeleteAsync(new Uri($"/api/v1/channels/{unused.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync(new Uri($"/api/v1/channels/{unused.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invalid_requests_and_wrong_roles_are_refused_and_tenants_are_isolated()
    {
        using var admin = TenantAdminClient();
        var bad = await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("", "Carrier pigeon", Json("\"text\""), null), Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await bad.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("\"name\"");
        body.ShouldContain("\"type\"");
        body.ShouldContain("\"config\"");

        var created = (await (await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Private to A", "ApiUpload", null, null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;

        using var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], TenantA));
        (await viewer.GetFromJsonAsync<List<ChannelResponse>>(ChannelsUri, Ct))!.ShouldContain(c => c.Id == created.Id);
        (await viewer.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Nope", "ApiUpload", null, null), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var adminB = factory.ClientFor(new TokenSpec("tenant-b-admin", [Roles.TenantAdmin], ApiFactory.TenantB.ToString()));
        (await adminB.GetFromJsonAsync<List<ChannelResponse>>(ChannelsUri, Ct))!.ShouldNotContain(c => c.Id == created.Id);
        (await adminB.DeleteAsync(new Uri($"/api/v1/channels/{created.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        factory.Query(db => db.IngestionChannels.Any(c => c.Id == created.Id)).ShouldBeTrue();
    }
}
