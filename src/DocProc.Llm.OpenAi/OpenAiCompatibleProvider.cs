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

    /// <summary>Extra round trips one call may spend adjusting <c>reasoning_effort</c>: drop it once, or walk the ladder down.</summary>
    public static readonly int MaxReasoningEffortRetries = ReasoningEffortLadder.Levels.Count;

    private readonly OpenAIClient _client;
    private readonly string _validationModel;

    /// <summary>Models that rejected <c>reasoning_effort</c>; later calls omit it instead of paying a round trip.</summary>
    private readonly ConcurrentDictionary<string, bool> _modelsWithoutReasoningEffort = new(StringComparer.Ordinal);

    /// <summary>Per model and requested level, the level the model accepted instead (null: none); later calls start there.</summary>
    private readonly ConcurrentDictionary<(string Model, string Requested), ChatReasoningEffortLevel?> _degradedEfforts = new();

    public OpenAiCompatibleProvider(OpenAIClient client, LlmProviderKind kind, LlmCapabilities capabilities, string validationModel)
    {
        ArgumentNullException.ThrowIfNull(client);
        OpenAiCompatibleCapabilities.EnsureSupported(kind, nameof(kind));
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
        ApplyRememberedEffort(chat);

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

    private void ApplyRememberedEffort(OpenAiChatRequest chat)
    {
        if (chat.ReasoningEffort is not { } requested)
        {
            return;
        }

        if (_modelsWithoutReasoningEffort.ContainsKey(chat.Model))
        {
            chat.ClearReasoningEffort();
        }
        else if (_degradedEfforts.TryGetValue((chat.Model, requested.ToString()), out var accepted))
        {
            chat.ReasoningEffort = accepted;
        }
    }

    /// <summary>
    /// One call with at most <see cref="MaxReasoningEffortRetries"/> extra round trips when the model rejects
    /// <c>reasoning_effort</c>: an unsupported parameter is dropped, an unsupported value degrades to the nearest level
    /// (docs/08 "Reasoning/effort"). Each attempt gets the request's own timeout; expiry is a transient error.
    /// </summary>
    private async Task<ChatCompletion> SendAsync(OpenAiChatRequest chat, bool allowReasoningEffortFallback, CancellationToken cancellationToken)
    {
        var requested = chat.ReasoningEffort;
        var retriesLeft = allowReasoningEffortFallback ? MaxReasoningEffortRetries : 0;

        while (true)
        {
            try
            {
                var completion = await SendOnceAsync(chat, cancellationToken).ConfigureAwait(false);
                if (requested is { } level && chat.ReasoningEffort != level)
                {
                    _degradedEfforts[(chat.Model, level.ToString())] = chat.ReasoningEffort;
                }

                return completion;
            }
            catch (ClientResultException ex)
            {
                if (retriesLeft == 0 || !chat.HasReasoningEffort || !AdjustReasoningEffort(chat, ex))
                {
                    throw OpenAiErrorMapper.Map(ex);
                }

                retriesLeft--;
            }
            catch (Exception ex) when (ex is not LlmException and not OperationCanceledException)
            {
                throw OpenAiErrorMapper.Map(ex);
            }
        }
    }

    private async Task<ChatCompletion> SendOnceAsync(OpenAiChatRequest chat, CancellationToken cancellationToken)
    {
        // LlmOptions.Timeout bounds one network attempt; the client's NetworkTimeout stays the hard ceiling.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(chat.Timeout);

        try
        {
            var result = await _client.GetChatClient(chat.Model)
                .CompleteChatAsync(chat.Messages, chat.Options, attempt.Token)
                .ConfigureAwait(false);
            return result.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (attempt.IsCancellationRequested)
        {
            throw new LlmException(LlmErrorKind.Transient, $"The provider did not answer within {chat.Timeout}.", OpenAiErrorMapper.DefaultTransientDelay, ex);
        }
    }

    /// <summary>Changes the request's effort for the next attempt and says whether a retry is worth it.</summary>
    private bool AdjustReasoningEffort(OpenAiChatRequest chat, ClientResultException error)
    {
        switch (OpenAiErrorMapper.Classify(error, ReasoningEffortParameter))
        {
            case ParameterRejection.UnsupportedParameter:
                // The model takes no reasoning_effort (older GPT models, most self-hosted servers): retry without it.
                _modelsWithoutReasoningEffort[chat.Model] = true;
                chat.ClearReasoningEffort();
                return true;

            case ParameterRejection.UnsupportedValue:
                chat.ReasoningEffort = ReasoningEffortLadder.Degrade(chat.ReasoningEffort!.Value, OpenAiErrorMapper.SupportedValues(error));
                return true;

            default:
                return false;
        }
    }
}
