using DocProc.Pipeline.Queue;
using DocProc.Pipeline.Runner;

namespace DocProc.Pipeline.Tests;

public class KeyedWorkGateTests
{
    private static readonly PipelineWorkKey DocumentA = new(PipelineWorkKind.Document, Guid.NewGuid());
    private static readonly PipelineWorkKey DocumentB = new(PipelineWorkKind.Document, Guid.NewGuid());

    [Fact]
    public async Task The_same_key_runs_one_at_a_time_and_other_keys_run_alongside()
    {
        var gate = new KeyedWorkGate();
        var cancellation = TestContext.Current.CancellationToken;

        var first = await gate.EnterAsync(DocumentA, cancellation);
        var second = gate.EnterAsync(DocumentA, cancellation);
        var other = gate.EnterAsync(DocumentB, cancellation);

        (await other.WaitAsync(TimeSpan.FromSeconds(5), cancellation)).Dispose();
        second.IsCompleted.ShouldBeFalse("the twin waits for the first lease");
        gate.ActiveKeys.ShouldBe(1);

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5), cancellation)).Dispose();
        gate.ActiveKeys.ShouldBe(0, "keys with nobody waiting are forgotten");
    }

    [Fact]
    public async Task Disposing_a_lease_twice_releases_once()
    {
        var gate = new KeyedWorkGate();
        var cancellation = TestContext.Current.CancellationToken;

        var lease = await gate.EnterAsync(DocumentA, cancellation);
        var waiting = gate.EnterAsync(DocumentA, cancellation);
        lease.Dispose();
        lease.Dispose();

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5), cancellation)).Dispose();
        var third = await gate.EnterAsync(DocumentA, cancellation);
        var fourth = gate.EnterAsync(DocumentA, cancellation);
        fourth.IsCompleted.ShouldBeFalse("a double dispose must not let two holders in");
        third.Dispose();
        (await fourth.WaitAsync(TimeSpan.FromSeconds(5), cancellation)).Dispose();
    }

    [Fact]
    public async Task A_cancelled_waiter_leaves_the_key_clean()
    {
        var gate = new KeyedWorkGate();
        using var waiter = new CancellationTokenSource();

        var holder = await gate.EnterAsync(DocumentA, TestContext.Current.CancellationToken);
        var waiting = gate.EnterAsync(DocumentA, waiter.Token);
        await waiter.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        gate.ActiveKeys.ShouldBe(1, "the holder is still in");
        holder.Dispose();
        gate.ActiveKeys.ShouldBe(0);
    }
}
