using DocProc.Application.Pipeline;
using DocProc.Pipeline.Queue;

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
        items[1].ShouldBeOfType<DocumentWorkItem>().Message.ShouldBe(document);
        await idle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        queue.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task Delayed_enqueue_arrives_after_the_delay_and_counts_as_in_flight_meanwhile()
    {
        using var queue = new InProcessPipelineQueue();
        var retry = new ProcessDocument(Tenant, Guid.NewGuid(), PipelineStage.Extract, Attempt: 2);

        await queue.EnqueueAsync(retry, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        queue.InFlight.ShouldBe(1);
        queue.WaitForIdleAsync(CancellationToken.None).IsCompleted.ShouldBeFalse("the delayed message is pending");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await reader.MoveNextAsync()).ShouldBeTrue();
        reader.Current.ShouldBeOfType<DocumentWorkItem>().Message.ShouldBe(retry);
        queue.Acknowledge(reader.Current);
        await queue.WaitForIdleAsync(cts.Token);
    }

    [Fact]
    public async Task Cancelled_delay_drops_the_message_and_still_settles()
    {
        using var queue = new InProcessPipelineQueue();
        using var caller = new CancellationTokenSource();
        await queue.EnqueueAsync(new ProcessDocument(Tenant, Guid.NewGuid(), PipelineStage.Extract), TimeSpan.FromMinutes(5), caller.Token);
        queue.InFlight.ShouldBe(1);

        await caller.CancelAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync(cts.Token);
        queue.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task Disposing_cancels_pending_delays()
    {
        var queue = new InProcessPipelineQueue();
        await queue.EnqueueAsync(new ProcessDocument(Tenant, Guid.NewGuid(), PipelineStage.Extract), TimeSpan.FromMinutes(5), CancellationToken.None);

        queue.Dispose();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync(cts.Token);
        Should.Throw<ObjectDisposedException>(() => queue.EnqueueAsync(new ProcessIngestion(Tenant, Guid.NewGuid()), CancellationToken.None));
    }
}
