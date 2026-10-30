using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Api.Tests;

public sealed class LlmAdminEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri ProvidersUri = new("/api/v1/llm-providers", UriKind.Relative);
    private static readonly Uri PolicyUri = new("/api/v1/llm-policy", UriKind.Relative);
    private static readonly Uri PlatformProvidersUri = new("/api/v1/llm/providers", UriKind.Relative);
    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient TenantAdminClient(string? tenant = null) => factory.ClientFor(new TokenSpec("tenant-admin", [Roles.TenantAdmin], tenant ?? TenantA));

    private HttpClient SuperAdmin() => factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));

    private static CreateLlmProviderRequest NewProvider(string apiKey, string kind = "anthropic") =>
        new(kind, "https://api.anthropic.com", null, apiKey, new Dictionary<string, string>(StringComparer.Ordinal) { ["extract"] = "claude-opus-5-5", ["Classify"] = "claude-haiku-5-5" }, 250m, "eu");

    [Fact]
    public async Task Creating_a_provider_keeps_the_key_out_of_the_database_and_in_the_secret_store()
    {
        using var admin = TenantAdminClient();
        const string apiKey = "sk-ant-api03-this-is-a-tenant-secret-key-XYZ9";

        var response = await admin.PostAsJsonAsync(ProvidersUri, NewProvider(apiKey), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(apiKey);
        body.ShouldNotContain("secretRef", Case.Insensitive);
        var created = (await response.Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;
        created.Scope.ShouldBe(nameof(ConfigScope.Tenant));
        created.TenantId.ShouldBe(ApiFactory.TenantA);
        created.Kind.ShouldBe(nameof(LlmProviderKind.Anthropic));
        created.AuthType.ShouldBe(nameof(LlmAuthType.ApiKey));
        created.Status.ShouldBe(nameof(ProviderConfigStatus.Active));
        created.KeyLast4.ShouldBe("XYZ9");
        created.DataResidency.ShouldBe(nameof(DataResidency.Eu));
        created.MonthlySpendCapUsd.ShouldBe(250m);
        created.Models.ShouldBe(new Dictionary<string, string>(StringComparer.Ordinal) { ["Extract"] = "claude-opus-5-5", ["Classify"] = "claude-haiku-5-5" });

        var row = factory.Query(db => db.LlmProviderConfigs.Single(c => c.Id == created.Id));
        row.SecretRef.ShouldBe($"tenant/{ApiFactory.TenantA}/{created.Id}");
        row.KeyLast4.ShouldBe("XYZ9");
        row.KeyFingerprint.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..16]);
        row.CreatedBy.ShouldBe("tenant-admin");
        foreach (var value in new object?[] { row.Endpoint, row.ApiVersion, row.LastError, row.CreatedBy, string.Join(";", row.Models.Values) })
        {
            (value?.ToString() ?? string.Empty).ShouldNotContain(apiKey);
        }

        var secrets = factory.Services.GetRequiredService<ISecretStore>();
        (await secrets.GetAsync(row.SecretRef!, Ct)).ShouldBe(apiKey);

        var list = (await admin.GetFromJsonAsync<List<LlmProviderResponse>>(ProvidersUri, Ct))!;
        list.ShouldContain(p => p.Id == created.Id);
        (await admin.GetStringAsync(ProvidersUri, Ct)).ShouldNotContain(apiKey);
    }

    [Fact]
    public async Task Provider_requests_are_validated()
    {
        using var admin = TenantAdminClient();

        var bad = await admin.PostAsJsonAsync(ProvidersUri, new CreateLlmProviderRequest("replay", "ftp://x", null, " ", new Dictionary<string, string>(StringComparer.Ordinal) { ["ocr"] = "m" }, -1m, "moon"), Ct);

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await bad.Content.ReadAsStringAsync(Ct);
        foreach (var field in new[] { "kind", "endpoint", "apiKey", "models", "monthlySpendCapUsd", "dataResidency" })
        {
            body.ShouldContain($"\"{field}\"");
        }

        (await admin.PostAsJsonAsync(ProvidersUri, NewProvider("k") with { Models = null }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.Query(db => db.LlmProviderConfigs.Count(c => c.TenantId == ApiFactory.TenantA && c.Kind == LlmProviderKind.Replay)).ShouldBe(0);

        // A key shorter than eight characters would be revealed almost whole by keyLast4.
        var shortKey = await admin.PostAsJsonAsync(ProvidersUri, NewProvider("abcdefg"), Ct);
        shortKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await shortKey.Content.ReadAsStringAsync(Ct)).ShouldContain("\"apiKey\"");
        (await admin.PostAsJsonAsync(ProvidersUri, NewProvider("abcd"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var eight = (await (await admin.PostAsJsonAsync(ProvidersUri, NewProvider("abcdefgh"), Ct)).Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;
        eight.KeyLast4.ShouldBe("efgh");

        // Stage names are case-insensitive, so two spellings of one stage are a contradiction, not a merge.
        var duplicated = await admin.PostAsJsonAsync(ProvidersUri, NewProvider("dup-key-12345678") with { Models = new Dictionary<string, string>(StringComparer.Ordinal) { ["extract"] = "model-a", ["Extract"] = "model-b" } }, Ct);
        duplicated.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await duplicated.Content.ReadAsStringAsync(Ct)).ShouldContain("\"models\"");
        factory.Query(db => db.LlmProviderConfigs.Any(c => c.KeyLast4 == "5678")).ShouldBeFalse();

        // Nor is a stage its ordinal.
        var numericStage = await admin.PostAsJsonAsync(ProvidersUri, NewProvider("num-key-12345678") with { Models = new Dictionary<string, string>(StringComparer.Ordinal) { ["0"] = "model-a" } }, Ct);
        numericStage.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await numericStage.Content.ReadAsStringAsync(Ct)).ShouldContain("\"models\"");
    }

    [Fact]
    public async Task Deleting_a_provider_removes_its_key_from_the_secret_store()
    {
        using var admin = TenantAdminClient();
        const string apiKey = "delete-me-key-00001234";
        var created = (await (await admin.PostAsJsonAsync(ProvidersUri, NewProvider(apiKey), Ct)).Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;
        var secrets = factory.Services.GetRequiredService<ISecretStore>();
        var reference = $"tenant/{ApiFactory.TenantA}/{created.Id}";
        (await secrets.GetAsync(reference, Ct)).ShouldBe(apiKey);

        (await admin.DeleteAsync(new Uri($"/api/v1/llm-providers/{created.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        factory.Query(db => db.LlmProviderConfigs.Any(c => c.Id == created.Id)).ShouldBeFalse();
        (await secrets.GetAsync(reference, Ct)).ShouldBeNull("the key does not outlive its configuration");
        (await admin.DeleteAsync(new Uri($"/api/v1/llm-providers/{created.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Policy_rejects_configurations_of_another_tenant_and_unknown_stages()
    {
        using var adminA = TenantAdminClient();
        using var adminB = TenantAdminClient(ApiFactory.TenantB.ToString());
        var own = (await (await adminA.PostAsJsonAsync(ProvidersUri, NewProvider("own-key-0001"), Ct)).Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;
        var foreign = (await (await adminB.PostAsJsonAsync(ProvidersUri, NewProvider("foreign-key-0002"), Ct)).Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;

        (await adminA.GetFromJsonAsync<LlmPolicyResponse>(PolicyUri, Ct))!.ShouldMatch(LlmPolicyResponse.Default());

        var foreignDefault = await adminA.PutAsJsonAsync(PolicyUri, new PutLlmPolicyRequest(foreign.Id, null, false), Ct);
        foreignDefault.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await foreignDefault.Content.ReadAsStringAsync(Ct)).ShouldContain(foreign.Id.ToString());

        var foreignOverride = await adminA.PutAsJsonAsync(PolicyUri, new PutLlmPolicyRequest(own.Id, new Dictionary<string, Guid>(StringComparer.Ordinal) { ["extract"] = foreign.Id }, false), Ct);
        foreignOverride.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var unknownStage = await adminA.PutAsJsonAsync(PolicyUri, new PutLlmPolicyRequest(own.Id, new Dictionary<string, Guid>(StringComparer.Ordinal) { ["ocr"] = own.Id }, false), Ct);
        unknownStage.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Platform configurations are visible to the tenant's gateway but are not the tenant's to pick either.
        var platformId = factory.Query(db => db.LlmProviderConfigs.Where(c => c.Scope == ConfigScope.Platform).Select(c => c.Id).FirstOrDefault());
        if (platformId != Guid.Empty)
        {
            (await adminA.PutAsJsonAsync(PolicyUri, new PutLlmPolicyRequest(platformId, null, true), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var ok = await adminA.PutAsJsonAsync(PolicyUri, new PutLlmPolicyRequest(own.Id, new Dictionary<string, Guid>(StringComparer.Ordinal) { ["classify"] = own.Id }, true), Ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var policy = (await ok.Content.ReadFromJsonAsync<LlmPolicyResponse>(Ct))!;
        policy.DefaultConfigId.ShouldBe(own.Id);
        policy.StageOverrides.ShouldBe(new Dictionary<string, Guid>(StringComparer.Ordinal) { [nameof(LlmStage.Classify)] = own.Id });
        policy.FallbackToPlatformKey.ShouldBeTrue();
        (await adminA.GetFromJsonAsync<LlmPolicyResponse>(PolicyUri, Ct)).ShouldMatch(policy);
        (await adminB.GetFromJsonAsync<LlmPolicyResponse>(PolicyUri, Ct))!.DefaultConfigId.ShouldBeNull("B has its own policy row, or none");

        // A configuration the policy points at cannot be deleted; another one can.
        (await adminA.DeleteAsync(new Uri($"/api/v1/llm-providers/{own.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await adminA.DeleteAsync(new Uri($"/api/v1/llm-providers/{foreign.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "not A's configuration");
        (await adminA.PutAsJsonAsync(PolicyUri, new PutLlmPolicyRequest(null, null, true), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await adminA.DeleteAsync(new Uri($"/api/v1/llm-providers/{own.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Query(db => db.LlmProviderConfigs.Any(c => c.Id == own.Id)).ShouldBeFalse();
        factory.Query(db => db.LlmProviderConfigs.Any(c => c.Id == foreign.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task Tenant_disable_and_enable_flip_the_status_within_the_tenant()
    {
        using var admin = TenantAdminClient();
        var created = (await (await admin.PostAsJsonAsync(ProvidersUri, NewProvider("flip-key-1234"), Ct)).Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;

        var disabled = (await (await admin.PostAsync(new Uri($"/api/v1/llm-providers/{created.Id}:disable", UriKind.Relative), null, Ct)).Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;
        disabled.Status.ShouldBe(nameof(ProviderConfigStatus.Disabled));
        factory.Query(db => db.LlmProviderConfigs.Single(c => c.Id == created.Id).Status).ShouldBe(ProviderConfigStatus.Disabled);

        var enabled = (await (await admin.PostAsync(new Uri($"/api/v1/llm-providers/{created.Id}:enable", UriKind.Relative), null, Ct)).Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;
        enabled.Status.ShouldBe(nameof(ProviderConfigStatus.Active));

        using var adminB = TenantAdminClient(ApiFactory.TenantB.ToString());
        (await adminB.PostAsync(new Uri($"/api/v1/llm-providers/{created.Id}:disable", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.GetFromJsonAsync<List<LlmProviderResponse>>(ProvidersUri, Ct))!.ShouldNotContain(p => p.Id == created.Id);

        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], TenantA));
        (await reviewer.GetAsync(ProvidersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reviewer.GetAsync(PolicyUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Usage_aggregates_the_tenant_rows_by_day_stage_or_model()
    {
        // A tenant of its own: the buckets and totals are exact, so no other test's usage rows may be in them.
        var tenant = factory.CreateTenant("Usage");
        var configId = Guid.CreateVersion7();
        var day1 = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 10, 21, 23, 30, 0, TimeSpan.Zero);
        factory.Run(db =>
        {
            db.LlmUsage.Add(Usage(tenant, configId, LlmStage.Classify, "haiku", day1, 100, 10, 0.001m));
            db.LlmUsage.Add(Usage(tenant, configId, LlmStage.Extract, "opus", day1.AddHours(2), 5000, 800, 0.2m));
            db.LlmUsage.Add(Usage(tenant, configId, LlmStage.Extract, "opus", day2, 7000, 900, 0.3m, LlmErrorKind.RateLimited));
            db.LlmUsage.Add(Usage(tenant, configId, LlmStage.Split, "haiku", day2.AddDays(5), 1, 1, 0.5m));
            db.LlmUsage.Add(Usage(ApiFactory.TenantB, configId, LlmStage.Extract, "opus", day1, 9999, 9999, 9m));
        });

        using var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], tenant.ToString()));
        var byDay = (await viewer.GetFromJsonAsync<LlmUsageResponse>(new Uri("/api/v1/llm-usage?from=2026-10-20&to=2026-10-21&groupBy=day", UriKind.Relative), Ct))!;

        byDay.From.ShouldBe(new DateOnly(2026, 10, 20));
        byDay.To.ShouldBe(new DateOnly(2026, 10, 21));
        byDay.Buckets.Select(b => b.Key).ShouldBe(["2026-10-20", "2026-10-21"]);
        byDay.Buckets[0].ShouldBe(new LlmUsageBucket("2026-10-20", 2, 0, 5100, 810, 0, 0, 0, 0.201m));
        byDay.Buckets[1].ShouldBe(new LlmUsageBucket("2026-10-21", 1, 1, 7000, 900, 0, 0, 0, 0.3m));
        byDay.Total.Calls.ShouldBe(3);
        byDay.Total.EstimatedCostUsd.ShouldBe(0.501m);

        var byStage = (await viewer.GetFromJsonAsync<LlmUsageResponse>(new Uri("/api/v1/llm-usage?from=2026-10-20&to=2026-10-21&groupBy=stage", UriKind.Relative), Ct))!;
        byStage.Buckets.Select(b => (b.Key, b.Calls)).ShouldBe([("Classify", 1), ("Extract", 2)]);

        var byModel = (await viewer.GetFromJsonAsync<LlmUsageResponse>(new Uri("/api/v1/llm-usage?from=2026-10-01&to=2026-10-31&groupBy=model", UriKind.Relative), Ct))!;
        byModel.Buckets.Select(b => (b.Key, b.Calls)).ShouldBe([("haiku", 2), ("opus", 2)]);

        // Default window: the last 30 days up to the clock's today.
        var defaults = (await viewer.GetFromJsonAsync<LlmUsageResponse>(new Uri("/api/v1/llm-usage", UriKind.Relative), Ct))!;
        defaults.To.ShouldBe(new DateOnly(2026, 10, 30));
        defaults.From.ShouldBe(new DateOnly(2026, 9, 30));
        defaults.GroupBy.ShouldBe("day");
        defaults.Total.Calls.ShouldBe(4);

        (await viewer.GetAsync(new Uri("/api/v1/llm-usage?groupBy=tenant", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await viewer.GetAsync(new Uri("/api/v1/llm-usage?from=2026-10-21&to=2026-10-20", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Platform_admin_manages_platform_providers_and_the_kill_switch_and_sees_usage_across_tenants()
    {
        using var superAdmin = SuperAdmin();
        const string apiKey = "platform-key-EU01";

        var response = await superAdmin.PostAsJsonAsync(PlatformProvidersUri, NewProvider(apiKey, "AzureOpenAi") with { Endpoint = "https://docproc-eu.openai.azure.com", ApiVersion = "2025-04-01-preview" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;
        created.Scope.ShouldBe(nameof(ConfigScope.Platform));
        created.TenantId.ShouldBeNull();
        created.Kind.ShouldBe(nameof(LlmProviderKind.AzureOpenAi));
        created.KeyLast4.ShouldBe("EU01");
        var row = factory.Query(db => db.LlmProviderConfigs.Single(c => c.Id == created.Id));
        row.SecretRef.ShouldBe($"platform/{created.Id}");
        row.TenantId.ShouldBeNull();
        (await factory.Services.GetRequiredService<ISecretStore>().GetAsync(row.SecretRef!, Ct)).ShouldBe(apiKey);

        (await superAdmin.GetFromJsonAsync<List<LlmProviderResponse>>(PlatformProvidersUri, Ct))!.ShouldContain(p => p.Id == created.Id);
        (await superAdmin.GetStringAsync(PlatformProvidersUri, Ct)).ShouldNotContain(apiKey);

        var killed = (await (await superAdmin.PostAsync(new Uri($"/api/v1/llm/providers/{created.Id}:disable", UriKind.Relative), null, Ct)).Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;
        killed.Status.ShouldBe(nameof(ProviderConfigStatus.Disabled));
        factory.Query(db => db.LlmProviderConfigs.Single(c => c.Id == created.Id).Status).ShouldBe(ProviderConfigStatus.Disabled);
        var revived = (await (await superAdmin.PostAsync(new Uri($"/api/v1/llm/providers/{created.Id}:enable", UriKind.Relative), null, Ct)).Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;
        revived.Status.ShouldBe(nameof(ProviderConfigStatus.Active));

        // The kill switch is for platform configurations only; a tenant's own one is managed through the tenant endpoints.
        using var tenantAdmin = TenantAdminClient();
        var tenantConfig = (await (await tenantAdmin.PostAsJsonAsync(ProvidersUri, NewProvider("tenant-key-7777"), Ct)).Content.ReadFromJsonAsync<LlmProviderResponse>(Ct))!;
        (await superAdmin.PostAsync(new Uri($"/api/v1/llm/providers/{tenantConfig.Id}:disable", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await superAdmin.GetFromJsonAsync<List<LlmProviderResponse>>(PlatformProvidersUri, Ct))!.ShouldNotContain(p => p.Id == tenantConfig.Id);

        using var op = factory.ClientFor(new TokenSpec("operator", [Roles.Operator]));
        (await op.GetAsync(PlatformProvidersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await op.PostAsync(new Uri($"/api/v1/llm/providers/{created.Id}:disable", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await tenantAdmin.GetAsync(PlatformProvidersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Two tenants of this test's own on a day no other test writes usage for, so the cross-tenant buckets are exact.
        var tenantX = factory.CreateTenant("Usage X");
        var tenantY = factory.CreateTenant("Usage Y");
        var at = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        factory.Run(db =>
        {
            db.LlmUsage.Add(Usage(tenantX, created.Id, LlmStage.Extract, "gpt", at, 10, 1, 0.01m));
            db.LlmUsage.Add(Usage(tenantY, created.Id, LlmStage.Extract, "gpt", at, 20, 2, 0.02m));
        });
        var usage = (await superAdmin.GetFromJsonAsync<LlmUsageResponse>(new Uri("/api/v1/llm/usage?from=2026-09-01&to=2026-09-01", UriKind.Relative), Ct))!;
        usage.GroupBy.ShouldBe("tenant");
        usage.Buckets.Select(b => (b.Key, b.InputTokens)).ShouldBe([(tenantX.ToString(), 10L), (tenantY.ToString(), 20L)], ignoreOrder: true);
        usage.Buckets.Select(b => b.Key).ShouldBeInOrder(SortDirection.Ascending, StringComparer.Ordinal);
        usage.Total.EstimatedCostUsd.ShouldBe(0.03m);
        (await superAdmin.GetAsync(new Uri("/api/v1/llm/usage?groupBy=stage", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static LlmUsageEntry Usage(Guid tenantId, Guid configId, LlmStage stage, string model, DateTimeOffset at, int input, int output, decimal cost, LlmErrorKind? error = null) => new()
    {
        TenantId = tenantId,
        ConfigId = configId,
        Stage = stage,
        KeyOwner = KeyOwner.Tenant,
        Provider = LlmProviderKind.Anthropic,
        Model = model,
        PromptVersion = "v1",
        InputTokens = input,
        OutputTokens = output,
        EstimatedCostUsd = cost,
        LatencyMs = 100,
        StopReason = error is null ? "end_turn" : "error",
        RequestHash = new string('a', 64),
        Error = error,
        At = at,
    };
}
