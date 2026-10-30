namespace DocProc.Llm;

/// <summary>Pipeline stages that call an LLM. Model and provider are configured per stage (docs/08-llm-providers-and-byok.md).</summary>
public enum LlmStage
{
    Classify,
    Split,
    Extract,
    Synthesize,
}

/// <summary>Normalised reasoning setting; each adapter maps it to its vendor's parameter and degrades to the nearest supported value.</summary>
public enum ReasoningEffort
{
    None,
    Low,
    Medium,
    High,
}

public enum LlmProviderKind
{
    Anthropic,
    AnthropicFoundry,
    OpenAi,
    AzureOpenAi,
    OpenAiCompatible,

    /// <summary>Serves recorded responses; used by all CI tests (docs/09-local-development.md).</summary>
    Replay,
}

/// <summary>What a provider can do; stage code adapts to this, never to the vendor.</summary>
public sealed record LlmCapabilities(
    bool NativePdfInput,
    bool JsonSchemaOutput,
    bool PromptCaching,
    bool Batch,
    bool Vision,
    int MaxPagesPerRequest,
    int MaxInputTokens);

public sealed record LlmUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    int ReasoningTokens = 0)
{
    public static readonly LlmUsage Zero = new(0, 0);

    public int TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens + ReasoningTokens;

    public static LlmUsage operator +(LlmUsage a, LlmUsage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheReadTokens + b.CacheReadTokens,
        a.CacheWriteTokens + b.CacheWriteTokens,
        a.ReasoningTokens + b.ReasoningTokens);

    public LlmUsage Add(LlmUsage other) => this + other;
}

/// <summary>Why a call failed, independent of the vendor. Drives retries and the LLM_PROVIDER_UNAVAILABLE flag.</summary>
public enum LlmErrorKind
{
    /// <summary>Key invalid, revoked or lacking permission.</summary>
    Auth,

    /// <summary>Provider rate limit; retry after the given delay.</summary>
    RateLimited,

    /// <summary>Provider quota or our spend cap exhausted; waits for a human or the next period.</summary>
    Quota,

    /// <summary>The model refused the content; not retryable on the same provider.</summary>
    ContentRefused,

    /// <summary>Our request was malformed (schema, size, unsupported feature); a bug, not a retry.</summary>
    InvalidRequest,

    /// <summary>Network or 5xx; retry with backoff.</summary>
    Transient,

    /// <summary>No usable provider configuration for this tenant and stage.</summary>
    Unavailable,
}

public sealed class LlmException(LlmErrorKind kind, string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public LlmErrorKind Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>Whether the worker should retry the same call later.</summary>
    public bool IsRetryable => Kind is LlmErrorKind.RateLimited or LlmErrorKind.Transient;

    /// <summary>Whether the tenant's documents should pause with LLM_PROVIDER_UNAVAILABLE rather than retry.</summary>
    public bool BlocksTenant => Kind is LlmErrorKind.Auth or LlmErrorKind.Quota or LlmErrorKind.Unavailable;
}
