using System.ClientModel.Primitives;

namespace DocProc.Llm.OpenAi.Pipeline;

/// <summary>
/// Appends Azure OpenAI's <c>api-version</c> query parameter when the configuration sets one (docs/08 "Provider
/// configuration model"). The v1 endpoint needs none; <c>preview</c> opts into preview features.
/// </summary>
public sealed class ApiVersionPolicy : PipelinePolicy
{
    public const string ParameterName = "api-version";

    private readonly string _apiVersion;

    public ApiVersionPolicy(string apiVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);
        _apiVersion = apiVersion;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private void Apply(PipelineMessage message)
    {
        if (message.Request.Uri is not { } uri || uri.Query.Contains($"{ParameterName}=", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var builder = new UriBuilder(uri);
        var parameter = $"{ParameterName}={Uri.EscapeDataString(_apiVersion)}";
        builder.Query = string.IsNullOrEmpty(builder.Query) ? parameter : $"{builder.Query.TrimStart('?')}&{parameter}";
        message.Request.Uri = builder.Uri;
    }
}
