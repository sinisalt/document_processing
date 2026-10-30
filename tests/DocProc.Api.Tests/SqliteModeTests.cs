using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Endpoints;
using DocProc.Application.Security;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Api.Tests;

/// <summary>
/// The API booted with <c>Persistence:Provider=Sqlite</c> against a temporary file: the schema is created from the
/// model, the development seed lands, and the API-key flow works end to end for the seeded tenants.
/// </summary>
public sealed class SqliteModeTests(DevModeFactory factory) : IClassFixture<DevModeFactory>
{
    private static readonly Uri KeysUri = new("/api/v1/api-keys", UriKind.Relative);
    private static readonly Uri MeUri = new("/api/v1/me", UriKind.Relative);
    private static readonly Uri ReadyUri = new("/health/ready", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Database_file_is_created_where_configured_and_readiness_is_green()
    {
        using var client = factory.CreateClient();

        (await client.GetAsync(ReadyUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        File.Exists(factory.DatabasePath).ShouldBeTrue();
        factory.Services.GetRequiredService<SqliteDatabaseLocation>().Path.ShouldBe(Path.GetFullPath(factory.DatabasePath));
    }

    [Fact]
    public async Task Seed_inserts_the_demo_tenants_once()
    {
        _ = factory.CreateClient();
        await using var db = factory.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();

        var tenants = await db.Tenants.AsNoTracking().OrderBy(t => t.Slug).ToListAsync(Ct);
        tenants.Select(t => (t.Id, t.Slug)).ShouldBe([(DevSeed.TenantA, "tenant-a"), (DevSeed.TenantB, "tenant-b")]);
        tenants[0].ShouldSatisfyAllConditions(
            t => t.Name.ShouldBe("Gasthaus zur Post GmbH"),
            t => t.CountryCode.ShouldBe("DE"),
            t => t.DefaultCurrency.ShouldBe("EUR"),
            t => t.TimeZone.ShouldBe("Europe/Berlin"),
            t => t.VatId.ShouldBe("DE123456789"),
            t => t.Status.ShouldBe(TenantStatus.Active));
        tenants[1].ShouldSatisfyAllConditions(
            t => t.Name.ShouldBe("Pizzeria Napoli d.o.o."),
            t => t.CountryCode.ShouldBe("HR"),
            t => t.TimeZone.ShouldBe("Europe/Zagreb"),
            t => t.VatId.ShouldBe("HR12345678901"));

        (await db.IngestionChannels.AsNoTracking().Where(c => c.Type == ChannelType.ApiUpload).Select(c => c.TenantId).ToListAsync(Ct))
            .ShouldBe([DevSeed.TenantA, DevSeed.TenantB], ignoreOrder: true);

        var suppliers = await db.Suppliers.AsNoTracking().ToListAsync(Ct);
        suppliers.Count(s => s.TenantId == DevSeed.TenantA).ShouldBe(3);
        suppliers.Count(s => s.TenantId == DevSeed.TenantB).ShouldBe(2);
        var metro = suppliers.Single(s => s.Id == DevSeed.MetroDeutschland);
        metro.Name.ShouldBe("METRO Deutschland GmbH");
        metro.VatId.ShouldBe("DE811177936");
        suppliers.Single(s => s.Id == DevSeed.GetraenkeHoffmann).NormalizedName.ShouldBe("GETRANKE HOFFMANN GMBH");

        var platform = await db.LlmProviderConfigs.AsNoTracking().SingleAsync(c => c.Id == DevSeed.PlatformAnthropicConfig, Ct);
        platform.ShouldSatisfyAllConditions(
            c => c.Scope.ShouldBe(ConfigScope.Platform),
            c => c.TenantId.ShouldBeNull(),
            c => c.Kind.ShouldBe(LlmProviderKind.Anthropic),
            c => c.SecretRef.ShouldBe(DevSeed.PlatformSecretRef),
            c => c.Status.ShouldBe(ProviderConfigStatus.Disabled),
            c => c.Models.Keys.ShouldBe([nameof(LlmStage.Classify), nameof(LlmStage.Split), nameof(LlmStage.Extract), nameof(LlmStage.Synthesize)], ignoreOrder: true),
            c => c.Models[nameof(LlmStage.Extract)].ShouldBe("claude-opus-5-5"),
            c => c.Models[nameof(LlmStage.Classify)].ShouldBe("claude-haiku-5-5"));
        platform.ToDomain().ModelFor(LlmStage.Synthesize).ShouldBe("claude-opus-5-5");

        var policy = await db.TenantLlmPolicies.AsNoTracking().SingleAsync(Ct);
        policy.TenantId.ShouldBe(DevSeed.TenantA);
        policy.FallbackToPlatformKey.ShouldBeTrue();
        policy.ToDomain().IsByok.ShouldBeFalse();

        // Idempotent: a second run (what every restart does) inserts nothing.
        (await DevSeed.ApplyAsync(db, TimeProvider.System, Ct)).ShouldBe(0);
        (await db.Tenants.CountAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Tenant_a_admin_creates_a_key_and_the_key_authenticates_as_tenant_a()
    {
        using var admin = await factory.ClientAsAsync("tenant-a-admin", Ct);

        var response = await admin.PostAsJsonAsync(KeysUri, new CreateApiKeyRequest("POS integration", [ApiKeyScopes.Ingest]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<CreatedApiKey>(Ct))!;
        created.Key.ShouldStartWith("dpk_");

        var list = (await admin.GetFromJsonAsync<List<ApiKeySummary>>(KeysUri, Ct))!;
        list.ShouldContain(k => k.Id == created.Id && k.IsActive);

        using var machine = factory.CreateClient();
        machine.DefaultRequestHeaders.Add("X-Api-Key", created.Key);
        var me = (await machine.GetFromJsonAsync<MeResponse>(MeUri, Ct))!;
        me.AuthMethod.ShouldBe("apikey");
        me.TenantId.ShouldBe(DevSeed.TenantA);
        me.Scopes.ShouldBe([ApiKeyScopes.Ingest]);
    }

    [Fact]
    public async Task Tenant_b_admin_cannot_see_tenant_a_keys()
    {
        using var adminA = await factory.ClientAsAsync("tenant-a-admin", Ct);
        var created = (await (await adminA.PostAsJsonAsync(KeysUri, new CreateApiKeyRequest("only-a", [ApiKeyScopes.Read]), Ct)).Content.ReadFromJsonAsync<CreatedApiKey>(Ct))!;

        using var adminB = await factory.ClientAsAsync("tenant-b-admin", Ct);
        var listB = (await adminB.GetFromJsonAsync<List<ApiKeySummary>>(KeysUri, Ct))!;

        listB.ShouldNotContain(k => k.Id == created.Id);
        (await adminB.DeleteAsync(new Uri($"/api/v1/api-keys/{created.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Superadmin_manages_tenant_b_keys_through_the_tenant_header()
    {
        using var superadmin = await factory.ClientAsAsync("superadmin", Ct, tenantHeader: DevSeed.TenantB.ToString());

        var created = await superadmin.PostAsJsonAsync(KeysUri, new CreateApiKeyRequest("tenant-b export", [ApiKeyScopes.Read]), Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var adminB = await factory.ClientAsAsync("tenant-b-admin", Ct);
        (await adminB.GetFromJsonAsync<List<ApiKeySummary>>(KeysUri, Ct))!.ShouldContain(k => k.Name == "tenant-b export");
    }
}
