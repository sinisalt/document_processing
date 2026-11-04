namespace DocProc.Pipeline.Runner;

/// <summary>
/// At most <see cref="Capacity"/> holders per tenant: the runner enters it before a delivery attempt takes a slot of
/// the delivery lane, so one tenant's slow receiver fills its own share and leaves the rest of the lane to the other
/// tenants. Tenants with nobody holding or waiting are forgotten, so the gate stays as small as the work in flight.
/// </summary>
internal sealed class TenantWorkGate(int capacity)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    public int Capacity { get; } = Math.Max(1, capacity);

    /// <summary>Tenants with a holder or a waiter right now.</summary>
    public int ActiveTenants
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Waits until the tenant has fewer than <see cref="Capacity"/> holders; disposing the lease lets the next waiter in.</summary>
    public async Task<IDisposable> EnterAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(tenantId, out var existing))
            {
                existing = new Entry(Capacity);
                _entries[tenantId] = existing;
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
            Leave(tenantId, entry, release: false);
            throw;
        }

        return new Lease(this, tenantId, entry);
    }

    private void Leave(Guid tenantId, Entry entry, bool release)
    {
        lock (_gate)
        {
            if (release)
            {
                entry.Semaphore.Release();
            }

            if (--entry.Holders == 0)
            {
                _entries.Remove(tenantId);
                entry.Dispose();
            }
        }
    }

    private sealed class Entry(int capacity) : IDisposable
    {
        public SemaphoreSlim Semaphore { get; } = new(capacity, capacity);

        /// <summary>The holders plus everyone waiting behind them.</summary>
        public int Holders { get; set; }

        public void Dispose() => Semaphore.Dispose();
    }

    private sealed class Lease(TenantWorkGate owner, Guid tenantId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Leave(tenantId, entry, release: true);
            }
        }
    }
}
