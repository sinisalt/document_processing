using System.Collections.Concurrent;
using DocProc.Llm.Gateway;

namespace DocProc.Llm.Providers;

/// <summary>
/// Record mode (docs/09): serves a recorded answer when the store has one and otherwise calls the real provider and
/// writes what came back, so a live run with <c>Llm:Replay:Record</c> fills the fixture directory that CI replays.
/// </summary>
public sealed class RecordingLlmProvider(ILlmProvider inner, IRecordingReplayStore store) : ILlmProvider
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<LlmRequest>> _batches = new(StringComparer.Ordinal);

    public ILlmProvider Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public LlmProviderKind Kind => Inner.Kind;

    public LlmCapabilities Capabilities => Inner.Capabilities;

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var hash = RequestHasher.Hash(request);
        if (store.TryGet(hash, out var recorded))
        {
            return recorded;
        }

        var result = await Inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        store.Put(hash, request, result);
        return result;
    }

    public async Task<BatchHandle> SubmitBatchAsync(IReadOnlyList<LlmRequest> requests, CancellationToken cancellationToken)
    {
        var handle = await Inner.SubmitBatchAsync(requests, cancellationToken).ConfigureAwait(false);
        _batches[handle.ProviderBatchId] = requests.ToList();
        return handle;
    }

    public async Task<BatchResult> GetBatchAsync(BatchHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var batch = await Inner.GetBatchAsync(handle, cancellationToken).ConfigureAwait(false);
        if (batch.Status == BatchStatus.Completed && _batches.TryRemove(handle.ProviderBatchId, out var requests))
        {
            foreach (var item in batch.Items)
            {
                if (item.Result is { } result && item.Index < requests.Count)
                {
                    store.Put(RequestHasher.Hash(requests[item.Index]), requests[item.Index], result);
                }
            }
        }

        return batch;
    }

    public Task ValidateCredentialAsync(CancellationToken cancellationToken) => Inner.ValidateCredentialAsync(cancellationToken);
}

/// <summary>Wraps every real adapter's provider in a <see cref="RecordingLlmProvider"/>; the gateway sees one factory.</summary>
public sealed class RecordingLlmProviderFactory(IEnumerable<ILlmProviderFactory> inner, IRecordingReplayStore store) : ILlmProviderFactory
{
    private readonly IReadOnlyList<ILlmProviderFactory> _inner = inner.Where(f => f is not RecordingLlmProviderFactory).ToList();

    public bool Supports(LlmProviderKind kind) => _inner.Any(f => f.Supports(kind));

    public ILlmProvider Create(LlmProviderConfig config, LlmCredential credential)
    {
        ArgumentNullException.ThrowIfNull(config);
        var factory = _inner.FirstOrDefault(f => f.Supports(config.Kind))
            ?? throw new LlmException(LlmErrorKind.Unavailable, $"No adapter is registered for provider kind {config.Kind}.");
        return new RecordingLlmProvider(factory.Create(config, credential), store);
    }
}
