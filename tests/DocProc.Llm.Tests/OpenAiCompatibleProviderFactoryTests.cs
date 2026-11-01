using System.Collections.Frozen;
using System.Net;
using DocProc.Llm.OpenAi;
using DocProc.Llm.OpenAi.Pipeline;
using DocProc.Llm.Providers;
using static DocProc.Llm.Tests.OpenAiTestSupport;

namespace DocProc.Llm.Tests;

public class OpenAiCompatibleProviderFactoryTests
{
    private static readonly LlmCredential ApiKey = new("sk-test");
    private static readonly Uri AzureResource = new("https://docproc-eu.openai.azure.com/");
    private static readonly Uri SelfHosted = new("http://vllm.internal:8000/v1");

    [Theory]
    [InlineData(LlmProviderKind.OpenAi, true)]
    [InlineData(LlmProviderKind.AzureOpenAi, true)]
    [InlineData(LlmProviderKind.OpenAiCompatible, true)]
    [InlineData(LlmProviderKind.Anthropic, false)]
    [InlineData(LlmProviderKind.AnthropicFoundry, false)]
    [InlineData(LlmProviderKind.Replay, false)]
    public void Supports_the_openai_family_only(LlmProviderKind kind, bool expected)
    {
        new OpenAiCompatibleProviderFactory().Supports(kind).ShouldBe(expected);
        if (!expected)
        {
            Should.Throw<ArgumentOutOfRangeException>(() => new OpenAiCompatibleProviderFactory().Create(Config(kind), ApiKey));
        }
    }

    [Theory]
    [InlineData("https://docproc-eu.openai.azure.com", "https://docproc-eu.openai.azure.com/openai/v1")]
    [InlineData("https://docproc-eu.openai.azure.com/", "https://docproc-eu.openai.azure.com/openai/v1")]
    [InlineData("https://docproc-eu.openai.azure.com/openai", "https://docproc-eu.openai.azure.com/openai/v1")]
    [InlineData("https://docproc-eu.openai.azure.com/openai/v1/", "https://docproc-eu.openai.azure.com/openai/v1")]
    [InlineData("https://docproc-eu.openai.azure.com/openai/v1?api-version=preview", "https://docproc-eu.openai.azure.com/openai/v1")]
    [InlineData("https://docproc.cognitiveservices.azure.com/", "https://docproc.cognitiveservices.azure.com/openai/v1")]
    public void Azure_resource_endpoints_resolve_to_the_v1_base(string configured, string expected) =>
        AzureOpenAiEndpoint.Resolve(new Uri(configured)).ShouldBe(new Uri(expected));

    [Fact]
    public async Task Azure_uses_the_api_key_header_the_deployment_name_and_the_api_version()
    {
        var handler = new FakeOpenAiHandler().EnqueueCompletion();
        var config = Config(LlmProviderKind.AzureOpenAi, AzureResource, apiVersion: "preview", models: new Dictionary<LlmStage, string>
        {
            [LlmStage.Classify] = "docproc-gpt-5-mini-eu",
            [LlmStage.Extract] = "docproc-gpt-5-eu",
        }.ToFrozenDictionary());
        var provider = Provider(handler, config, ApiKey);

        provider.Kind.ShouldBe(LlmProviderKind.AzureOpenAi);
        provider.Capabilities.ShouldBe(OpenAiCompatibleCapabilities.OpenAi);
        await provider.CompleteAsync(Request(null, model: "docproc-gpt-5-eu"), CancellationToken.None);

        var sent = handler.Requests.ShouldHaveSingleItem();
        sent.Uri.ShouldBe(new Uri("https://docproc-eu.openai.azure.com/openai/v1/chat/completions?api-version=preview"));
        sent.Headers["api-key"].ShouldBe("sk-test");
        sent.Headers.ContainsKey("Authorization").ShouldBeFalse();
        sent.Body.GetProperty("model").GetString().ShouldBe("docproc-gpt-5-eu");
    }

