using DocProc.Domain.Documents;

namespace DocProc.Application.Pipeline;

/// <summary>Worker stages (docs/03). Each consumes a message, checks the current document state, and is safe to run twice.</summary>
public enum PipelineStage
{
    Ingest,
    Normalize,
    Split,
    Classify,
    Match,
    Extract,
    Validate,
    Review,
    Publish,
    SynthesizeProfile,
}

public static class PipelineStages
{
    /// <summary>The document status a stage leaves behind when it succeeds; null for stages that do not move the document.</summary>
    public static DocumentStatus? ResultingStatus(PipelineStage stage) => stage switch
    {
        PipelineStage.Ingest => DocumentStatus.Received,
        PipelineStage.Normalize => DocumentStatus.Normalized,
        PipelineStage.Split => DocumentStatus.Split,
        PipelineStage.Classify => DocumentStatus.Classified,
        PipelineStage.Match => DocumentStatus.Matched,
        PipelineStage.Extract => DocumentStatus.Extracted,
        PipelineStage.Validate => DocumentStatus.Validated,
        _ => null,
    };

    /// <summary>The stage that follows on the happy path; null after Validate (the state machine decides review vs auto-approval).</summary>
    public static PipelineStage? Next(PipelineStage stage) => stage switch
    {
        PipelineStage.Ingest => PipelineStage.Normalize,
        PipelineStage.Normalize => PipelineStage.Split,
        PipelineStage.Split => PipelineStage.Classify,
        PipelineStage.Classify => PipelineStage.Match,
        PipelineStage.Match => PipelineStage.Extract,
        PipelineStage.Extract => PipelineStage.Validate,
        _ => null,
    };
}

/// <summary>Queue message for one document step (docs/03 "Idempotency and retries").</summary>
public sealed record ProcessDocument(Guid TenantId, Guid DocumentId, PipelineStage Stage, int Attempt = 1)
{
    public ProcessDocument Retry() => this with { Attempt = Attempt + 1 };

    public ProcessDocument? Next() => PipelineStages.Next(Stage) is { } next ? new ProcessDocument(TenantId, DocumentId, next) : null;
}

/// <summary>Queue message for an ingestion (one email, file or API call) before any document exists.</summary>
public sealed record ProcessIngestion(Guid TenantId, Guid IngestionId, int Attempt = 1);

/// <summary>Outbox event emitted at stage 9 and delivered to webhooks.</summary>
public sealed record DocumentEventMessage(Guid TenantId, Guid DocumentId, string EventType, DateTimeOffset At);
