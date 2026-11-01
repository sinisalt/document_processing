using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Api.Tests;

public sealed class ChannelEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri ChannelsUri = new("/api/v1/channels", UriKind.Relative);
    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient TenantAdminClient() => factory.ClientFor(new TokenSpec("tenant-a-admin", [Roles.TenantAdmin], TenantA));

    private ISecretStore Secrets => factory.Services.GetRequiredService<ISecretStore>();

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static Uri ChannelUri(Guid id) => new($"/api/v1/channels/{id}", UriKind.Relative);

    [Fact]
    public async Task Channels_store_config_as_json_and_the_secret_under_a_server_generated_reference()
    {
        using var admin = TenantAdminClient();
        const string password = "imap-app-password-9f3e";

        var response = await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Invoices mailbox", "mailbox", Json("""{"address":"invoices@tenant-a.test","provider":"imap","port":993}"""), password), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(password);
        body.ShouldNotContain("secretRef", Case.Insensitive);
        var created = (await response.Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        created.Type.ShouldBe(nameof(ChannelType.Mailbox));
        created.Status.ShouldBe(nameof(ChannelStatus.Active));
        created.HasSecret.ShouldBeTrue();
        created.Config.GetProperty("address").GetString().ShouldBe("invoices@tenant-a.test");
        created.Config.GetProperty("port").GetInt32().ShouldBe(993);

        var stored = factory.Query(db => db.IngestionChannels.Single(c => c.Id == created.Id));
        stored.TenantId.ShouldBe(ApiFactory.TenantA);
        stored.SecretRef.ShouldBe($"tenant/{ApiFactory.TenantA}/channel/{created.Id}");
        Json(stored.ConfigJson).GetProperty("provider").GetString().ShouldBe("imap");
        (await Secrets.GetAsync(stored.SecretRef!, Ct)).ShouldBe(password, "the secret round-trips through the store under the generated reference");

        var listJson = await admin.GetStringAsync(ChannelsUri, Ct);
        listJson.ShouldContain(created.Id.ToString());
        listJson.ShouldNotContain(password);
        listJson.ShouldNotContain("tenant/", Case.Insensitive);

        var noConfig = (await (await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Drop folder", "WatchedFolder", null, null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        noConfig.HasSecret.ShouldBeFalse();
        noConfig.Config.ValueKind.ShouldBe(JsonValueKind.Object);
        noConfig.Config.EnumerateObject().ShouldBeEmpty();
        factory.Query(db => db.IngestionChannels.Single(c => c.Id == noConfig.Id).SecretRef).ShouldBeNull();
    }

    [Fact]
    public async Task A_client_supplied_secretRef_is_ignored_and_can_never_point_at_another_secret()
    {
        using var admin = TenantAdminClient();
        var platformReference = $"platform/{Guid.NewGuid()}";
        await Secrets.SetAsync(platformReference, "the-platform-llm-key", Ct);

        // secretRef is not a request property any more; System.Text.Json drops it, and the reference is generated.
        var withSecret = await admin.PostAsJsonAsync(ChannelsUri, new { name = "Hijack attempt", type = "Mailbox", config = new { address = "x@attacker.test" }, secretRef = platformReference, secret = "own-password" }, Ct);
        withSecret.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await withSecret.Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        var stored = factory.Query(db => db.IngestionChannels.Single(c => c.Id == created.Id).SecretRef);
        stored.ShouldBe(ChannelEndpoints.SecretReference(ApiFactory.TenantA, created.Id));
        (await Secrets.GetAsync(stored!, Ct)).ShouldBe("own-password");
        (await Secrets.GetAsync(platformReference, Ct)).ShouldBe("the-platform-llm-key", "untouched");

        // Without a secret of its own the channel simply has none, whatever reference the client named.
        var withoutSecret = await admin.PostAsJsonAsync(ChannelsUri, new { name = "Reference only", type = "Mailbox", secretRef = platformReference }, Ct);
        withoutSecret.StatusCode.ShouldBe(HttpStatusCode.Created);
        var referenceOnly = (await withoutSecret.Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        referenceOnly.HasSecret.ShouldBeFalse();
        factory.Query(db => db.IngestionChannels.Single(c => c.Id == referenceOnly.Id).SecretRef).ShouldBeNull();
    }

    [Fact]
    public async Task Patch_rotates_or_clears_the_secret_and_delete_removes_it_from_the_store()
    {
        using var admin = TenantAdminClient();
        var created = (await (await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Rotating mailbox", "Mailbox", null, "first-password"), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        var reference = ChannelEndpoints.SecretReference(ApiFactory.TenantA, created.Id);

        // Absent: untouched.
        var renamed = (await (await admin.PatchAsJsonAsync(ChannelUri(created.Id), new PatchChannelRequest("Renamed", null, null, null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        renamed.HasSecret.ShouldBeTrue();
        (await Secrets.GetAsync(reference, Ct)).ShouldBe("first-password");

        // A value: rotated in place.
        var rotated = (await (await admin.PatchAsJsonAsync(ChannelUri(created.Id), new PatchChannelRequest(null, null, null, "second-password"), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        rotated.HasSecret.ShouldBeTrue();
        (await Secrets.GetAsync(reference, Ct)).ShouldBe("second-password");
        factory.Query(db => db.IngestionChannels.Single(c => c.Id == created.Id).SecretRef).ShouldBe(reference);

        // An empty string: cleared, row and store.
        var cleared = (await (await admin.PatchAsJsonAsync(ChannelUri(created.Id), new PatchChannelRequest(null, null, null, string.Empty), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        cleared.HasSecret.ShouldBeFalse();
        factory.Query(db => db.IngestionChannels.Single(c => c.Id == created.Id).SecretRef).ShouldBeNull();
        (await Secrets.GetAsync(reference, Ct)).ShouldBeNull();

        // Setting one on a channel that has none generates the reference again.
        var again = (await (await admin.PatchAsJsonAsync(ChannelUri(created.Id), new PatchChannelRequest(null, null, null, "third-password"), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        again.HasSecret.ShouldBeTrue();
        (await Secrets.GetAsync(reference, Ct)).ShouldBe("third-password");

        (await admin.DeleteAsync(ChannelUri(created.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Secrets.GetAsync(reference, Ct)).ShouldBeNull("the channel's secret went with it");

        var tooLong = await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Long", "Mailbox", null, new string('x', ChannelEndpoints.MaxSecretLength + 1)), Ct);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooLong.Content.ReadAsStringAsync(Ct)).ShouldContain("\"secret\"");
    }

    [Fact]
    public async Task A_legacy_row_pointing_at_a_foreign_reference_can_neither_overwrite_nor_delete_that_secret()
    {
        // Rows written before references were server-generated may carry a client-chosen reference such as the
        // platform's LLM key. Rotate, clear and delete must never reach the store through it.
        var tenant = factory.CreateTenant("Legacy references");
        var platformReference = $"platform/{Guid.NewGuid()}";
        await Secrets.SetAsync(platformReference, "the-platform-llm-key", Ct);
        var rotated = Guid.CreateVersion7();
        var cleared = Guid.CreateVersion7();
        var deleted = Guid.CreateVersion7();
        var now = factory.Clock.GetUtcNow();
        factory.Run(db =>
        {
            foreach (var id in new[] { rotated, cleared, deleted })
            {
                db.IngestionChannels.Add(new IngestionChannel { Id = id, TenantId = tenant, Name = "Legacy " + id.ToString("N")[..6], Type = ChannelType.Mailbox, SecretRef = platformReference, CreatedAt = now });
            }
        });
        using var admin = factory.TenantAdminOf(tenant);

        // Rotate: the new value lands under the channel's own reference; the row is repointed; the platform key is untouched.
        var rotateResponse = await admin.PatchAsJsonAsync(ChannelUri(rotated), new PatchChannelRequest(null, null, null, "x"), Ct);
        rotateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await rotateResponse.Content.ReadFromJsonAsync<ChannelResponse>(Ct))!.HasSecret.ShouldBeTrue();
        var ownReference = ChannelEndpoints.SecretReference(tenant, rotated);
        factory.Query(db => db.IngestionChannels.Single(c => c.Id == rotated).SecretRef).ShouldBe(ownReference);
        (await Secrets.GetAsync(ownReference, Ct)).ShouldBe("x");
        (await Secrets.GetAsync(platformReference, Ct)).ShouldBe("the-platform-llm-key");

        // Clear on the repointed row removes the channel's own secret only.
        (await admin.PatchAsJsonAsync(ChannelUri(rotated), new PatchChannelRequest(null, null, null, string.Empty), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Secrets.GetAsync(ownReference, Ct)).ShouldBeNull();
        (await Secrets.GetAsync(platformReference, Ct)).ShouldBe("the-platform-llm-key");

        // Clear on a legacy row detaches it from the foreign reference without deleting anything.
        var clearResponse = await admin.PatchAsJsonAsync(ChannelUri(cleared), new PatchChannelRequest(null, null, null, string.Empty), Ct);
        clearResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await clearResponse.Content.ReadFromJsonAsync<ChannelResponse>(Ct))!.HasSecret.ShouldBeFalse();
        factory.Query(db => db.IngestionChannels.Single(c => c.Id == cleared).SecretRef).ShouldBeNull();
        (await Secrets.GetAsync(platformReference, Ct)).ShouldBe("the-platform-llm-key");

        // Delete of a legacy row removes the row only.
        (await admin.DeleteAsync(ChannelUri(deleted), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Query(db => db.IngestionChannels.Any(c => c.Id == deleted)).ShouldBeFalse();
        (await Secrets.GetAsync(platformReference, Ct)).ShouldBe("the-platform-llm-key", "the platform secret survives rotate, clear and delete");
    }

    [Fact]
    public async Task Patch_changes_name_status_and_config_and_delete_refuses_a_channel_with_ingestions()
    {
        using var admin = TenantAdminClient();
        var created = (await (await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("To edit", "ApiUpload", Json("""{"a":1}"""), null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        var uri = ChannelUri(created.Id);

        var patched = await admin.PatchAsJsonAsync(uri, new PatchChannelRequest("Edited", "paused", Json("""{"b":2}"""), null), Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = (await patched.Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        after.Name.ShouldBe("Edited");
        after.Status.ShouldBe(nameof(ChannelStatus.Paused));
        after.Config.TryGetProperty("a", out _).ShouldBeFalse();
        after.Config.GetProperty("b").GetInt32().ShouldBe(2);

        (await admin.PatchAsJsonAsync(uri, new PatchChannelRequest(null, "broken", null, null), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PatchAsJsonAsync(uri, new PatchChannelRequest(null, "0", null, null), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a numeric value is not a status name");
        (await admin.PatchAsJsonAsync(uri, new PatchChannelRequest(null, null, Json("[1,2]"), null), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        factory.Run(db => db.Ingestions.Add(TestData.Ingestion(ApiFactory.TenantA, created.Id, factory.Clock.GetUtcNow())));
        (await admin.DeleteAsync(uri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var unused = (await (await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Unused", "PortalUpload", null, null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        (await admin.DeleteAsync(ChannelUri(unused.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync(ChannelUri(unused.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Config_over_16_KB_is_refused_on_create_and_patch()
    {
        using var admin = TenantAdminClient();
        var justUnder = Json($$$"""{"blob":"{{{new string('a', ChannelEndpoints.MaxConfigLength - 12)}}}"}""");
        var over = Json($$$"""{"blob":"{{{new string('a', ChannelEndpoints.MaxConfigLength)}}}"}""");
        justUnder.GetRawText().Length.ShouldBeLessThanOrEqualTo(ChannelEndpoints.MaxConfigLength);
        over.GetRawText().Length.ShouldBeGreaterThan(ChannelEndpoints.MaxConfigLength);

        var tooBig = await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Big", "WatchedFolder", over, null), Ct);
        tooBig.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooBig.Content.ReadAsStringAsync(Ct)).ShouldContain("\"config\"");

        var ok = await admin.PostAsJsonAsync(ChannelsUri, new CreateChannelRequest("Big enough", "WatchedFolder", justUnder, null), Ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await ok.Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;

        var patchTooBig = await admin.PatchAsJsonAsync(ChannelUri(created.Id), new PatchChannelRequest(null, null, over, null), Ct);
        patchTooBig.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await patchTooBig.Content.ReadAsStringAsync(Ct)).ShouldContain("\"config\"");
        factory.Query(db => db.IngestionChannels.Single(c => c.Id == created.Id).ConfigJson).ShouldBe(justUnder.GetRawText(), "the stored config is untouched");
    }

    [Fact]
    public async Task A_malformed_stored_config_reads_as_an_empty_object_instead_of_failing_the_list()
    {
        var tenant = factory.CreateTenant("Malformed config");
        var broken = Guid.CreateVersion7();
        var fine = Guid.CreateVersion7();
        var now = factory.Clock.GetUtcNow();
        factory.Run(db =>
        {
            db.IngestionChannels.Add(new IngestionChannel { Id = broken, TenantId = tenant, Name = "Broken", Type = ChannelType.Mailbox, ConfigJson = "{not json", CreatedAt = now });
            db.IngestionChannels.Add(new IngestionChannel { Id = fine, TenantId = tenant, Name = "Fine", Type = ChannelType.Mailbox, ConfigJson = """{"ok":true}""", CreatedAt = now });
        });

        using var admin = factory.TenantAdminOf(tenant);
        var response = await admin.GetAsync(ChannelsUri, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = (await response.Content.ReadFromJsonAsync<List<ChannelResponse>>(Ct))!;
        list.Select(c => c.Id).ShouldBe([broken, fine], ignoreOrder: true);
        var brokenResponse = list.Single(c => c.Id == broken);
        brokenResponse.Config.ValueKind.ShouldBe(JsonValueKind.Object);
        brokenResponse.Config.EnumerateObject().ShouldBeEmpty();
        list.Single(c => c.Id == fine).Config.GetProperty("ok").GetBoolean().ShouldBeTrue();

        // PATCH on the broken row still works and repairs the stored config.
        var repaired = (await (await admin.PatchAsJsonAsync(ChannelUri(broken), new PatchChannelRequest(null, null, Json("""{"fixed":1}"""), null), Ct)).Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        repaired.Config.GetProperty("fixed").GetInt32().ShouldBe(1);
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
        (await adminB.DeleteAsync(ChannelUri(created.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        factory.Query(db => db.IngestionChannels.Any(c => c.Id == created.Id)).ShouldBeTrue();
    }
}
