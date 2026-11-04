using DocProc.Pipeline.Runner;

namespace DocProc.Pipeline.Tests;

/// <summary>The per-tenant share of the delivery lane: a tenant at its cap waits, every other tenant goes on.</summary>
public class TenantWorkGateTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    [Fact]
    public async Task A_tenant_at_its_cap_waits_and_another_tenant_enters_at_once()
    {
        var gate = new TenantWorkGate(2);
        var cancellation = TestContext.Current.CancellationToken;

        var first = await gate.EnterAsync(TenantA, cancellation);
        var second = await gate.EnterAsync(TenantA, cancellation);
        var third = gate.EnterAsync(TenantA, cancellation);
        var other = gate.EnterAsync(TenantB, cancellation);

        (await other.WaitAsync(TimeSpan.FromSeconds(5), cancellation)).Dispose();
        third.IsCompleted.ShouldBeFalse("tenant A holds its two places");

        first.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5), cancellation)).Dispose();
        second.Dispose();
        gate.ActiveTenants.ShouldBe(0, "tenants with nobody holding or waiting are forgotten");
    }

    [Fact]
    public async Task A_cancelled_wait_leaves_no_place_taken_and_a_lease_released_twice_counts_once()
    {
        var gate = new TenantWorkGate(1);
        var cancellation = TestContext.Current.CancellationToken;
        var lease = await gate.EnterAsync(TenantA, cancellation);
        using (var stop = new CancellationTokenSource())
        {
            var waiting = gate.EnterAsync(TenantA, stop.Token);
            await stop.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(waiting);
        }

        var next = gate.EnterAsync(TenantA, cancellation);
        var after = gate.EnterAsync(TenantA, cancellation);
        lease.Dispose();
        lease.Dispose();
        var nextLease = await next.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation);
        after.IsCompleted.ShouldBeFalse("the second dispose released nothing");
        nextLease.Dispose();
        (await after.WaitAsync(TimeSpan.FromSeconds(5), cancellation)).Dispose();
        gate.ActiveTenants.ShouldBe(0);
        gate.Capacity.ShouldBe(1);
        new TenantWorkGate(0).Capacity.ShouldBe(1, "a cap of nothing would stop every delivery");
    }
}
