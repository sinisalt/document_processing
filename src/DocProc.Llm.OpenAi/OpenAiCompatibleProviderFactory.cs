using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using DocProc.Llm.OpenAi.Pipeline;
using DocProc.Llm.Providers;
using OpenAI;

namespace DocProc.Llm.OpenAi;

/// <summary>
/// Builds an <see cref="OpenAiCompatibleProvider"/> per configuration (docs/08 "Provider configuration model"):
/// <list type="bullet">
/// <item><see cref="LlmProviderKind.OpenAi"/>: api.openai.com (or the configured base URL) with a bearer API key.</item>
/// <item><see cref="LlmProviderKind.AzureOpenAi"/>: the resource's <c>/openai/v1</c> endpoint with the <c>api-key</c>
/// header, or <see cref="DefaultAzureCredential"/> when the credential asks for managed identity; deployment names are
/// the model ids and an optional <c>api-version</c> (from the configuration, else from the pasted endpoint's query) is appended.</item>
/// <item><see cref="LlmProviderKind.OpenAiCompatible"/>: an arbitrary base URL (vLLM, Ollama) with an optional key and
/// the self-hosted capability profile.</item>
/// </list>
/// The client holds the key; nothing else does. Retries belong to the worker (<see cref="LlmException.IsRetryable"/>).
/// A tenant-scope configuration talks through <see cref="TenantEndpointGuard.HttpClient"/> (public https hosts only,
/// checked at connect time); a platform configuration through the SDK's default transport.
/// </summary>
/// <param name="transport">Replaces the SDK's HTTP transport for every configuration (tests); null keeps the defaults above.</param>
/// <param name="clock">Drives every provider's per-request timeout; the host's <see cref="TimeProvider"/>, <see cref="TimeProvider.System"/> when null.</param>
public sealed class OpenAiCompatibleProviderFactory(PipelineTransport? transport = null, TimeProvider? clock = null) : ILlmProviderFactory
{
    public const string AzureApiKeyHeader = "api-key";

    /// <summary>The hard ceiling on one network call; <see cref="LlmOptions.Timeout"/> bounds each request below it.</summary>
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);

    private HttpClientPipelineTransport? _tenantTransport;

    /// <summary>The clock the providers built here time their requests with.</summary>
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    /// <summary>
    /// The client tenant-scope configurations talk through: <see cref="TenantEndpointGuard.HttpClient"/> unless a test
    /// sets one from <see cref="TenantEndpointGuard.CreateClient"/>. An init property, not a constructor parameter,
    /// so dependency injection can never hand it an unguarded <see cref="HttpClient"/>.
    /// </summary>
    public HttpClient TenantHttpClient { get; init; } = TenantEndpointGuard.HttpClient;

    public bool Supports(LlmProviderKind kind) => OpenAiCompatibleCapabilities.Supports(kind);

    public ILlmProvider Create(LlmProviderConfig config, LlmCredential credential)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(credential);
        if (!Supports(config.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(config), config.Kind, "This factory serves OpenAI, Azure OpenAI and OpenAI-compatible endpoints only.");
        }

        TenantEndpointGuard.EnsureAllowed(config);
        var validationModel = config.ModelFor(LlmStage.Classify) ?? config.Models.Values.FirstOrDefault()
            ?? throw new LlmException(LlmErrorKind.InvalidRequest, $"Provider configuration {config.Id} has no models configured.");

        var client = config.Kind switch
        {
            LlmProviderKind.OpenAi => CreateOpenAiClient(config, credential),
            LlmProviderKind.AzureOpenAi => CreateAzureClient(config, credential),
            _ => CreateCompatibleClient(config, credential),
        };

        return new OpenAiCompatibleProvider(client, config.Kind, OpenAiCompatibleCapabilities.For(config.Kind), validationModel, Clock);
    }

    private OpenAIClient CreateOpenAiClient(LlmProviderConfig config, LlmCredential credential)
    {
        var options = NewOptions(config);
        if (config.Endpoint is { } endpoint)
        {
            options.Endpoint = endpoint;
        }

        return new OpenAIClient(RequireApiKey(config, credential), options);
    }

    private OpenAIClient CreateAzureClient(LlmProviderConfig config, LlmCredential credential)
    {
        var endpoint = config.Endpoint
            ?? throw new LlmException(LlmErrorKind.InvalidRequest, $"Azure OpenAI configuration {config.Id} has no endpoint.");

        var options = NewOptions(config);
        options.Endpoint = AzureOpenAiEndpoint.Resolve(endpoint);

        // Resolve() strips the query; an api-version a tenant left on the pasted endpoint still counts when the configuration sets none.
        var apiVersion = string.IsNullOrWhiteSpace(config.ApiVersion) ? AzureOpenAiEndpoint.ApiVersionFrom(endpoint) : config.ApiVersion;
        if (!string.IsNullOrWhiteSpace(apiVersion))
        {
            options.AddPolicy(new ApiVersionPolicy(apiVersion), PipelinePosition.PerCall);
        }

        AuthenticationPolicy auth = credential.UseManagedIdentity
            ? new BearerTokenPolicy(new DefaultAzureCredential(includeInteractiveCredentials: false), AzureOpenAiEndpoint.TokenScope)
            : ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy(RequireApiKey(config, credential), AzureApiKeyHeader, keyPrefix: null);

        return new OpenAIClient(auth, options);
    }

    private OpenAIClient CreateCompatibleClient(LlmProviderConfig config, LlmCredential credential)
    {
        var endpoint = config.Endpoint
            ?? throw new LlmException(LlmErrorKind.InvalidRequest, $"OpenAI-compatible configuration {config.Id} has no endpoint.");

        var options = NewOptions(config);
        options.Endpoint = endpoint;
        options.AddPolicy(LegacyMaxTokensPolicy.Instance, PipelinePosition.PerCall); // Ollama only honours max_tokens

        return string.IsNullOrWhiteSpace(credential.ApiKey)
            ? new OpenAIClient(AnonymousAuthenticationPolicy.Instance, options)
            : new OpenAIClient(new ApiKeyCredential(credential.ApiKey), options);
    }

    private OpenAIClientOptions NewOptions(LlmProviderConfig config)
    {
        var options = new OpenAIClientOptions
        {
            NetworkTimeout = NetworkTimeout,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };

        if (transport is not null)
        {
            options.Transport = transport;
        }
        else if (config.Scope == ConfigScope.Tenant)
        {
            options.Transport = _tenantTransport ??= new HttpClientPipelineTransport(TenantHttpClient);
        }

        return options;
    }

    private static ApiKeyCredential RequireApiKey(LlmProviderConfig config, LlmCredential credential)
    {
        if (credential.UseManagedIdentity)
        {
            throw new LlmException(LlmErrorKind.Auth, $"Provider configuration {config.Id} ({config.Kind}) needs an API key; managed identity is only available for Azure OpenAI.");
        }

        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            throw new LlmException(LlmErrorKind.Auth, $"Provider configuration {config.Id} has no API key.");
        }

        return new ApiKeyCredential(credential.ApiKey);
    }
}
