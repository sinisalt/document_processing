using DocProc.Llm;
using DocProc.Llm.Gateway;

namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>
/// A resolved pipeline as it ran (docs/11 §10.1 <c>pipelineHash</c>): content-addressed and immutable, one row per
/// tenant and hash. Every document processed with <c>configurableSteps</c> carries the hash of its last stage's
/// pipeline (<c>Documents.PipelineHash</c>), so the steps and parameters a document ran with stay readable after the
/// supplier's or the layout's overrides change (<c>GET /pipelines/{hash}</c>).
/// </summary>
public sealed class PipelineSnapshot : ITenantScoped
{
    public Guid TenantId { get; set; }

    /// <summary>The 32 hex characters of <c>ResolvedPipeline.Hash</c>.</summary>
    public required string Hash { get; set; }

    /// <summary>The steps (<c>ResolvedPipeline.StepsJson</c>): id, kind, phase, params, enabled, source, fixed, runs.</summary>
    public required string StepsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>How one <c>http-lookup</c> call ended (docs/11 §10.2).</summary>
public enum LookupCallStatus
{
    /// <summary>The destination answered, and the response mapping gave the field assignments (the last time it ran).</summary>
    Succeeded,

    /// <summary>The call or its mapping (the last time it ran) failed; the step raised <c>LOOKUP_FAILED</c> (unless <c>onFailure: ignore</c>).</summary>
    Failed,
}

/// <summary>
/// The result of one <c>http-lookup</c> call for a document (docs/11 §10.2), keyed by the step and the hash of the
/// request it sent: a retry of the stage, or a reprocess that sends the same request, reuses the answer instead of
/// calling the destination again (idempotent and cached per document version). It keeps the destination's answer
/// (<see cref="ResponseJson"/>), which the step's current <c>response</c> expression maps on every run, so a changed
/// mapping applies to a reprocess without a second call (docs/11 §10.2, Correction (P5) fix 3); the assignments of the
/// last mapping are kept beside it.
/// </summary>
public sealed class LookupResult : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    /// <summary>The step id in the pipeline (<c>erpVendor</c>).</summary>
    public required string StepId { get; set; }

    /// <summary>SHA-256 (hex) of the method, the URL and the body sent.</summary>
    public required string RequestHash { get; set; }

    public Guid DestinationId { get; set; }

    public LookupCallStatus Status { get; set; }

    /// <summary>The http status of the last attempt, when one answered.</summary>
    public int? HttpStatus { get; set; }

    /// <summary>The field assignments (<c>{path: value}</c>) the response mapping gave the last time it ran; null for a failure.</summary>
    public string? AssignmentsJson { get; set; }

    /// <summary>
    /// The destination's answer as the response mapping reads it: the body's JSON text (at most 256 KB), or <c>null</c>
    /// for an empty or non-JSON body. Null when the destination gave no answer to map (a failed call), and on rows
    /// stored before the answers were kept, whose request is then sent once more.
    /// </summary>
    public string? ResponseJson { get; set; }

    /// <summary>Why the call failed (<c>timeout</c>, <c>status</c>, <c>blocked</c>, <c>mapping</c>, ...); null for a success.</summary>
    public string? Error { get; set; }

    public int Attempts { get; set; }

    public int DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One of a tenant's LLM budgets (docs/11 §16 "LLM cost", P5): the most the tenant spends on LLM calls in a UTC day, a
/// UTC month or on one document, for every stage (<see cref="Stage"/> null) or one. A call that would take the spend
/// past it is not made (<c>LLM_BUDGET_EXCEEDED</c>); spend is summed from <see cref="LlmUsageEntry"/>.
/// </summary>
public sealed class LlmBudgetRecord : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public LlmBudgetPeriod Period { get; set; }

    public LlmStage? Stage { get; set; }

    public decimal LimitUsd { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public LlmBudget ToDomain() => new(Period, LimitUsd, Stage);
}
