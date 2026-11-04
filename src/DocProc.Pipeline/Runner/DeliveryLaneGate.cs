namespace DocProc.Pipeline.Runner;

/// <summary>
/// The slots of the delivery lane (<see cref="Slots"/>, <c>Delivery:MaxParallelism</c>) and each tenant's share of
/// them (<see cref="PerTenant"/>, <c>Delivery:MaxParallelismPerTenant</c>). An attempt waits on its tenant's receiver
/// for up to the destination's timeout, so a slot can be held for minutes by a receiver that never answers. Two rules
/// keep such receivers from holding up the other tenants:
/// <list type="bullet">
/// <item><description>a tenant holds at most <see cref="PerTenant"/> slots;</description></item>
/// <item><description>a tenant that holds a slot never takes the last free one: it is kept for a tenant that holds
/// none. So tenants that already deliver share at most <see cref="Slots"/> - 1 slots, and a tenant with nothing in
/// flight finds a slot as long as no other such tenant took it.</description></item>
/// </list>
/// The lane is full only when the last slot went to a tenant that held none, so slow receivers fill it only when at
/// least ceil((<see cref="Slots"/> - 1) / <see cref="PerTenant"/>) + 1 tenants have them (three with the defaults 4
/// and 2; with fewer slots than that there is no way around it). A waiter is let in when a slot frees, the first in
/// line whose tenant may take it; tenants with nobody holding or waiting are forgotten.
/// </summary>
internal sealed class DeliveryLaneGate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, int> _held = [];
    private readonly LinkedList<Waiter> _waiting = [];
    private int _free;

    public DeliveryLaneGate(int slots, int perTenant)
    {
        Slots = Math.Max(1, slots);
        PerTenant = Math.Max(1, perTenant);
        _free = Slots;
    }

    public int Slots { get; }

    public int PerTenant { get; }

    /// <summary>Slots nobody holds right now.</summary>
    public int Free
    {
        get
        {
            lock (_gate)
            {
                return _free;
            }
        }
    }

    /// <summary>Tenants with a slot or a waiter right now.</summary>
    public int ActiveTenants
    {
        get
        {
            lock (_gate)
            {
                return _held.Keys.Union(_waiting.Select(w => w.TenantId)).Count();
            }
        }
    }

    /// <summary>Waits for a slot the tenant may take; disposing the lease frees it for the next waiter.</summary>
    public Task<IDisposable> EnterAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IDisposable>(cancellationToken);
        }

        Waiter waiter;
        lock (_gate)
        {
            // Nobody waiting could take a slot right now (every change lets in whoever may), so a newcomer that may
            // take one passes nobody who could.
            if (MayTake(tenantId))
            {
                return Task.FromResult<IDisposable>(Take(tenantId));
            }

            waiter = new Waiter(tenantId);
            waiter.Node = _waiting.AddLast(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            waiter.Registration = cancellationToken.Register(() => Cancel(waiter, cancellationToken));

            // Let in before the registration was stored: Complete may have found none to dispose.
            if (waiter.Completion.Task.IsCompleted)
            {
                waiter.Registration.Dispose();
            }
        }

        return waiter.Completion.Task;
    }

    /// <summary>Whether <paramref name="tenantId"/> may take a slot now: one is free, the tenant is below its share, and it takes the last one only when it holds none.</summary>
    private bool MayTake(Guid tenantId)
    {
        var held = _held.GetValueOrDefault(tenantId);
        return _free > 0 && held < PerTenant && (held == 0 || _free > 1);
    }

    private Lease Take(Guid tenantId)
    {
        _free--;
        _held[tenantId] = _held.GetValueOrDefault(tenantId) + 1;
        return new Lease(this, tenantId);
    }

    private void Release(Guid tenantId)
    {
        List<(Waiter Waiter, Lease Lease)> admitted;
        lock (_gate)
        {
            _free++;
            if (--_held[tenantId] == 0)
            {
                _held.Remove(tenantId);
            }

            admitted = Admit();
        }

        Complete(admitted);
    }

    /// <summary>Lets in, first in line first, every waiter whose tenant may take a slot now. Called under the lock.</summary>
    private List<(Waiter Waiter, Lease Lease)> Admit()
    {
        var admitted = new List<(Waiter, Lease)>();
        for (var node = _waiting.First; node is not null && _free > 0;)
        {
            var next = node.Next;
            if (MayTake(node.Value.TenantId))
            {
                _waiting.Remove(node);
                node.Value.Node = null;
                admitted.Add((node.Value, Take(node.Value.TenantId)));
            }

            node = next;
        }

        return admitted;
    }

    private static void Complete(List<(Waiter Waiter, Lease Lease)> admitted)
    {
        foreach (var (waiter, lease) in admitted)
        {
            // Only a waiter still in line is let in, and only one still in line is cancelled, so this always succeeds;
            // the registration is disposed after it, so EnterAsync sees the task completed if it stores one later.
            if (!waiter.Completion.TrySetResult(lease))
            {
                lease.Dispose();
            }

            waiter.Registration.Dispose();
        }
    }

    private void Cancel(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (waiter.Node is null)
            {
                // Let in already: the lease is the caller's to dispose.
                return;
            }

            _waiting.Remove(waiter.Node);
            waiter.Node = null;
        }

        waiter.Completion.TrySetCanceled(cancellationToken);
    }

    private sealed class Waiter(Guid tenantId)
    {
        public Guid TenantId { get; } = tenantId;

        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The waiter's place in line; null once it was let in or gave up.</summary>
        public LinkedListNode<Waiter>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class Lease(DeliveryLaneGate owner, Guid tenantId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(tenantId);
            }
        }
    }
}
