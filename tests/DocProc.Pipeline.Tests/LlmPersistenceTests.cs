using DocProc.Infrastructure.Llm;
using DocProc.Infrastructure.Security;
using DocProc.Llm;
using DocProc.Llm.Providers;
using Microsoft.Extensions.Configuration;

namespace DocProc.Pipeline.Tests;

public class LlmPersistenceTests
{
    private static LlmProviderConfig Config(LlmProviderKind kind = LlmProviderKind.Anthropic, string? secretRef = "llm-platform-eu", LlmAuthType auth = LlmAuthType.ApiKey) => new()
    {
        Id = Guid.Parse("00000000-0003-4000-8000-000000000001"),
        Scope = ConfigScope.Platform,
        Kind = kind,
        Auth = new LlmAuth(auth, secretRef),
    };

    private static SecretStoreLlmCredentialStore Store(params (string Key, string? Value)[] settings) => Store(null, settings);

    private static SecretStoreLlmCredentialStore Store(InMemorySecretStore? secrets, params (string Key, string? Value)[] settings) =>
        new(secrets, new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value)).Build());

    [Fact]
    public async Task Resolves_a_key_the_api_wrote_to_the_secret_store_before_configuration()
    {
        var secrets = new InMemorySecretStore();
        var tenantRef = $"tenant/{Guid.NewGuid()}/{Guid.NewGuid()}";
        await secrets.SetAsync(tenantRef, "sk-byok-tenant", CancellationToken.None);
        await secrets.SetAsync("llm-platform-eu", "sk-from-store", CancellationToken.None);
        var store = Store(secrets, ("Llm:Secrets:llm-platform-eu", "sk-from-configuration"));

        (await store.GetAsync(Config(secretRef: tenantRef), CancellationToken.None)).ApiKey.ShouldBe("sk-byok-tenant");
        (await store.GetAsync(Config(), CancellationToken.None)).ApiKey.ShouldBe("sk-from-store", "the secret store wins over configuration");
    }

    [Fact]
    public async Task Falls_back_to_configuration_when_the_secret_store_has_nothing_under_the_reference()
    {
        var credential = await Store(new InMemorySecretStore(), ("Llm:Secrets:llm-platform-eu", "sk-seeded")).GetAsync(Config(), CancellationToken.None);

        credential.ApiKey.ShouldBe("sk-seeded");
    }

    [Fact]
    public async Task Resolves_the_key_from_configuration_under_the_secret_reference()
    {
        var credential = await Store(("Llm:Secrets:llm-platform-eu", "  sk-test ")).GetAsync(Config(), CancellationToken.None);

        credential.ApiKey.ShouldBe("sk-test");
        credential.UseManagedIdentity.ShouldBeFalse();
    }

    [Fact]
    public async Task Falls_back_to_the_environment_variable_in_upper_snake_case()
    {
        SecretStoreLlmCredentialStore.EnvironmentVariableFor("llm-platform-eu").ShouldBe("LLM_SECRET_LLM_PLATFORM_EU");
        SecretStoreLlmCredentialStore.EnvironmentVariableFor("tenant.a/openai").ShouldBe("LLM_SECRET_TENANT_A_OPENAI");

        var variable = "LLM_SECRET_PIPELINE_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var secretRef = variable["LLM_SECRET_".Length..].ToLowerInvariant().Replace('_', '-');
        Environment.SetEnvironmentVariable(variable, "from-env");
        try
        {
            var credential = await Store().GetAsync(Config(secretRef: secretRef), CancellationToken.None);
            credential.ApiKey.ShouldBe("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Missing_key_is_an_auth_error_that_keeps_internal_names_out_of_the_tenant_visible_message()
    {
        var secretRef = $"tenant/{Guid.NewGuid()}/nowhere-to-be-found";
        var ex = await Should.ThrowAsync<LlmException>(() => Store(new InMemorySecretStore()).GetAsync(Config(secretRef: secretRef), CancellationToken.None));

        ex.Kind.ShouldBe(LlmErrorKind.Auth);
        ex.BlocksTenant.ShouldBeTrue();
        ex.Message.ShouldContain(Config().Id.ToString());
        ex.Message.ShouldNotContain(secretRef);
        ex.Message.ShouldNotContain("Llm:Secrets");
        ex.Message.ShouldNotContain("LLM_SECRET_");
    }

    [Fact]
    public async Task Managed_identity_and_replay_need_no_secret()
    {
        (await Store().GetAsync(Config(auth: LlmAuthType.ManagedIdentity, secretRef: null), CancellationToken.None)).UseManagedIdentity.ShouldBeTrue();
        (await Store().GetAsync(Config(kind: LlmProviderKind.Replay, secretRef: null), CancellationToken.None)).ShouldBe(LlmCredential.None);
    }
}
