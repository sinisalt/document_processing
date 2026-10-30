using DocProc.Application.Pipeline;
using DocProc.Pipeline.Queue;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Pipeline.Tests;

public class InProcessPipelineQueueTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public async Task Delivers_in_order_and_settles_when_everything_is_acknowledged()
    {
        using var queue = new InProcessPipelineQueue();
        queue.WaitForIdleAsync(CancellationToken.None).IsCompletedSuccessfully.ShouldBeTrue("a fresh queue is idle");

        var ingestion = new ProcessIngestion(Tenant, Guid.NewGuid());
        var document = new ProcessDocument(Tenant, Guid.NewGuid(), PipelineStage.Normalize);
        await queue.EnqueueAsync(ingestion, CancellationToken.None);
        await queue.EnqueueAsync(document, CancellationToken.None);
        queue.InFlight.ShouldBe(2);
        var idle = queue.WaitForIdleAsync(CancellationToken.None);
        idle.IsCompleted.ShouldBeFalse();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var items = new List<PipelineWorkItem>();
        await foreach (var item in queue.ReadAllAsync(cts.Token))
        {
            items.Add(item);
            queue.Acknowledge(item);
            if (items.Count == 2)
            {
                break;
            }
        }

        items[0].ShouldBeOfType<IngestionWorkItem>().Message.ShouldBe(ingestion);
        items[0].Key.ShouldBe(new PipelineWorkKey(PipelineWorkKind.Ingestion, ingestion.IngestionId));
        items[1].ShouldBeOfType<DocumentWorkItem>().Message.ShouldBe(document);
        items[1].Key.ShouldBe(new PipelineWorkKey(PipelineWorkKind.Document, document.DocumentId));
        await idle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        queue.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task Delayed_enqueue_waits_on_the_injected_clock_and_counts_as_in_flight_meanwhile()
    {
        var clock = new FakeTimeProvider();
        using var queue = new InProcessPipelineQueue(clock);
        var retry = new ProcessDocument(Tenant, Guid.NewGuid(), PipelineStage.Extract, Attempt: 2);

        await queue.EnqueueAsync(retry, TimeSpan.FromMinutes(5), CancellationToken.None);
        queue.InFlight.ShouldBe(1);
        queue.Delayed.ShouldBe(1);
        queue.WaitForIdleAsync(CancellationToken.None).IsCompleted.ShouldBeFalse("the delayed message is pending");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = reader.MoveNextAsync();
        clock.Advance(TimeSpan.FromMinutes(4));
        next.IsCompleted.ShouldBeFalse("four minutes of fake time are not five");
        queue.Delayed.ShouldBe(1);

        clock.Advance(TimeSpan.FromMinutes(1));

        (await next).ShouldBeTrue();
        reader.Current.ShouldBeOfType<DocumentWorkItem>().Message.ShouldBe(retry);
        queue.Delayed.ShouldBe(0);
        queue.InFlight.ShouldBe(1, "queued and read, not yet acknowledged");
        queue.Acknowledge(reader.Current);
        await queue.WaitForIdleAsync(cts.Token);
    }

    [Fact]
    public async Task Cancelled_delay_drops_the_message_and_still_settles()
    {
        var clock = new FakeTimeProvider();
        using var queue = new InProcessPipelineQueue(clock);
        using var caller = new CancellationTokenSource();
        await queue.EnqueueAsync(new ProcessDocument(Tenant, Guid.NewGuid(), PipelineStage.Extract), TimeSpan.FromMinutes(5), caller.Token);
        queue.InFlight.ShouldBe(1);

        await caller.CancelAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync(cts.Token);
        queue.InFlight.ShouldBe(0);
        queue.Delayed.ShouldBe(0);
    }

    [Fact]
    public async Task Disposing_cancels_pending_delays()
    {
        var clock = new FakeTimeProvider();
        var queue = new InProcessPipelineQueue(clock);
        await queue.EnqueueAsync(new ProcessDocument(Tenant, Guid.NewGuid(), PipelineStage.Extract), TimeSpan.FromMinutes(5), CancellationToken.None);

        queue.Dispose();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync(cts.Token);
        queue.Delayed.ShouldBe(0);
        Should.Throw<ObjectDisposedException>(() => queue.EnqueueAsync(new ProcessIngestion(Tenant, Guid.NewGuid()), CancellationToken.None));
    }
}
