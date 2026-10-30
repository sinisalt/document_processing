using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;

namespace DocProc.Api.Tests;

public sealed class TenantSettingsEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri SettingsUri = new("/api/v1/settings", UriKind.Relative);
    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient TenantAdminClient() => factory.ClientFor(new TokenSpec("tenant-a-admin", [Roles.TenantAdmin], TenantA));

    [Fact]
    public async Task Get_returns_the_identity_and_the_defaults()
    {
        using var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], TenantA));

        var response = (await viewer.GetFromJsonAsync<TenantSettingsResponse>(SettingsUri, Ct))!;

        response.Tenant.ShouldBe(new TenantIdentity(ApiFactory.TenantA, "Tenant A", "tenant-a", "Gasthaus zur Post GmbH", null, "EUR", "DE", "Europe/Berlin"));
        response.Settings.AutoApproveConfidenceThreshold.ShouldBe(0.8);
        response.Settings.RetentionYears.ShouldBe(10);
        response.Settings.ShareExtractorProfiles.ShouldBeTrue();
        response.Settings.AlwaysReviewSupplierIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Put_validates_the_ranges_and_the_supplier_ids_then_persists()
    {
        using var admin = TenantAdminClient();

        var tooHigh = await admin.PutAsJsonAsync(SettingsUri, new TenantSettingsDto(1.5, [], 10, true), Ct);
        tooHigh.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooHigh.Content.ReadAsStringAsync(Ct)).ShouldContain("autoApproveConfidenceThreshold");

        var negative = await admin.PutAsJsonAsync(SettingsUri, new TenantSettingsDto(-0.1, [], 10, true), Ct);
        negative.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var retention = await admin.PutAsJsonAsync(SettingsUri, new TenantSettingsDto(0.5, [], 31, true), Ct);
        retention.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await retention.Content.ReadAsStringAsync(Ct)).ShouldContain("retentionYears");

        var unknownSupplier = await admin.PutAsJsonAsync(SettingsUri, new TenantSettingsDto(0.5, [Guid.NewGuid()], 10, true), Ct);
        unknownSupplier.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknownSupplier.Content.ReadAsStringAsync(Ct)).ShouldContain("alwaysReviewSupplierIds");

        var supplier = (await (await admin.PostAsJsonAsync(new Uri("/api/v1/suppliers", UriKind.Relative), new CreateSupplierRequest("Always review me", null, null, null, null, null, null, null), Ct)).Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        var ok = await admin.PutAsJsonAsync(SettingsUri, new TenantSettingsDto(0.95, [supplier.Id, supplier.Id], 8, false), Ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = (await ok.Content.ReadFromJsonAsync<TenantSettingsResponse>(Ct))!;
        saved.Settings.ShouldMatch(new TenantSettingsDto(0.95, [supplier.Id], 8, false));

        var stored = factory.Query(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA));
        stored.Settings.AutoApproveConfidenceThreshold.ShouldBe(0.95);
        stored.Settings.AlwaysReviewSupplierIds.ShouldBe([supplier.Id]);
        stored.Settings.RetentionYears.ShouldBe(8);
        stored.Settings.ShareExtractorProfiles.ShouldBeFalse();
        stored.UpdatedAt.ShouldBe(factory.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Only_tenant_admins_write_and_a_tenant_is_required()
    {
        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], TenantA));
        (await reviewer.GetAsync(SettingsUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reviewer.PutAsJsonAsync(SettingsUri, new TenantSettingsDto(0.5, [], 10, true), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var platform = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));
        (await platform.GetAsync(SettingsUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var acting = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]), tenantHeader: Guid.NewGuid().ToString());
        (await acting.GetAsync(SettingsUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var tenantB = factory.ClientFor(new TokenSpec("tenant-b-admin", [Roles.TenantAdmin], ApiFactory.TenantB.ToString()));
        (await tenantB.GetFromJsonAsync<TenantSettingsResponse>(SettingsUri, Ct))!.Tenant.Id.ShouldBe(ApiFactory.TenantB);
    }
}
