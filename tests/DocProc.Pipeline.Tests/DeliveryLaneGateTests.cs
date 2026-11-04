using DocProc.Pipeline.Runner;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The slots of the delivery lane and each tenant's share: a tenant at its share waits, a tenant that holds a slot
/// never takes the last free one, and a tenant with nothing in flight finds that one.
/// </summary>
public class DeliveryLaneGateTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid TenantC = Guid.NewGuid();
    private static readonly Guid TenantD = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_tenant_at_its_share_waits_and_another_tenant_enters_at_once()
    {
        var gate = new DeliveryLaneGate(4, 2);

        var first = await gate.EnterAsync(TenantA, Ct);
        var second = await gate.EnterAsync(TenantA, Ct);
        var third = gate.EnterAsync(TenantA, Ct);
        var other = gate.EnterAsync(TenantB, Ct);

        (await other.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        third.IsCompleted.ShouldBeFalse("tenant A holds its two slots");

        first.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        second.Dispose();
        gate.ActiveTenants.ShouldBe(0, "tenants with nobody holding or waiting are forgotten");
        gate.Free.ShouldBe(4);
    }

    [Fact]
    public async Task Two_tenants_whose_receivers_never_answer_leave_a_slot_for_the_others()
    {
        // The defaults: four slots, two per tenant. Before, A and B held two each and every other tenant waited for
        // up to the receivers' timeout on every retry round.
        var gate = new DeliveryLaneGate(4, 2);
        var a1 = await gate.EnterAsync(TenantA, Ct);
        var a2 = await gate.EnterAsync(TenantA, Ct);
        var b1 = await gate.EnterAsync(TenantB, Ct);
        var b2 = gate.EnterAsync(TenantB, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        b2.IsCompleted.ShouldBeFalse("tenant B holds a slot, and the one left is kept for a tenant that holds none");
        gate.Free.ShouldBe(1);

        // Every other tenant still gets through, one after the other.
        for (var i = 0; i < 3; i++)
        {
            (await gate.EnterAsync(Guid.NewGuid(), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        }

        b2.IsCompleted.ShouldBeFalse("a slot freed by a tenant holding none goes back to the reserve, not to B's second attempt");

        // A third tenant with a slow receiver takes the last slot: only now is the lane full.
        var c1 = await gate.EnterAsync(TenantC, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        gate.Free.ShouldBe(0);
        var d1 = gate.EnterAsync(TenantD, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        d1.IsCompleted.ShouldBeFalse();

        // A frees a slot: D, which holds none, takes it before B's second attempt, first in line but holding one.
        a1.Dispose();
        (await d1.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        b2.IsCompleted.ShouldBeFalse("one slot is free again and B holds one");

        // C finishes: two are free, so B may take its second one.
        c1.Dispose();
        (await b2.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();

        a2.Dispose();
        b1.Dispose();
        gate.Free.ShouldBe(4);
        gate.ActiveTenants.ShouldBe(0);
    }

    [Fact]
    public async Task One_tenant_alone_never_takes_the_last_slot()
    {
        var gate = new DeliveryLaneGate(2, 2);
        var first = await gate.EnterAsync(TenantA, Ct);
        var second = gate.EnterAsync(TenantA, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        second.IsCompleted.ShouldBeFalse("with two slots a tenant holds one, and the other is the reserve");
        (await gate.EnterAsync(TenantB, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();

        var single = new DeliveryLaneGate(1, 2);
        var only = await single.EnterAsync(TenantA, Ct);
        single.Free.ShouldBe(0, "with one slot the tenant that holds none takes it");
        only.Dispose();
    }

    [Fact]
    public async Task A_cancelled_wait_leaves_no_slot_taken_and_a_lease_released_twice_counts_once()
    {
        var gate = new DeliveryLaneGate(1, 1);
        var lease = await gate.EnterAsync(TenantA, Ct);
        using (var stop = new CancellationTokenSource())
        {
            var waiting = gate.EnterAsync(TenantA, stop.Token);
            await stop.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(waiting);
        }

        using (var stopped = new CancellationTokenSource())
        {
            await stopped.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(gate.EnterAsync(TenantB, stopped.Token));
        }

        var next = gate.EnterAsync(TenantA, Ct);
        var after = gate.EnterAsync(TenantA, Ct);
        lease.Dispose();
        lease.Dispose();
        var nextLease = await next.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        after.IsCompleted.ShouldBeFalse("the second dispose released nothing");
        nextLease.Dispose();
        (await after.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        gate.ActiveTenants.ShouldBe(0);
        gate.Free.ShouldBe(1);
        gate.PerTenant.ShouldBe(1);
        new DeliveryLaneGate(0, 0).Slots.ShouldBe(1, "a lane of nothing would stop every delivery");
        new DeliveryLaneGate(0, 0).PerTenant.ShouldBe(1);
    }

    [Fact]
    public async Task Many_attempts_of_many_tenants_never_exceed_the_slots_or_a_share()
    {
        var gate = new DeliveryLaneGate(4, 2);
        var tenants = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var running = 0;
        var maxRunning = 0;
        var perTenant = tenants.ToDictionary(t => t, _ => 0);
        var sync = new Lock();
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(
            async () =>
            {
                var tenant = tenants[i % tenants.Length];
                using (await gate.EnterAsync(tenant, Ct))
                {
                    lock (sync)
                    {
                        running++;
                        maxRunning = Math.Max(maxRunning, running);
                        (++perTenant[tenant]).ShouldBeLessThanOrEqualTo(2);
                        running.ShouldBeLessThanOrEqualTo(4);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(1), Ct);
                    lock (sync)
                    {
                        running--;
                        perTenant[tenant]--;
                    }
                }
            },
            Ct)));

        maxRunning.ShouldBeGreaterThan(1);
        gate.Free.ShouldBe(4);
        gate.ActiveTenants.ShouldBe(0);
    }
}
