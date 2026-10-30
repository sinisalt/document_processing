using System.Collections.Concurrent;

namespace DocProc.Llm.Providers;

/// <summary>
/// Answers from a queue of canned results in call order, regardless of the request hash, so pipeline tests can be
/// written without pre-computing hashes (docs/09). Every request it saw is kept for assertions. Registered under
/// <see cref="LlmProviderKind.Replay"/> when <c>Llm:Replay:Scripted</c> is true.
/// </summary>
public sealed class ScriptedLlmProvider : ILlmProvider
{
    private readonly ConcurrentQueue<Func<LlmRequest, LlmResult>> _script = new();
    private readonly ConcurrentQueue<LlmRequest> _requests = new();
    private readonly ConcurrentDictionary<string, BatchResult> _batches = new(StringComparer.Ordinal);

    public LlmProviderKind Kind => LlmProviderKind.Replay;

    public LlmCapabilities Capabilities => ReplayLlmProvider.ReplayCapabilities;

    /// <summary>Requests received so far, oldest first.</summary>
    public IReadOnlyList<LlmRequest> Requests => _requests.ToList();

    public int Remaining => _script.Count;

    public ScriptedLlmProvider Enqueue(LlmResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _script.Enqueue(_ => result);
        return this;
    }

    /// <summary>Scripts a successful structured answer; the model name defaults to whatever the request asked for.</summary>
    public ScriptedLlmProvider Enqueue(string json, string stopReason = StopReasons.EndTurn, LlmUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        _script.Enqueue(request => new LlmResult { Json = json, Usage = usage ?? LlmUsage.Zero, Model = request.Model, StopReason = stopReason });
        return this;
    }

    /// <summary>Scripts a failure: the exception is thrown when that turn is reached.</summary>
    public ScriptedLlmProvider Enqueue(LlmException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        _script.Enqueue(_ => throw failure);
        return this;
    }

    public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Enqueue(request);
        if (!_script.TryDequeue(out var answer))
        {
            throw new LlmException(
                LlmErrorKind.InvalidRequest,
                $"The scripted provider has no answer left for request {_requests.Count} (stage {request.Stage}, prompt {request.Prompt.CacheKey}).");
        }

        return Task.FromResult(answer(request));
    }

    public async Task<BatchHandle> SubmitBatchAsync(IReadOnlyList<LlmRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var id = Guid.NewGuid().ToString("N");
        var items = new List<BatchItem>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
        {
            try
            {
                items.Add(new BatchItem(i, await CompleteAsync(requests[i], cancellationToken).ConfigureAwait(false), null));
            }
            catch (LlmException ex)
            {
                items.Add(new BatchItem(i, null, ex));
            }
        }

        _batches[id] = new BatchResult(BatchStatus.Completed, items);
        return new BatchHandle(Kind, id, DateTimeOffset.UtcNow, requests.Count);
    }

    public Task<BatchResult> GetBatchAsync(BatchHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return _batches.TryGetValue(handle.ProviderBatchId, out var result)
            ? Task.FromResult(result)
            : throw new LlmException(LlmErrorKind.InvalidRequest, $"Unknown batch {handle.ProviderBatchId}.");
    }

    public Task ValidateCredentialAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Serves the one shared <see cref="ScriptedLlmProvider"/> for every Replay configuration.</summary>
public sealed class ScriptedLlmProviderFactory(ScriptedLlmProvider provider) : ILlmProviderFactory
{
    public bool Supports(LlmProviderKind kind) => kind == LlmProviderKind.Replay;

    public ILlmProvider Create(LlmProviderConfig config, LlmCredential credential) => provider;
}
