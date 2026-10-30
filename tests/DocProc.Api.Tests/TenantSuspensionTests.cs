using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;

namespace DocProc.Api.Tests;

/// <summary>Suspension is enforced for people (bearer tokens) by <see cref="TenantResolutionMiddleware"/>, not only for API keys.</summary>
public sealed class TenantSuspensionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri SuppliersUri = new("/api/v1/suppliers", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateSupplierRequest NewSupplier(string name) => new(name, null, null, null, null, null, null, null);

    [Fact]
    public async Task A_suspended_tenants_admin_is_refused_until_the_tenant_is_activated_and_the_cache_expires()
    {
        var tenant = factory.CreateTenant("Suspended customer", TenantStatus.Suspended);
        using var admin = factory.TenantAdminOf(tenant);

        var get = await admin.GetAsync(SuppliersUri, Ct);
        get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        get.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        JsonDocument.Parse(await get.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("title").GetString().ShouldBe("Tenant suspended");

        (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier("While suspended"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Query(db => db.Suppliers.Any(s => s.TenantId == tenant)).ShouldBeFalse("nothing was written");

        using var superAdmin = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));
        (await superAdmin.PostAsync(new Uri($"/api/v1/tenants/{tenant}:activate", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The status is cached per tenant for 30 seconds on the application clock.
        factory.Clock.Advance(TenantStatusCache.Ttl + TimeSpan.FromSeconds(1));
        (await admin.GetAsync(SuppliersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier("After activation"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // And suspension bites again once the cache has turned over.
        (await superAdmin.PostAsync(new Uri($"/api/v1/tenants/{tenant}:suspend", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Clock.Advance(TenantStatusCache.Ttl + TimeSpan.FromSeconds(1));
        (await admin.GetAsync(SuppliersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_status_change_locks_the_tenants_users_out_or_lets_them_back_in_at_once_on_this_node()
    {
        var tenant = factory.CreateTenant("Instant suspension");
        using var admin = factory.TenantAdminOf(tenant);
        using var superAdmin = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));
        var tenantUri = new Uri($"/api/v1/tenants/{tenant}", UriKind.Relative);

        (await admin.GetAsync(SuppliersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the status is now cached as Active");

        // The endpoints invalidate the cache after the write, so no clock advance is needed on the node that took the change.
        (await superAdmin.PostAsync(new Uri($"/api/v1/tenants/{tenant}:suspend", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var refused = await admin.GetAsync(SuppliersUri, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("title").GetString().ShouldBe("Tenant suspended");

        (await superAdmin.PostAsync(new Uri($"/api/v1/tenants/{tenant}:activate", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync(SuppliersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // PATCH with a status change invalidates too; one without a status change leaves the cache alone (still Active).
        (await superAdmin.PatchAsJsonAsync(tenantUri, new PatchTenantRequest("Renamed while active", null, null, null, null), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync(SuppliersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await superAdmin.PatchAsJsonAsync(tenantUri, new PatchTenantRequest(null, null, null, null, "suspended"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync(SuppliersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await superAdmin.PatchAsJsonAsync(tenantUri, new PatchTenantRequest(null, null, null, null, "Active"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync(SuppliersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_bearer_token_that_merely_claims_to_be_an_api_key_is_still_checked_while_a_real_key_keeps_its_401()
    {
        var tenant = factory.CreateTenant("Claim forgery", TenantStatus.Suspended);

        // The skip for API keys is keyed on the authentication scheme, so a JWT carrying auth_method=apikey is a person's token like any other.
        var token = ApiFactory.MintWithClaims(
            new TokenSpec("forger", [Roles.TenantAdmin], tenant.ToString()),
            new Dictionary<string, object> { [ApiKeyAuthenticationHandler.AuthMethodClaimType] = ApiKeyAuthenticationHandler.AuthMethodValue });
        using var forged = factory.ClientForBearer(token);
        var response = await forged.GetAsync(SuppliersUri, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("title").GetString().ShouldBe("Tenant suspended");

        // A real key of the suspended tenant is refused where it always was: by the key validator, as 401.
        var key = factory.InsertKey(tenant, "machine", ApiKeyScope.Read);
        using var machine = factory.ClientForApiKey(key);
        var machineResponse = await machine.GetAsync(SuppliersUri, Ct);
        machineResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        machineResponse.Headers.WwwAuthenticate.ToString().ShouldContain("ApiKey");
    }

    [Fact]
    public void The_api_key_skip_is_keyed_on_the_authentication_scheme_not_on_a_claim()
    {
        var tenant = TenantContext.Anonymous();
        tenant.SetTenant(Guid.NewGuid());
        Claim[] claims = [new("tenant_id", tenant.TenantId.ToString()!), new(ApiKeyAuthenticationHandler.AuthMethodClaimType, ApiKeyAuthenticationHandler.AuthMethodValue)];

        var bearer = new ClaimsPrincipal(new ClaimsIdentity(claims, "AuthenticationTypes.Federation", "preferred_username", "roles"));
        TenantResolutionMiddleware.ClaimedTenantNeedsStatusCheck(bearer, null, tenant).ShouldBeTrue("a bearer token is checked whatever its claims say");

        var apiKey = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.SchemeName, "preferred_username", "roles"));
        TenantResolutionMiddleware.ClaimedTenantNeedsStatusCheck(apiKey, null, tenant).ShouldBeFalse("a principal the API-key scheme produced was checked by the key validator");

        TenantResolutionMiddleware.ClaimedTenantNeedsStatusCheck(bearer, tenant.TenantId.ToString(), tenant).ShouldBeFalse("the SuperAdmin override is platform staff at work");
    }

    [Fact]
    public async Task Every_tenant_role_of_a_suspended_tenant_is_refused_on_every_tenant_endpoint()
    {
        var tenant = factory.CreateTenant("Suspended roles", TenantStatus.Suspended);

        foreach (var role in new[] { Roles.TenantAdmin, Roles.Reviewer, Roles.Viewer })
        {
            using var client = factory.ClientFor(new TokenSpec("user-" + role, [role], tenant.ToString()));
            foreach (var path in new[] { "/api/v1/suppliers", "/api/v1/documents", "/api/v1/settings", "/api/v1/channels", "/api/v1/me" })
            {
                (await client.GetAsync(new Uri(path, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{role} on {path}");
            }
        }
    }

    [Fact]
    public async Task A_token_for_a_tenant_that_does_not_exist_is_refused_with_404()
    {
        using var admin = factory.TenantAdminOf(Guid.NewGuid());

        var response = await admin.GetAsync(SuppliersUri, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("title").GetString().ShouldBe("Unknown tenant");
    }

    [Fact]
    public async Task Platform_staff_are_not_affected_and_a_super_admin_may_still_look_into_a_suspended_tenant()
    {
        var tenant = factory.CreateTenant("Suspended but inspectable", TenantStatus.Suspended);

        using var op = factory.ClientFor(new TokenSpec("operator", [Roles.Operator]));
        (await op.GetAsync(new Uri($"/api/v1/tenants/{tenant}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var acting = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]), tenantHeader: tenant.ToString());
        var settings = await acting.GetAsync(new Uri("/api/v1/settings", UriKind.Relative), Ct);
        settings.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await settings.Content.ReadFromJsonAsync<TenantSettingsResponse>(Ct))!.Tenant.Id.ShouldBe(tenant);
    }

    [Fact]
    public async Task An_active_tenants_users_are_unaffected()
    {
        var tenant = factory.CreateTenant("Active customer");
        using var admin = factory.TenantAdminOf(tenant);

        (await admin.GetAsync(SuppliersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier("Active supplier"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}
