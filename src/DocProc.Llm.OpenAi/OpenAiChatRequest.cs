using OpenAI.Chat;

namespace DocProc.Llm.OpenAi;

/// <summary>
/// The provider-specific shape of one chat-completion call: the model (or Azure deployment name), the messages and the
/// SDK options. <see cref="OpenAiRequestBuilder"/> produces it; <see cref="OpenAiCompatibleProvider"/> sends it.
/// </summary>
public sealed record OpenAiChatRequest(string Model, IReadOnlyList<ChatMessage> Messages, ChatCompletionOptions Options)
{
    /// <summary>Whether a reasoning effort is set; the provider clears it once when a model rejects the parameter (docs/08 "Reasoning/effort").</summary>
    public bool HasReasoningEffort => Options.ReasoningEffortLevel is not null;

    public void ClearReasoningEffort() => Options.ReasoningEffortLevel = null;
}
