using System.Threading.Channels;
using DocProc.Application.Pipeline;

namespace DocProc.Pipeline.Queue;

/// <summary>What a work item is about; with the id it forms the key the runner serialises work on.</summary>
public enum PipelineWorkKind
{
    Ingestion,
    Document,

    /// <summary>The id is a tenant's: the decide-and-commit half of stage 1 runs one ingestion at a time per tenant (<c>IngestCommitGate</c>).</summary>
    Tenant,
}

/// <summary>The unit of serialisation of the runner: one ingestion or one document (docs/03 "Idempotency and retries").</summary>
public readonly record struct PipelineWorkKey(PipelineWorkKind Kind, Guid Id);

/// <summary>One unit of work the runner consumes: an ingestion to unpack or a document stage to run.</summary>
public abstract record PipelineWorkItem(Guid TenantId)
{
    /// <summary>Two items with the same key never run at the same time in one process.</summary>
    public abstract PipelineWorkKey Key { get; }
}

public sealed record IngestionWorkItem(ProcessIngestion Message) : PipelineWorkItem(Message.TenantId)
{
    public override PipelineWorkKey Key => new(PipelineWorkKind.Ingestion, Message.IngestionId);
}

public sealed record DocumentWorkItem(ProcessDocument Message) : PipelineWorkItem(Message.TenantId)
{
    public override PipelineWorkKey Key => new(PipelineWorkKind.Document, Message.DocumentId);
}

/// <summary>The consumer side of the queue. The runner acknowledges every item it took, whatever the outcome.</summary>
public interface IPipelineQueueReader
{
    IAsyncEnumerable<PipelineWorkItem> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>Marks an item as handled (processed, dropped or re-enqueued), so <see cref="InProcessPipelineQueue.WaitForIdleAsync"/> can settle.</summary>
    void Acknowledge(PipelineWorkItem item);
}

/// <summary>
/// The in-process transport of docs/03 for local development and tests: an unbounded channel. A delayed enqueue
/// (retry with backoff) waits on a background continuation driven by the injected <see cref="TimeProvider"/> (so
/// tests advance a fake clock instead of waiting) that is cancelled when the queue is disposed or the caller's token
/// fires, so a stopping host never leaks timers. Not durable: anything queued is lost when the process ends, which is
/// why Azure uses Service Bus (a later block) and why every handler re-checks the document state.
/// </summary>
public sealed class InProcessPipelineQueue : IPipelineQueue, IPipelineQueueReader, IDisposable
{
    private readonly Channel<PipelineWorkItem> _channel = Channel.CreateUnbounded<PipelineWorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private int _inFlight;
    private int _delayed;
    private bool _disposed;
    private TaskCompletionSource _idle = Completed();

    /// <param name="timeProvider">Drives the delayed enqueues; the system clock when omitted.</param>
    public InProcessPipelineQueue(TimeProvider? timeProvider = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Items queued, waiting on a delay or being processed.</summary>
    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    /// <summary>Items still waiting on a delay (a subset of <see cref="InFlight"/>); tests advance the clock while this is positive.</summary>
    public int Delayed
    {
        get
        {
            lock (_gate)
            {
                return _delayed;
            }
        }
    }

    public Task EnqueueAsync(ProcessIngestion message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        Write(new IngestionWorkItem(message));
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(ProcessDocument message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        Write(new DocumentWorkItem(message));
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(ProcessDocument message, TimeSpan delay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (delay <= TimeSpan.Zero)
        {
            return EnqueueAsync(message, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        TrackDelayed();
        _ = WriteLaterAsync(new DocumentWorkItem(message), delay, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Ingestion retry with backoff; not part of <see cref="IPipelineQueue"/>, which only schedules document steps.</summary>
    public Task EnqueueAsync(ProcessIngestion message, TimeSpan delay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (delay <= TimeSpan.Zero)
        {
            return EnqueueAsync(message, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        TrackDelayed();
        _ = WriteLaterAsync(new IngestionWorkItem(message), delay, cancellationToken);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<PipelineWorkItem> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Acknowledge(PipelineWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Untrack();
    }

    /// <summary>Completes once nothing is queued, delayed or being processed. Tests use it instead of sleeping.</summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        Task idle;
        lock (_gate)
        {
            idle = _idle.Task;
        }

        return idle.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
        _channel.Writer.TryComplete();
        _lifetime.Dispose();
    }

    private void Write(PipelineWorkItem item)
    {
        Track();
        if (!_channel.Writer.TryWrite(item))
        {
            Untrack();
            throw new ObjectDisposedException(nameof(InProcessPipelineQueue));
        }
    }

    private async Task WriteLaterAsync(PipelineWorkItem item, TimeSpan delay, CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, callerToken);
        try
        {
            await Task.Delay(delay, _clock, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            UntrackDelayed(stillInFlight: false);
            return;
        }

        UntrackDelayed(stillInFlight: true);
        if (!_channel.Writer.TryWrite(item))
        {
            Untrack();
        }
    }

    private void Track()
    {
        lock (_gate)
        {
            if (_inFlight++ == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void TrackDelayed()
    {
        lock (_gate)
        {
            _delayed++;
            if (_inFlight++ == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void Untrack()
    {
        lock (_gate)
        {
            if (--_inFlight == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    /// <summary>The delay is over: the item moves from "delayed" to "queued", or leaves the in-flight count when it was cancelled.</summary>
    private void UntrackDelayed(bool stillInFlight)
    {
        lock (_gate)
        {
            _delayed--;
            if (!stillInFlight && --_inFlight == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
