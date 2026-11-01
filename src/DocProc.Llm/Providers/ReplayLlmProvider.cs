using System.Collections.Concurrent;
using DocProc.Llm.Gateway;

namespace DocProc.Llm.Providers;

/// <summary>Where recorded responses live: a dictionary in unit tests, a fixture directory in integration tests.</summary>
public interface IReplayStore
{
    bool TryGet(string requestHash, out LlmResult result);

    void Put(string requestHash, LlmResult result);
}

public sealed class InMemoryReplayStore : IReplayStore
{
    private readonly ConcurrentDictionary<string, LlmResult> _items = new(StringComparer.Ordinal);

    public int Count => _items.Count;

    public bool TryGet(string requestHash, out LlmResult result) => _items.TryGetValue(requestHash, out result!);

    public void Put(string requestHash, LlmResult result) => _items[requestHash] = result;
}

/// <summary>
/// What a replay miss tells the reader in place of the default "re-record the fixture" advice. A host registers one
/// when a miss means something else than a stale fixture: the development demo data does, because there a miss means
/// the document has no demo fixture at all (docs/09 "Demo data").
/// </summary>
public sealed record LlmReplayMissHint(string Text);

/// <summary>
/// Serves recorded responses keyed by request hash, so the full pipeline runs in CI without a provider or a key
/// (docs/09 "LLM providers"). A miss is an <see cref="LlmErrorKind.InvalidRequest"/>: the fixture is out of date, or,
/// with a <paramref name="missHint"/>, whatever the hint says.
/// </summary>
public sealed class ReplayLlmProvider(IReplayStore store, string? missHint = null) : ILlmProvider
{
    public const string DefaultMissHint = "Re-record the fixture with LLM_LIVE=1.";

    public static readonly LlmCapabilities ReplayCapabilities = new(
        NativePdfInput: true, JsonSchemaOutput: true, PromptCaching: true, Batch: true, Vision: true,
        MaxPagesPerRequest: 600, MaxInputTokens: 200_000);

    public LlmProviderKind Kind => LlmProviderKind.Replay;

    public LlmCapabilities Capabilities => ReplayCapabilities;

    public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var hash = RequestHasher.Hash(request);
        if (store.TryGet(hash, out var result))
        {
            return Task.FromResult(result);
        }

        throw new LlmException(
            LlmErrorKind.InvalidRequest,
            $"No recorded response for request {hash[..12]} (stage {request.Stage}, model {request.Model}, prompt {request.Prompt.CacheKey}). {(string.IsNullOrWhiteSpace(missHint) ? DefaultMissHint : missHint)}");
    }

    public async Task<BatchHandle> SubmitBatchAsync(IReadOnlyList<LlmRequest> requests, CancellationToken cancellationToken)
    {
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

    public Task<BatchResult> GetBatchAsync(BatchHandle handle, CancellationToken cancellationToken) =>
        _batches.TryGetValue(handle.ProviderBatchId, out var result)
            ? Task.FromResult(result)
            : throw new LlmException(LlmErrorKind.InvalidRequest, $"Unknown batch {handle.ProviderBatchId}.");

    public Task ValidateCredentialAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private readonly ConcurrentDictionary<string, BatchResult> _batches = new(StringComparer.Ordinal);
}

public sealed class ReplayLlmProviderFactory(IReplayStore store, string? missHint = null) : ILlmProviderFactory
{
    public bool Supports(LlmProviderKind kind) => kind == LlmProviderKind.Replay;

    public ILlmProvider Create(LlmProviderConfig config, LlmCredential credential) => new ReplayLlmProvider(store, missHint);
}
