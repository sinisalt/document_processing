namespace DocProc.Llm.OpenAi;

/// <summary>
/// Azure OpenAI specifics that the plain OpenAI SDK needs spelled out (docs/08 "What stays provider-specific"): the
/// resource endpoint maps to its OpenAI-compatible <c>/openai/v1</c> path, and Entra tokens are requested for the
/// Cognitive Services scope.
/// </summary>
public static class AzureOpenAiEndpoint
{
    public const string V1Path = "/openai/v1";

    /// <summary>The OAuth scope Azure OpenAI accepts for managed identity and other Entra credentials.</summary>
    public const string TokenScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Turns the resource endpoint a tenant pastes from the portal (<c>https://{resource}.openai.azure.com</c>, with or
    /// without <c>/openai</c> or <c>/openai/v1</c> and a trailing slash) into the v1 base URL.
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
}
