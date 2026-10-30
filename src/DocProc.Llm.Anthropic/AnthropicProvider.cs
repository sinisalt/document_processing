using System.Diagnostics;
using Anthropic;
using Anthropic.Models.Messages;
using DocProc.Llm.Providers;

namespace DocProc.Llm.Anthropic;

/// <summary>
/// Anthropic API adapter (docs/06). Claude on Microsoft Foundry uses the same request shape against the tenant's
/// Foundry endpoint; Azure-identity based auth for Foundry arrives with the BYOK work in Phase 2.
/// </summary>
public sealed class AnthropicProvider : ILlmProvider
{
    public static readonly LlmCapabilities AnthropicCapabilities = new(
        NativePdfInput: true,
        JsonSchemaOutput: true,
        PromptCaching: true,
        Batch: false, // Message Batches arrive with the eval harness in Phase 3.
        Vision: true,
        MaxPagesPerRequest: 600,
        MaxInputTokens: 200_000);

    private readonly IAnthropicClient _client;
    private readonly string _validationModel;

    public AnthropicProvider(IAnthropicClient client, LlmProviderKind kind, string validationModel)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (kind is not (LlmProviderKind.Anthropic or LlmProviderKind.AnthropicFoundry))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "This adapter serves Anthropic and Claude on Foundry only.");
        }

        _client = client;
        Kind = kind;
        _validationModel = validationModel;
    }

    public LlmProviderKind Kind { get; }

    public LlmCapabilities Capabilities => AnthropicCapabilities;

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var parameters = AnthropicRequestBuilder.Build(request, Kind);
        var started = Stopwatch.GetTimestamp();
        Message message;
        try
        {
            message = await _client.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not LlmException)
        {
            throw AnthropicErrorMapper.Map(ex);
        }

        return ToResult(message, Stopwatch.GetElapsedTime(started));
    }

    public Task<BatchHandle> SubmitBatchAsync(IReadOnlyList<LlmRequest> requests, CancellationToken cancellationToken) =>
        throw new LlmException(LlmErrorKind.InvalidRequest, "Message Batches are not wired up yet; run requests individually.");

    public Task<BatchResult> GetBatchAsync(BatchHandle handle, CancellationToken cancellationToken) =>
        throw new LlmException(LlmErrorKind.InvalidRequest, "Message Batches are not wired up yet.");

    public async Task ValidateCredentialAsync(CancellationToken cancellationToken)
    {
        var probe = new MessageCreateParams
        {
            Model = _validationModel,
            MaxTokens = 1,
            Messages = [new MessageParam(new UserMessageParam(new MessageContentParam("ping")))],
        };

        try
        {
            await _client.Messages.Create(probe, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not LlmException)
        {
            throw AnthropicErrorMapper.Map(ex);
        }
    }

    internal static LlmResult ToResult(Message message, TimeSpan latency)
    {
        var stopReason = message.StopReason?.Raw() ?? StopReasons.EndTurn;
        if (string.Equals(stopReason, "refusal", StringComparison.Ordinal))
        {
            throw new LlmException(LlmErrorKind.ContentRefused, "The model refused to process this document.");
        }

        var text = string.Concat(message.Content
            .Select(block => block.TryPickText(out var t) ? t.Text : null)
            .Where(t => t is not null));

        var usage = message.Usage;
        return new LlmResult
        {
            Json = text,
            Usage = new LlmUsage(
                InputTokens: checked((int)usage.InputTokens),
                OutputTokens: checked((int)usage.OutputTokens),
                CacheReadTokens: checked((int)(usage.CacheReadInputTokens ?? 0)),
                CacheWriteTokens: checked((int)(usage.CacheCreationInputTokens ?? 0))),
            Model = message.Model?.Raw() ?? string.Empty,
            StopReason = stopReason switch
            {
                "end_turn" => StopReasons.EndTurn,
                "max_tokens" or "model_context_window_exceeded" => StopReasons.MaxTokens,
                _ => stopReason,
            },
            ProviderRequestId = message.ID,
            Latency = latency,
        };
    }
}

/// <summary>Builds an <see cref="AnthropicProvider"/> per configuration. The client holds the key; nothing else does.</summary>
public sealed class AnthropicProviderFactory : ILlmProviderFactory
{
    public bool Supports(LlmProviderKind kind) => kind is LlmProviderKind.Anthropic or LlmProviderKind.AnthropicFoundry;

    public ILlmProvider Create(LlmProviderConfig config, LlmCredential credential)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            throw new LlmException(LlmErrorKind.Auth, $"Provider configuration {config.Id} has no API key.");
        }

        var client = config.Endpoint is { } endpoint
            ? new AnthropicClient
            {
                ApiKey = credential.ApiKey,
                BaseUrl = endpoint.ToString().TrimEnd('/'),
                MaxRetries = 0, // the worker owns retry policy via LlmException.IsRetryable
                Timeout = TimeSpan.FromMinutes(5),
            }
            : new AnthropicClient
            {
                ApiKey = credential.ApiKey,
                MaxRetries = 0,
                Timeout = TimeSpan.FromMinutes(5),
            };

        var validationModel = config.ModelFor(LlmStage.Classify) ?? config.Models.Values.FirstOrDefault() ?? "claude-haiku-5-5";
        return new AnthropicProvider(client, config.Kind, validationModel);
    }
}
