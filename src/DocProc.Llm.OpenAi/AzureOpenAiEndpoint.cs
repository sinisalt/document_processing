using System.Web;

namespace DocProc.Llm.OpenAi;

/// <summary>
/// Azure OpenAI specifics that the plain OpenAI SDK needs spelled out (docs/08 "What stays provider-specific"): the
/// resource endpoint maps to its OpenAI-compatible <c>/openai/v1</c> path, and Entra tokens are requested for the
/// Cognitive Services scope.
/// </summary>
public static class AzureOpenAiEndpoint
{
    public const string V1Path = "/openai/v1";

    /// <summary>The query parameter Azure uses to select an API version; see <see cref="ApiVersionFrom"/>.</summary>
    public const string ApiVersionParameter = "api-version";

    /// <summary>
    /// The OAuth scope requested for managed identity and other Entra credentials. Azure OpenAI resources accept the
    /// Cognitive Services audience for both the legacy and the <c>/openai/v1</c> surface, and it is the one Microsoft's
    /// Azure OpenAI quickstarts and the <c>Azure.AI.OpenAI</c> package request, so it is kept here rather than the newer
    /// <c>https://ai.azure.com/.default</c> audience that the Foundry portal documents; making the scope configurable is
    /// deferred to the BYOK work in Phase 2 together with explicit client-credential configuration.
    /// </summary>
    public const string TokenScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Turns the resource endpoint a tenant pastes from the portal (<c>https://{resource}.openai.azure.com</c>, with or
    /// without <c>/openai</c> or <c>/openai/v1</c> and a trailing slash) into the v1 base URL. Any query string is
    /// dropped here; an <c>api-version</c> in it is lifted separately by <see cref="ApiVersionFrom"/>.
    /// </summary>
    public static Uri Resolve(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The Azure OpenAI endpoint must be an absolute URL.", nameof(endpoint));
        }

        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(V1Path, StringComparison.OrdinalIgnoreCase))
        {
            // already the v1 base
        }
        else if (path.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            path += "/v1";
        }
        else
        {
            path += V1Path;
        }

        return new UriBuilder(endpoint) { Path = path, Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    /// <summary>
    /// The <c>api-version</c> a tenant left on the pasted endpoint (<c>...?api-version=preview</c>), or null when
    /// there is none. The configuration's explicit <see cref="Providers.LlmProviderConfig.ApiVersion"/> takes precedence.
    /// </summary>
    public static string? ApiVersionFrom(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || string.IsNullOrEmpty(endpoint.Query))
        {
            return null;
        }

        var value = HttpUtility.ParseQueryString(endpoint.Query)[ApiVersionParameter];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
