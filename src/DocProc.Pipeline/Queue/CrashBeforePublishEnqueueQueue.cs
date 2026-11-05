using DocProc.Application.Pipeline;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Queue;

/// <summary>
/// Development-only fault injection for the publish sweeper demo (docs/11 §23, P0; docs/09 "Demo: a decision whose
/// publish message is lost"): while the marker file exists, the next publish nudge of a decision (approve, reject or
/// auto-approval: a <see cref="PipelineStage.Publish"/> message of the normal flow) deletes the marker and ends the
/// process at once, after the decision and its outbox row were committed and before the message is queued. That is a
/// crash between commit and enqueue; the restarted host's <c>PublishSweeperHostedService</c> then publishes the
/// decision. Every other message, and every message while the marker is absent, goes straight to the inner queue.
/// Registered only in the Development environment and only when <see cref="PipelineOptions.CrashBeforePublishEnqueueMarkerKey"/>
/// names a file, so no other host has it.
/// </summary>
public sealed partial class CrashBeforePublishEnqueueQueue : IPipelineQueue
{
    private readonly IPipelineQueue _inner;
    private readonly string _markerPath;
    private readonly ILogger<CrashBeforePublishEnqueueQueue> _logger;
    private readonly Action<string> _crash;

    public CrashBeforePublishEnqueueQueue(IPipelineQueue inner, string markerPath, ILogger<CrashBeforePublishEnqueueQueue> logger, Action<string>? crash = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _markerPath = markerPath;
        _logger = logger;
        _crash = crash ?? Environment.FailFast;
    }

    public Task EnqueueAsync(ProcessIngestion message, CancellationToken cancellationToken) => _inner.EnqueueAsync(message, cancellationToken);

    public Task EnqueueAsync(ProcessDocument message, CancellationToken cancellationToken)
    {
        CrashIfArmed(message);
        return _inner.EnqueueAsync(message, cancellationToken);
    }

    public Task EnqueueAsync(ProcessDocument message, TimeSpan delay, CancellationToken cancellationToken)
    {
        CrashIfArmed(message);
        return _inner.EnqueueAsync(message, delay, cancellationToken);
    }

    public Task EnqueueAsync(ProcessDelivery message, TimeSpan delay, CancellationToken cancellationToken) => _inner.EnqueueAsync(message, delay, cancellationToken);

    private void CrashIfArmed(ProcessDocument message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Stage != PipelineStage.Publish || message.Origin is not null || message.Attempt != 1 || !File.Exists(_markerPath))
        {
            return;
        }

        // Disarm first: the restarted host must not crash again on the sweeper's or the next decision's message.
        File.Delete(_markerPath);
        LogCrashing(_logger, message.DocumentId, _markerPath);
        _crash($"Fault injection ({PipelineOptions.CrashBeforePublishEnqueueMarkerKey}): the decision on document {message.DocumentId} is committed; ending the process before its publish message is queued.");
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Fault injection: ending the process before the publish message of document {DocumentId} is queued (marker {Marker} removed).")]
    private static partial void LogCrashing(ILogger logger, Guid documentId, string marker);
}
