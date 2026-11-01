using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Tests;

public sealed class TenantEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri TenantsUri = new("/api/v1/tenants", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient SuperAdmin() => factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));

    private HttpClient Operator() => factory.ClientFor(new TokenSpec("operator", [Roles.Operator]));

    private static CreateTenantRequest NewTenant(string slug) =>
        new($"Tenant {slug}", slug, $"Legal {slug} GmbH", "DE999999999", "EUR", "DE", "Europe/Berlin");

    [Fact]
    public async Task Super_admin_creates_a_tenant_with_its_two_default_channels()
    {
        using var admin = SuperAdmin();

        var response = await admin.PostAsJsonAsync(TenantsUri, NewTenant("new-restaurant"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<TenantResponse>(Ct))!;
        response.Headers.Location!.ToString().ShouldEndWith(created.Id.ToString());
        created.Slug.ShouldBe("new-restaurant");
        created.Status.ShouldBe(nameof(TenantStatus.Active));
        created.VatId.ShouldBe("DE999999999");
        created.CreatedAt.ShouldBe(factory.Clock.GetUtcNow());

        var channels = factory.Query(db => db.IngestionChannels.Where(c => c.TenantId == created.Id).ToList());
        channels.Select(c => c.Type).ShouldBe([ChannelType.ApiUpload, ChannelType.PortalUpload], ignoreOrder: true);
        channels.ShouldAllBe(c => c.Status == ChannelStatus.Active && c.TenantId == created.Id);

        var fetched = (await admin.GetFromJsonAsync<TenantResponse>(new Uri($"/api/v1/tenants/{created.Id}", UriKind.Relative), Ct))!;
        fetched.ShouldMatch(created);

        // The new tenant is visible to its own admin without any X-Tenant-Id gymnastics.
        using var tenantAdmin = factory.ClientFor(new TokenSpec("new-admin", [Roles.TenantAdmin], created.Id.ToString()));
        var settings = (await tenantAdmin.GetFromJsonAsync<TenantSettingsResponse>(new Uri("/api/v1/settings", UriKind.Relative), Ct))!;
        settings.Tenant.Slug.ShouldBe("new-restaurant");
    }

    [Fact]
    public async Task Operator_can_list_and_read_but_not_create_and_tenant_users_see_nothing()
    {
        using var op = Operator();

        var page = (await op.GetFromJsonAsync<Page<TenantResponse>>(TenantsUri, Ct))!;
        page.Items.Select(t => t.Id).ShouldContain(ApiFactory.TenantA);
        (await op.GetAsync(new Uri($"/api/v1/tenants/{ApiFactory.TenantA}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await op.PostAsJsonAsync(TenantsUri, NewTenant("operator-made"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await op.PostAsync(new Uri($"/api/v1/tenants/{ApiFactory.TenantA}:suspend", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var tenantAdmin = factory.ClientFor(new TokenSpec("tenant-a-admin", [Roles.TenantAdmin], ApiFactory.TenantA.ToString()));
        (await tenantAdmin.GetAsync(TenantsUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        factory.Query(db => db.Tenants.Any(t => t.Slug == "operator-made")).ShouldBeFalse();
    }

    [Fact]
    public async Task Listing_filters_by_status_and_text_and_pages_by_slug()
    {
        using var admin = SuperAdmin();
        // A prefix of this test's own, so the text search never matches tenants other tests created.
        var prefix = "pg" + Guid.NewGuid().ToString("N")[..6] + "-";
        var slugs = new[] { prefix + "a", prefix + "b", prefix + "c" };
        foreach (var slug in slugs)
        {
            (await admin.PostAsJsonAsync(TenantsUri, NewTenant(slug), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var suspended = (await admin.GetFromJsonAsync<Page<TenantResponse>>(new Uri("/api/v1/tenants?status=suspended", UriKind.Relative), Ct))!;
        suspended.Items.ShouldAllBe(t => t.Status == nameof(TenantStatus.Suspended));
        suspended.Items.Select(t => t.Id).ShouldContain(ApiFactory.SuspendedTenant);

        var searched = (await admin.GetFromJsonAsync<Page<TenantResponse>>(new Uri($"/api/v1/tenants?q={prefix}", UriKind.Relative), Ct))!;
        searched.Items.Select(t => t.Slug).ShouldBe(slugs);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var uri = new Uri($"/api/v1/tenants?q={prefix}&limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), UriKind.Relative);
            var page = (await admin.GetFromJsonAsync<Page<TenantResponse>>(uri, Ct))!;
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            seen.AddRange(page.Items.Select(t => t.Slug));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.ShouldBe(slugs);

        (await admin.GetAsync(new Uri("/api/v1/tenants?status=nope", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync(new Uri("/api/v1/tenants?cursor=not-a-cursor!", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_validates_every_field_and_refuses_a_taken_slug()
    {
        using var admin = SuperAdmin();

        var bad = await admin.PostAsJsonAsync(TenantsUri, new CreateTenantRequest(" ", "Not A Slug", "", null, "eur", "Germany", "Mars/Olympus"), Ct);

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await bad.Content.ReadAsStringAsync(Ct);
        foreach (var field in new[] { "name", "slug", "legalName", "defaultCurrency", "countryCode", "timeZone" })
        {
            body.ShouldContain($"\"{field}\"");
        }

        (await admin.PostAsJsonAsync(TenantsUri, NewTenant("tenant-a"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Patch_suspend_and_activate_change_the_tenant()
    {
        using var admin = SuperAdmin();
        var created = (await (await admin.PostAsJsonAsync(TenantsUri, NewTenant("to-change"), Ct)).Content.ReadFromJsonAsync<TenantResponse>(Ct))!;
        var uri = new Uri($"/api/v1/tenants/{created.Id}", UriKind.Relative);

        var patched = await admin.PatchAsJsonAsync(uri, new PatchTenantRequest("Renamed", null, "", ["Renamed", " Renamed ", ""], null), Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = (await patched.Content.ReadFromJsonAsync<TenantResponse>(Ct))!;
        after.Name.ShouldBe("Renamed");
        after.LegalName.ShouldBe(created.LegalName);
        after.VatId.ShouldBeNull();
        after.NameAliases.ShouldBe(["Renamed"]);

        (await admin.PatchAsJsonAsync(uri, new PatchTenantRequest(null, null, null, null, "Gone"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var suspended = (await (await admin.PostAsync(new Uri($"/api/v1/tenants/{created.Id}:suspend", UriKind.Relative), null, Ct)).Content.ReadFromJsonAsync<TenantResponse>(Ct))!;
        suspended.Status.ShouldBe(nameof(TenantStatus.Suspended));
        factory.Query(db => db.Tenants.Single(t => t.Id == created.Id).Status).ShouldBe(TenantStatus.Suspended);

        var activated = (await (await admin.PostAsync(new Uri($"/api/v1/tenants/{created.Id}:activate", UriKind.Relative), null, Ct)).Content.ReadFromJsonAsync<TenantResponse>(Ct))!;
        activated.Status.ShouldBe(nameof(TenantStatus.Active));

        (await admin.PostAsync(new Uri($"/api/v1/tenants/{Guid.NewGuid()}:suspend", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync(new Uri($"/api/v1/tenants/{Guid.NewGuid()}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
