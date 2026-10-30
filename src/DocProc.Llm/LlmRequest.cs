using DocProc.Domain.Documents;
using DocProc.Llm.Schema;

namespace DocProc.Llm;

public sealed record LlmOptions
{
    public static readonly LlmOptions Default = new();

    public ReasoningEffort Effort { get; init; } = ReasoningEffort.Low;

    public int MaxOutputTokens { get; init; } = 8_192;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Set when the caller is a backfill or eval that may use the provider's batch endpoint.</summary>
    public bool AllowBatch { get; init; }
}

/// <summary>One call to a provider. <see cref="Model"/> is a model id or, for Azure OpenAI, a deployment name.</summary>
public sealed record LlmRequest
{
    public required LlmStage Stage { get; init; }

    public required string Model { get; init; }

    public required PromptBundle Prompt { get; init; }

    public DocumentInput? Document { get; init; }

    public JsonSchemaDefinition? OutputSchema { get; init; }

    public LlmOptions Options { get; init; } = LlmOptions.Default;

    /// <summary>Correlation for usage rows and logs; never sent to the provider.</summary>
    public Guid? DocumentId { get; init; }
}

public sealed record LlmResult
{
    public required string Json { get; init; }

    public required LlmUsage Usage { get; init; }

    public required string Model { get; init; }

    /// <summary>Normalised: "end_turn", "max_tokens", "refusal" or the vendor's value when none of these fit.</summary>
    public required string StopReason { get; init; }

    public string? ProviderRequestId { get; init; }

    public TimeSpan Latency { get; init; }

    /// <summary>The provider kind the gateway resolved for the call; null when the result did not pass through the gateway.</summary>
    public LlmProviderKind? Provider { get; init; }

    /// <summary>Whose key paid for the call (docs/08 "Cost attribution"); null when the result did not pass through the gateway.</summary>
    public KeyOwner? KeyOwner { get; init; }

    public bool IsTruncated => StopReason == StopReasons.MaxTokens;

    public bool IsRefusal => StopReason == StopReasons.Refusal;
}

public static class StopReasons
{
    public const string EndTurn = "end_turn";
    public const string MaxTokens = "max_tokens";
    public const string Refusal = "refusal";
}

public enum BatchStatus
{
    InProgress,
    Completed,
    Failed,
    Expired,
    Cancelled,
}

public sealed record BatchHandle(LlmProviderKind Provider, string ProviderBatchId, DateTimeOffset SubmittedAt, int RequestCount);

/// <summary>Per-request outcome of a batch: exactly one of Result or Error is set, in submission order.</summary>
public sealed record BatchItem(int Index, LlmResult? Result, LlmException? Error);

public sealed record BatchResult(BatchStatus Status, IReadOnlyList<BatchItem> Items);
