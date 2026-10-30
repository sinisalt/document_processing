namespace DocProc.Llm.OpenAi;

/// <summary>
/// Capability descriptor for OpenAI, Azure OpenAI and OpenAI-compatible endpoints. The adapter itself ships in Phase 1
/// alongside the first pilot (docs/07) so both provider families are exercised early; the pipeline already adapts to
/// these capabilities rather than to the vendor.
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
}
