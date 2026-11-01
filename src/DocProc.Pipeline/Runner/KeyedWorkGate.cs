using DocProc.Pipeline.Queue;

namespace DocProc.Pipeline.Runner;

/// <summary>
/// Single flight per <see cref="PipelineWorkKey"/>: the runner enters the gate before it runs a message and two
/// messages for the same document (or the same ingestion) never run at the same time in one process. A copy that
/// arrives while its twin runs waits, then finds the stage already done and is dropped by the status check. Keys with
/// nobody waiting are forgotten, so the gate stays as small as the work in flight.
/// </summary>
internal sealed class KeyedWorkGate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<PipelineWorkKey, Entry> _entries = [];

    /// <summary>Keys with a holder or a waiter right now.</summary>
    public int ActiveKeys
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Waits until no other holder has <paramref name="key"/>; disposing the lease lets the next waiter in.</summary>
    public async Task<IDisposable> EnterAsync(PipelineWorkKey key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var existing))
            {
                existing = new Entry();
                _entries[key] = existing;
            }

            entry = existing;
            entry.Holders++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Leave(key, entry, release: false);
            throw;
        }

        return new Lease(this, key, entry);
    }

    private void Leave(PipelineWorkKey key, Entry entry, bool release)
    {
        lock (_gate)
        {
            if (release)
            {
                entry.Semaphore.Release();
            }

            if (--entry.Holders == 0)
            {
                _entries.Remove(key);
                entry.Dispose();
            }
        }
    }

    private sealed class Entry : IDisposable
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>The holder plus everyone waiting behind it.</summary>
        public int Holders { get; set; }

        public void Dispose() => Semaphore.Dispose();
    }

    private sealed class Lease(KeyedWorkGate owner, PipelineWorkKey key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Leave(key, entry, release: true);
            }
        }
    }
}
