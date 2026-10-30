using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Application.Security;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Tests;

public sealed class ApiKeyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri KeysUri = new("/api/v1/api-keys", UriKind.Relative);
    private static readonly Uri MeUri = new("/api/v1/me", UriKind.Relative);
    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient TenantAdminClient() => factory.ClientFor(new TokenSpec("tenant-a-admin", [Roles.TenantAdmin], TenantA));

    [Fact]
    public void Generated_keys_have_the_documented_shape_and_hash_deterministically()
    {
        var a = ApiKeys.Generate();
        var b = ApiKeys.Generate();

        a.Key.ShouldStartWith("dpk_");
        ApiKeys.IsWellFormed(a.Key).ShouldBeTrue();
        ApiKeys.PrefixOf(a.Key).ShouldBe(a.Prefix);
        a.Prefix.Length.ShouldBe(8);
        a.Hash.Length.ShouldBe(64);
        ApiKeys.Hash(a.Key).ShouldBe(a.Hash);
        a.Key.ShouldNotBe(b.Key);
        ApiKeys.IsWellFormed("dpk_short").ShouldBeFalse();
        ApiKeys.IsWellFormed("Bearer eyJ...").ShouldBeFalse();
    }

    [Fact]
    public async Task Tenant_admin_creates_a_key_and_the_secret_is_shown_exactly_once()
    {
        using var admin = TenantAdminClient();

        var response = await admin.PostAsJsonAsync(KeysUri, new CreateApiKeyRequest("POS integration", [ApiKeyScopes.Ingest, ApiKeyScopes.Read]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<CreatedApiKey>(Ct))!;
        created.Key.ShouldStartWith("dpk_" + created.Prefix + "_");
        created.Scopes.ShouldBe([ApiKeyScopes.Ingest, ApiKeyScopes.Read], ignoreOrder: true);
        response.Headers.Location!.ToString().ShouldEndWith(created.Id.ToString());

        var list = (await admin.GetFromJsonAsync<List<ApiKeySummary>>(KeysUri, Ct))!;
        var summary = list.Single(k => k.Id == created.Id);
        summary.Prefix.ShouldBe(created.Prefix);
        summary.IsActive.ShouldBeTrue();
        System.Text.Json.JsonSerializer.Serialize(list).ShouldNotContain(created.Key);

        // Stored as hash only.
        var stored = factory.FindKey(created.Key)!;
        stored.Hash.ShouldBe(ApiKeys.Hash(created.Key));
        stored.TenantId.ShouldBe(ApiFactory.TenantA);
    }

    [Fact]
    public async Task A_key_authenticates_as_its_tenant_with_scope_permissions_only()
    {
        using var admin = TenantAdminClient();
        var created = (await (await admin.PostAsJsonAsync(KeysUri, new CreateApiKeyRequest("read-only", [ApiKeyScopes.Read]), Ct)).Content.ReadFromJsonAsync<CreatedApiKey>(Ct))!;

        foreach (var viaAuthorization in new[] { false, true })
        {
            using var machine = factory.ClientForApiKey(created.Key, viaAuthorization);
            var me = (await machine.GetFromJsonAsync<MeResponse>(MeUri, Ct))!;

            me.Subject.ShouldBe("apikey:" + created.Id.ToString("N"));
            me.Name.ShouldBe("read-only");
            me.AuthMethod.ShouldBe("apikey");
            me.Roles.ShouldBeEmpty();
            me.Scopes.ShouldBe([ApiKeyScopes.Read]);
            me.TenantId.ShouldBe(ApiFactory.TenantA);
            me.Scope.ShouldBe("tenant");
            me.Permissions.ShouldBe([Policies.Read]);
        }

        // Scopes map to policies one to one; a key never satisfies role-only policies such as TenantAdmin.
        using var readOnly = factory.ClientForApiKey(created.Key);
        (await readOnly.GetAsync(KeysUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ingest_scope_grants_the_ingest_policy()
    {
        var key = factory.InsertKey(ApiFactory.TenantA, "scanner", ApiKeyScope.Ingest);
        using var machine = factory.ClientForApiKey(key);

        var me = (await machine.GetFromJsonAsync<MeResponse>(MeUri, Ct))!;

        me.Permissions.ShouldBe([Policies.Ingest]);
    }

    [Fact]
    public async Task Revoked_unknown_malformed_and_suspended_tenant_keys_are_unauthorized()
    {
        using var admin = TenantAdminClient();
        var created = (await (await admin.PostAsJsonAsync(KeysUri, new CreateApiKeyRequest("to revoke", [ApiKeyScopes.Read]), Ct)).Content.ReadFromJsonAsync<CreatedApiKey>(Ct))!;
        (await admin.DeleteAsync(new Uri($"/api/v1/api-keys/{created.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync(new Uri($"/api/v1/api-keys/{created.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent, "revoke is idempotent");
        (await admin.GetFromJsonAsync<List<ApiKeySummary>>(KeysUri, Ct))!.Single(k => k.Id == created.Id).IsActive.ShouldBeFalse();

        var suspended = factory.InsertKey(ApiFactory.SuspendedTenant, "stale", ApiKeyScope.Read);

        foreach (var key in new[] { created.Key, ApiKeys.Generate().Key, "dpk_not_a_real_key", suspended })
        {
            using var machine = factory.ClientForApiKey(key);
            var response = await machine.GetAsync(MeUri, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, key);
            response.Headers.WwwAuthenticate.ToString().ShouldContain("ApiKey");
        }
    }

    [Fact]
    public async Task Keys_cannot_override_the_tenant_and_last_use_is_recorded_coarsely()
    {
        var key = factory.InsertKey(ApiFactory.TenantA, "pos", ApiKeyScope.Read);

        using var overriding = factory.ClientForApiKey(key, tenantHeader: Guid.NewGuid().ToString());
        (await overriding.GetAsync(MeUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var machine = factory.ClientForApiKey(key);
        (await machine.GetAsync(MeUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var first = factory.FindKey(key)!.LastUsedAt;
        first.ShouldBe(factory.Clock.GetUtcNow());

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        (await machine.GetAsync(MeUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.FindKey(key)!.LastUsedAt.ShouldBe(first, "within the resolution window no write happens");

        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        (await machine.GetAsync(MeUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.FindKey(key)!.LastUsedAt.ShouldBe(factory.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Only_tenant_admins_manage_keys_and_platform_scope_needs_a_tenant()
    {
        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], TenantA));
        (await reviewer.GetAsync(KeysUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var platform = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));
        (await platform.GetAsync(KeysUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var acting = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]), tenantHeader: TenantA);
        (await acting.GetAsync(KeysUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Invalid_requests_are_validation_problems()
    {
        using var admin = TenantAdminClient();

        var noScopes = await admin.PostAsJsonAsync(KeysUri, new CreateApiKeyRequest("x", []), Ct);
        noScopes.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await noScopes.Content.ReadAsStringAsync(Ct)).ShouldContain("scopes");

        var badScope = await admin.PostAsJsonAsync(KeysUri, new CreateApiKeyRequest("x", ["admin"]), Ct);
        badScope.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var noName = await admin.PostAsJsonAsync(KeysUri, new CreateApiKeyRequest("  ", [ApiKeyScopes.Read]), Ct);
        noName.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await noName.Content.ReadAsStringAsync(Ct)).ShouldContain("name");
    }

    [Fact]
    public async Task OpenApi_document_declares_the_api_key_scheme()
    {
        using var anonymous = factory.ClientFor(null);

        var json = await anonymous.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);

        json.ShouldContain("\"X-Api-Key\"");
    }
}
