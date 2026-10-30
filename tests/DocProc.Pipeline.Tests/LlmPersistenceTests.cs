using DocProc.Infrastructure.Llm;
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

    private static ConfigurationLlmCredentialStore Store(params (string Key, string? Value)[] settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value)).Build());

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
        ConfigurationLlmCredentialStore.EnvironmentVariableFor("llm-platform-eu").ShouldBe("LLM_SECRET_LLM_PLATFORM_EU");
        ConfigurationLlmCredentialStore.EnvironmentVariableFor("tenant.a/openai").ShouldBe("LLM_SECRET_TENANT_A_OPENAI");

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
    public async Task Missing_key_is_an_auth_error_that_names_both_places_to_put_it()
    {
        var ex = await Should.ThrowAsync<LlmException>(() => Store().GetAsync(Config(secretRef: "nowhere-to-be-found"), CancellationToken.None));

        ex.Kind.ShouldBe(LlmErrorKind.Auth);
        ex.BlocksTenant.ShouldBeTrue();
        ex.Message.ShouldContain("Llm:Secrets:nowhere-to-be-found");
        ex.Message.ShouldContain("LLM_SECRET_NOWHERE_TO_BE_FOUND");
    }

    [Fact]
    public async Task Managed_identity_and_replay_need_no_secret()
    {
        (await Store().GetAsync(Config(auth: LlmAuthType.ManagedIdentity, secretRef: null), CancellationToken.None)).UseManagedIdentity.ShouldBeTrue();
        (await Store().GetAsync(Config(kind: LlmProviderKind.Replay, secretRef: null), CancellationToken.None)).ShouldBe(LlmCredential.None);
    }
}
