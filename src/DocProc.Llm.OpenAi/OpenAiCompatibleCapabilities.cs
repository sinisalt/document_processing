namespace DocProc.Llm.OpenAi;

/// <summary>
/// Capability descriptor for OpenAI, Azure OpenAI and OpenAI-compatible endpoints (docs/08 "Provider abstraction").
/// The pipeline adapts to these capabilities rather than to the vendor; <see cref="OpenAiRequestBuilder"/> picks the
/// document and output strategy from them.
/// </summary>
public static class OpenAiCompatibleCapabilities
{
    public static readonly LlmCapabilities OpenAi = new(
        NativePdfInput: true,   // chat completions accept PDF file parts as base64
        JsonSchemaOutput: true, // strict mode: all properties required, additionalProperties false
        PromptCaching: true,    // automatic prefix caching
        Batch: false,
        Vision: true,
        MaxPagesPerRequest: 100,
        MaxInputTokens: 128_000);

    /// <summary>Self-hosted OpenAI-compatible servers (vLLM, Ollama) typically lack PDF input and strict schemas.</summary>
    public static readonly LlmCapabilities SelfHosted = OpenAi with
    {
        NativePdfInput = false,
        JsonSchemaOutput = false,
        PromptCaching = false,
        MaxInputTokens = 32_000,
    };

    /// <summary>The descriptor the adapter uses for a provider kind; <see cref="LlmProviderKind.OpenAiCompatible"/> is the self-hosted profile.</summary>
    public static LlmCapabilities For(LlmProviderKind kind) => kind switch
    {
        LlmProviderKind.OpenAi or LlmProviderKind.AzureOpenAi => OpenAi,
        LlmProviderKind.OpenAiCompatible => SelfHosted,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This adapter serves OpenAI, Azure OpenAI and OpenAI-compatible endpoints only."),
    };
}
