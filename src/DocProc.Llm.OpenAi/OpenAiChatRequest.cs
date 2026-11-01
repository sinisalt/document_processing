using OpenAI.Chat;

namespace DocProc.Llm.OpenAi;

/// <summary>
/// The provider-specific shape of one chat-completion call: the model (or Azure deployment name), the messages, the
/// SDK options and the per-call timeout. <see cref="OpenAiRequestBuilder"/> produces it; <see cref="OpenAiCompatibleProvider"/> sends it.
/// </summary>
public sealed record OpenAiChatRequest(string Model, IReadOnlyList<ChatMessage> Messages, ChatCompletionOptions Options)
{
    /// <summary>How long one network attempt may take (<see cref="LlmOptions.Timeout"/>); the provider maps expiry to a transient error.</summary>
    public TimeSpan Timeout { get; init; } = LlmOptions.Default.Timeout;

    /// <summary>Whether a reasoning effort is set; the provider clears or degrades it when a model rejects the parameter or the value (docs/08 "Reasoning/effort").</summary>
    public bool HasReasoningEffort => Options.ReasoningEffortLevel is not null;

    /// <summary>The reasoning effort currently on the request; the provider lowers it along the ladder when a model rejects the value.</summary>
    public ChatReasoningEffortLevel? ReasoningEffort
    {
        get => Options.ReasoningEffortLevel;
        set => Options.ReasoningEffortLevel = value;
    }

    public void ClearReasoningEffort() => Options.ReasoningEffortLevel = null;
}
