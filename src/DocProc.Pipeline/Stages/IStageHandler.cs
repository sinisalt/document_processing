using DocProc.Application.Pipeline;

namespace DocProc.Pipeline.Stages;

/// <summary>What a stage handler reports back to the runner.</summary>
public enum StageOutcome
{
    /// <summary>The stage completed and the document moved on; the runner enqueues the next stage.</summary>
    Advanced,

    /// <summary>The message was stale (the stage had already run, or the document is elsewhere); nothing was changed.</summary>
    Skipped,

    /// <summary>The stage completed but decided the next step itself (review, auto-approval, publish); the runner enqueues nothing.</summary>
    Stopped,
}

/// <summary>
/// One worker stage of docs/03 for one document message. Implementations are scoped (one DbContext per message),
/// re-check the current <c>Document.Status</c> before doing work and are safe to run twice. Failures are thrown: the
/// runner applies the failure policy (docs/03 "Idempotency and retries").
/// </summary>
public interface IStageHandler
{
    PipelineStage Stage { get; }

    Task<StageOutcome> HandleAsync(ProcessDocument message, CancellationToken cancellationToken);
}

/// <summary>Stage 1 (docs/03): turns an ingestion's items into documents and queues them for stage 2.</summary>
public interface IIngestionHandler
{
    Task HandleAsync(ProcessIngestion message, CancellationToken cancellationToken);
}