    [Fact]
    public async Task Azure_without_an_api_version_sends_none()
    {
        var handler = new FakeOpenAiHandler().EnqueueCompletion();

        await Provider(handler, Config(LlmProviderKind.AzureOpenAi, AzureResource), ApiKey).CompleteAsync(Request(null), CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri.ShouldBe(new Uri("https://docproc-eu.openai.azure.com/openai/v1/chat/completions"));
    }

    [Fact]
    public async Task Azure_keeps_an_api_version_pasted_into_the_endpoint_when_the_configuration_sets_none()
    {
        var pasted = new Uri("https://docproc-eu.openai.azure.com/openai/v1?api-version=preview");
        var fromEndpoint = new FakeOpenAiHandler().EnqueueCompletion();
        var fromConfig = new FakeOpenAiHandler().EnqueueCompletion();

        await Provider(fromEndpoint, Config(LlmProviderKind.AzureOpenAi, pasted), ApiKey).CompleteAsync(Request(null), CancellationToken.None);
        await Provider(fromConfig, Config(LlmProviderKind.AzureOpenAi, pasted, apiVersion: "2025-04-01-preview"), ApiKey).CompleteAsync(Request(null), CancellationToken.None);

        fromEndpoint.Requests.ShouldHaveSingleItem().Uri.ShouldBe(new Uri("https://docproc-eu.openai.azure.com/openai/v1/chat/completions?api-version=preview"));
        fromConfig.Requests.ShouldHaveSingleItem().Uri.ShouldBe(new Uri("https://docproc-eu.openai.azure.com/openai/v1/chat/completions?api-version=2025-04-01-preview"), "the configuration's api-version wins");
        AzureOpenAiEndpoint.ApiVersionFrom(pasted).ShouldBe("preview");
        AzureOpenAiEndpoint.ApiVersionFrom(AzureResource).ShouldBeNull();
    }

    [Fact]
    public void Azure_with_managed_identity_builds_a_provider_without_a_key()
    {
        var config = Config(LlmProviderKind.AzureOpenAi, AzureResource, auth: LlmAuthType.ManagedIdentity);

        var provider = new OpenAiCompatibleProviderFactory().Create(config, new LlmCredential(UseManagedIdentity: true));

        provider.ShouldBeOfType<OpenAiCompatibleProvider>();
        provider.Kind.ShouldBe(LlmProviderKind.AzureOpenAi);
    }

    [Fact]
    public void Managed_identity_is_only_available_for_azure()
    {
        var error = Should.Throw<LlmException>(() => new OpenAiCompatibleProviderFactory().Create(Config(LlmProviderKind.OpenAi), new LlmCredential(UseManagedIdentity: true)));

        error.Kind.ShouldBe(LlmErrorKind.Auth);
    }

    [Theory]
    [InlineData(LlmProviderKind.OpenAi)]
    [InlineData(LlmProviderKind.AzureOpenAi)]
    public void A_missing_api_key_is_an_auth_error(LlmProviderKind kind)
    {
        var error = Should.Throw<LlmException>(() => new OpenAiCompatibleProviderFactory().Create(Config(kind, AzureResource), LlmCredential.None));

        error.Kind.ShouldBe(LlmErrorKind.Auth);
    }

    [Theory]
    [InlineData(LlmProviderKind.AzureOpenAi)]
    [InlineData(LlmProviderKind.OpenAiCompatible)]
    public void Azure_and_compatible_endpoints_need_a_base_url(LlmProviderKind kind)
    {
        var error = Should.Throw<LlmException>(() => new OpenAiCompatibleProviderFactory().Create(Config(kind, endpoint: null), ApiKey));

        error.Kind.ShouldBe(LlmErrorKind.InvalidRequest);
    }

    [Fact]
    public void A_configuration_without_models_cannot_be_validated()
    {
        var error = Should.Throw<LlmException>(() => new OpenAiCompatibleProviderFactory().Create(Config(LlmProviderKind.OpenAi, models: FrozenDictionary<LlmStage, string>.Empty), ApiKey));

        error.Kind.ShouldBe(LlmErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task Compatible_servers_get_the_self_hosted_profile_and_may_run_without_a_key()
    {
        var handler = new FakeOpenAiHandler().EnqueueCompletion();
        var provider = Provider(handler, Config(LlmProviderKind.OpenAiCompatible, SelfHosted), LlmCredential.None);

        provider.Capabilities.ShouldBe(OpenAiCompatibleCapabilities.SelfHosted);
        await provider.CompleteAsync(Request(new DocumentInput { Pdf = new byte[] { 1 }, PageTexts = ["Rechnung"] }), CancellationToken.None);

        var sent = handler.Requests.ShouldHaveSingleItem();
        sent.Uri.ShouldBe(new Uri("http://vllm.internal:8000/v1/chat/completions"));
        sent.Headers.ContainsKey("Authorization").ShouldBeFalse();
        sent.Body.GetProperty("response_format").GetProperty("type").GetString().ShouldBe("json_object");
        sent.Body.GetProperty("messages")[1].GetProperty("content").EnumerateArray().Select(c => c.GetProperty("type").GetString()).ShouldBe(["text", "text"]);
    }

    [Theory]
    [InlineData(LlmProviderKind.OpenAi)]
    [InlineData(LlmProviderKind.AzureOpenAi)]
    public async Task OpenAi_and_azure_send_max_completion_tokens_only(LlmProviderKind kind)
    {
        var handler = new FakeOpenAiHandler().EnqueueCompletion().EnqueueCompletion(content: "", finishReason: "length");
        var provider = Provider(handler, Config(kind, AzureResource), ApiKey);

        await provider.CompleteAsync(Request(null), CancellationToken.None);
        await provider.ValidateCredentialAsync(CancellationToken.None);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Body.GetProperty("max_completion_tokens").GetInt32().ShouldBe(4096);
        handler.Requests[0].Body.TryGetProperty("max_tokens", out _).ShouldBeFalse();
        handler.Requests[1].Body.GetProperty("max_completion_tokens").GetInt32().ShouldBe(1);
        handler.Requests[1].Body.TryGetProperty("max_tokens", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Compatible_servers_get_the_legacy_max_tokens_instead_of_max_completion_tokens()
    {
        var handler = new FakeOpenAiHandler().EnqueueCompletion().EnqueueCompletion(content: "", finishReason: "length");
        var provider = Provider(handler, Config(LlmProviderKind.OpenAiCompatible, SelfHosted), LlmCredential.None);
        var request = Request(new DocumentInput { PageTexts = ["Rechnung über 1.234,56 €"] });

        await provider.CompleteAsync(request, CancellationToken.None);
        await provider.ValidateCredentialAsync(CancellationToken.None);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Body.GetProperty("max_tokens").GetInt32().ShouldBe(4096);
        handler.Requests[0].Body.TryGetProperty("max_completion_tokens", out _).ShouldBeFalse("Ollama ignores the modern field and would run unbounded");
        handler.Requests[0].Body.TryGetProperty("user", out _).ShouldBeFalse();
        handler.Requests[0].Body.GetProperty("model").GetString().ShouldBe("gpt-5");
        handler.Requests[0].Body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!.ShouldContain("1.234,56 €", Case.Sensitive, "the rewrite keeps non-ASCII text intact");
        handler.Requests[1].Body.GetProperty("max_tokens").GetInt32().ShouldBe(1);
        handler.Requests[1].Body.TryGetProperty("max_completion_tokens", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_legacy_max_tokens_rewrite_leaves_bodies_without_the_field_alone()
    {
        LegacyMaxTokensPolicy.Rewrite("{\"model\":\"m\",\"max_tokens\":3}"u8.ToArray()).ShouldBeNull();
        LegacyMaxTokensPolicy.Rewrite("[1,2]"u8.ToArray()).ShouldBeNull();

        var rewritten = LegacyMaxTokensPolicy.Rewrite("{\"model\":\"m\",\"max_completion_tokens\":3,\"max_tokens\":9}"u8.ToArray());

        rewritten.ShouldNotBeNull().ToString().ShouldBe("{\"model\":\"m\",\"max_tokens\":3}");
    }

    [Fact]
    public async Task Compatible_servers_with_a_key_send_it_as_a_bearer_token()
    {
        var handler = new FakeOpenAiHandler().EnqueueCompletion();

        await Provider(handler, Config(LlmProviderKind.OpenAiCompatible, SelfHosted), new LlmCredential("local-key")).CompleteAsync(Request(null), CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Headers["Authorization"].ShouldBe("Bearer local-key");
    }

    [Fact]
    public async Task OpenAi_honours_a_custom_base_url_such_as_a_proxy()
    {
        var handler = new FakeOpenAiHandler().EnqueueCompletion();

        await Provider(handler, Config(LlmProviderKind.OpenAi, new Uri("https://llm-proxy.internal/openai/v1")), ApiKey).CompleteAsync(Request(null), CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri.ShouldBe(new Uri("https://llm-proxy.internal/openai/v1/chat/completions"));
    }

    [Fact]
    public async Task Server_errors_are_not_retried_by_the_sdk()
    {
        var handler = new FakeOpenAiHandler().Enqueue(HttpStatusCode.ServiceUnavailable, FakeOpenAiHandler.ErrorJson("overloaded", type: "server_error"));

        var error = await Should.ThrowAsync<LlmException>(() => Provider(handler, Config(LlmProviderKind.OpenAi), ApiKey).CompleteAsync(Request(null), CancellationToken.None));

        error.Kind.ShouldBe(LlmErrorKind.Transient);
        handler.Requests.Count.ShouldBe(1);
    }
}
