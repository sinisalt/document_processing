using System.ClientModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using DocProc.Llm.Providers;
using OpenAI;
using OpenAI.Chat;

namespace DocProc.Llm.OpenAi;

/// <summary>
/// Chat-completions adapter for the OpenAI API, Azure OpenAI (deployment names as model ids) and OpenAI-compatible
/// servers (docs/08 "Provider abstraction"). One instance is bound to one configuration and credential; the
/// <see cref="OpenAIClient"/> holds the secret.
/// </summary>
public sealed class OpenAiCompatibleProvider : ILlmProvider
{
    public const string ReasoningEffortParameter = "reasoning_effort";
    public const string ValidationPrompt = "ping";

    private readonly OpenAIClient _client;
    private readonly string _validationModel;

    /// <summary>Models that rejected <c>reasoning_effort</c>; later calls omit it instead of paying a round trip.</summary>
    private readonly ConcurrentDictionary<string, bool> _modelsWithoutReasoningEffort = new(StringComparer.Ordinal);

    public OpenAiCompatibleProvider(OpenAIClient client, LlmProviderKind kind, LlmCapabilities capabilities, string validationModel)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(validationModel);

        _client = client;
        Kind = kind;
        Capabilities = capabilities;
        _validationModel = validationModel;
    }

    public OpenAiCompatibleProvider(OpenAIClient client, LlmProviderKind kind, string validationModel)
        : this(client, kind, OpenAiCompatibleCapabilities.For(kind), validationModel)
    {
    }

    public LlmProviderKind Kind { get; }

    public LlmCapabilities Capabilities { get; }

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var chat = OpenAiRequestBuilder.Build(request, Kind, Capabilities);
        if (_modelsWithoutReasoningEffort.ContainsKey(chat.Model))
        {
            chat.ClearReasoningEffort();
        }

        var started = Stopwatch.GetTimestamp();
        var completion = await SendAsync(chat, allowReasoningEffortFallback: true, cancellationToken).ConfigureAwait(false);
        return ToResult(completion, Stopwatch.GetElapsedTime(started));
    }

    public Task<BatchHandle> SubmitBatchAsync(IReadOnlyList<LlmRequest> requests, CancellationToken cancellationToken) =>
        throw new LlmException(LlmErrorKind.InvalidRequest, "The Batch API is not wired up yet; run requests individually.");

    public Task<BatchResult> GetBatchAsync(BatchHandle handle, CancellationToken cancellationToken) =>
        throw new LlmException(LlmErrorKind.InvalidRequest, "The Batch API is not wired up yet.");

    /// <summary>A one-token completion on the classify model (docs/08 "Key handling" step 3).</summary>
    public async Task ValidateCredentialAsync(CancellationToken cancellationToken)
    {
        var probe = new OpenAiChatRequest(
            _validationModel,
            [new UserChatMessage(ValidationPrompt)],
            new ChatCompletionOptions { MaxOutputTokenCount = 1 });

        await SendAsync(probe, allowReasoningEffortFallback: false, cancellationToken).ConfigureAwait(false);
    }

    public static LlmResult ToResult(ChatCompletion completion, TimeSpan latency)
    {
        if (!string.IsNullOrEmpty(completion.Refusal))
        {
            throw new LlmException(LlmErrorKind.ContentRefused, $"The model refused to process this document: {completion.Refusal}");
        }

        if (completion.FinishReason == ChatFinishReason.ContentFilter)
        {
            throw new LlmException(LlmErrorKind.ContentRefused, "The provider's content filter blocked this document.");
        }

        var text = string.Concat(completion.Content
            .Where(part => part.Kind == ChatMessageContentPartKind.Text)
            .Select(part => part.Text));

        return new LlmResult
        {
            Json = text,
            Usage = ToUsage(completion.Usage),
            Model = completion.Model ?? string.Empty,
            StopReason = completion.FinishReason switch
            {
                ChatFinishReason.Stop => StopReasons.EndTurn,
                ChatFinishReason.Length => StopReasons.MaxTokens,
                ChatFinishReason.ToolCalls => "tool_calls",
                ChatFinishReason.FunctionCall => "function_call",
                var other => other.ToString(),
            },
            ProviderRequestId = completion.Id,
            Latency = latency,
        };
    }

    /// <summary>
    /// OpenAI's <c>prompt_tokens</c> includes cached tokens and <c>completion_tokens</c> includes reasoning tokens; the
    /// price table bills each bucket separately, so they are split out here. Self-hosted servers may omit usage.
    /// </summary>
    public static LlmUsage ToUsage(ChatTokenUsage? usage)
    {
        if (usage is null)
        {
            return LlmUsage.Zero;
        }

        var cached = usage.InputTokenDetails?.CachedTokenCount ?? 0;
        var reasoning = usage.OutputTokenDetails?.ReasoningTokenCount ?? 0;
        return new LlmUsage(
            InputTokens: Math.Max(0, usage.InputTokenCount - cached),
            OutputTokens: Math.Max(0, usage.OutputTokenCount - reasoning),
            CacheReadTokens: cached,
            CacheWriteTokens: 0, // automatic prefix caching has no write charge
            ReasoningTokens: reasoning);
    }

    private async Task<ChatCompletion> SendAsync(OpenAiChatRequest chat, bool allowReasoningEffortFallback, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.GetChatClient(chat.Model)
                .CompleteChatAsync(chat.Messages, chat.Options, cancellationToken)
                .ConfigureAwait(false);
            return result.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClientResultException ex) when (allowReasoningEffortFallback && chat.HasReasoningEffort && OpenAiErrorMapper.IsUnsupportedParameter(ex, ReasoningEffortParameter))
        {
            // The model takes no reasoning_effort (older GPT models, most self-hosted servers): retry once without it.
            _modelsWithoutReasoningEffort[chat.Model] = true;
            chat.ClearReasoningEffort();
            return await SendAsync(chat, allowReasoningEffortFallback: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not LlmException)
        {
            throw OpenAiErrorMapper.Map(ex);
        }
    }
}
