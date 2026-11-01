using DocProc.Domain.Documents;
using DocProc.Llm.Gateway;
using DocProc.Llm.Providers;

namespace DocProc.Llm.Tests;

public class LlmConfigResolverTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static LlmProviderConfig Config(ConfigScope scope, LlmProviderKind kind, ProviderConfigStatus status = ProviderConfigStatus.Active, params LlmStage[] stages) => new()
    {
        Id = Guid.NewGuid(),
        Scope = scope,
        TenantId = scope == ConfigScope.Tenant ? TenantId : null,
        Kind = kind,
        Auth = new LlmAuth(LlmAuthType.ApiKey, $"llm-{kind}".ToLowerInvariant()),
        Models = (stages.Length == 0 ? Enum.GetValues<LlmStage>() : stages).ToDictionary(s => s, s => $"{kind}-{s}".ToLowerInvariant()),
        Status = status,
    };

    private static readonly LlmProviderConfig Platform = Config(ConfigScope.Platform, LlmProviderKind.Anthropic);

    [Fact]
    public void Tenant_without_policy_uses_the_platform_default()
    {
        var resolved = LlmConfigResolver.Resolve(LlmStage.Extract, null, new Dictionary<Guid, LlmProviderConfig>(), Platform);

        resolved.Config.ShouldBe(Platform);
        resolved.KeyOwner.ShouldBe(KeyOwner.Platform);
        resolved.Model.ShouldBe("anthropic-extract");
        resolved.UsedFallback.ShouldBeFalse();
    }

    [Fact]
    public void Stage_override_beats_tenant_default_beats_platform()
    {
        var tenantDefault = Config(ConfigScope.Tenant, LlmProviderKind.AzureOpenAi);
        var extractOverride = Config(ConfigScope.Tenant, LlmProviderKind.AnthropicFoundry, stages: LlmStage.Extract);
        var policy = new TenantLlmPolicy
        {
            TenantId = TenantId,
            DefaultConfigId = tenantDefault.Id,
            StageOverrides = new Dictionary<LlmStage, Guid> { [LlmStage.Extract] = extractOverride.Id },
        };
        var configs = new[] { tenantDefault, extractOverride }.ToDictionary(c => c.Id);

        LlmConfigResolver.Resolve(LlmStage.Extract, policy, configs, Platform).Config.ShouldBe(extractOverride);
        var classify = LlmConfigResolver.Resolve(LlmStage.Classify, policy, configs, Platform);
        classify.Config.ShouldBe(tenantDefault);
        classify.KeyOwner.ShouldBe(KeyOwner.Tenant);
    }

    [Fact]
    public void Unusable_tenant_config_without_fallback_permission_is_unavailable()
    {
        var invalid = Config(ConfigScope.Tenant, LlmProviderKind.OpenAi, ProviderConfigStatus.Invalid) with { LastError = "401 invalid api key" };
        var policy = new TenantLlmPolicy { TenantId = TenantId, DefaultConfigId = invalid.Id };

        var ex = Should.Throw<LlmException>(() =>
            LlmConfigResolver.Resolve(LlmStage.Extract, policy, new[] { invalid }.ToDictionary(c => c.Id), Platform));

        ex.Kind.ShouldBe(LlmErrorKind.Unavailable);
        ex.BlocksTenant.ShouldBeTrue();
        ex.Message.ShouldContain("401 invalid api key");
    }

    [Fact]
    public void Unusable_tenant_config_falls_back_to_platform_when_the_policy_allows()
    {
        var disabled = Config(ConfigScope.Tenant, LlmProviderKind.OpenAi, ProviderConfigStatus.Disabled);
        var policy = new TenantLlmPolicy { TenantId = TenantId, DefaultConfigId = disabled.Id, FallbackToPlatformKey = true };

        var resolved = LlmConfigResolver.Resolve(LlmStage.Extract, policy, new[] { disabled }.ToDictionary(c => c.Id), Platform);

        resolved.Config.ShouldBe(Platform);
        resolved.KeyOwner.ShouldBe(KeyOwner.Platform);
        resolved.UsedFallback.ShouldBeTrue();
    }

    [Fact]
    public void Tenant_config_missing_the_stage_counts_as_unusable_for_that_stage()
    {
        var classifyOnly = Config(ConfigScope.Tenant, LlmProviderKind.OpenAi, stages: LlmStage.Classify);
        var policy = new TenantLlmPolicy { TenantId = TenantId, DefaultConfigId = classifyOnly.Id, FallbackToPlatformKey = true };
        var configs = new[] { classifyOnly }.ToDictionary(c => c.Id);

        LlmConfigResolver.Resolve(LlmStage.Classify, policy, configs, Platform).Config.ShouldBe(classifyOnly);
        LlmConfigResolver.Resolve(LlmStage.Extract, policy, configs, Platform).Config.ShouldBe(Platform);
    }

    [Fact]
    public void Our_key_or_nothing_policy_never_touches_the_platform_key()
    {
        var policy = new TenantLlmPolicy { TenantId = TenantId, AllowPlatformKeyAtAll = false };

        Should.Throw<LlmException>(() => LlmConfigResolver.Resolve(LlmStage.Extract, policy, new Dictionary<Guid, LlmProviderConfig>(), Platform))
            .Kind.ShouldBe(LlmErrorKind.Unavailable);
    }

    [Fact]
    public void No_platform_default_is_unavailable()
    {
        Should.Throw<LlmException>(() => LlmConfigResolver.Resolve(LlmStage.Extract, null, new Dictionary<Guid, LlmProviderConfig>(), null))
            .Kind.ShouldBe(LlmErrorKind.Unavailable);
    }
}
