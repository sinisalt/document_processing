using DocProc.Application.Configuration;
using DocProc.Definitions.Pipelines;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm.Gateway;

namespace DocProc.Pipeline.Support;

/// <summary>
/// How a stage handler gets the steps of its phase (docs/11 §10.1, §10.3): it resolves the document's pipeline, records
/// its hash on the document (<c>PipelineHash</c>, with <c>configurableSteps</c> only), raises <c>PIPELINE_INVALID</c>
/// when the overrides were refused (the type's own steps run then), and writes one <c>document.pipeline_applied</c>
/// event when the pipeline is not the type's own. Without <c>configurableSteps</c> the pipeline is the built-in one, so
/// every check a stage makes against it answers as the stage behaved before P5.
/// </summary>
internal sealed class StagePipeline(IPipelineConfigResolver resolver, DocumentFlagWriter flags, DocumentTimeline timeline)
{
    /// <summary>The timeline event of a pipeline that is not the type's own (overridden, or refused).</summary>
    public const string AppliedEvent = DocProc.Application.Abstractions.DocumentEventTypes.PipelineApplied;

    public async Task<ResolvedPipeline> ResolveAsync(Document document, EffectiveType? type, string phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        // Split and Classify run before Match: a supplier's (and its layout's) overrides apply there only when the source
        // named the supplier (docs/11 §10.1, Correction (P5 A)). A supplier Match found by VAT or IBAN in an earlier run
        // (still on the document after :reprocess from Normalize or Classify) is not known yet at these phases.
        var beforeMatch = phase is PipelinePhases.Split or PipelinePhases.Classify;
        var known = !beforeMatch || DocProc.Pipeline.Stages.ClassifyHandler.KnownFromSource(document);
        var config = await resolver.ResolveAsync(new PipelineRequest(document.TenantId, type, known ? document.SupplierId : null, known ? document.SupplierLayoutVersionId : null), cancellationToken).ConfigureAwait(false);
        if (!config.Configurable)
        {
            return config.Pipeline;
        }

        document.PipelineHash = config.Pipeline.Hash;
        flags.ReplaceForStep(document, $"pipeline.{phase}", [FlagCodes.PipelineInvalid], config.Refused
            ? [FlagCodes.Create(
                FlagCodes.PipelineInvalid,
                $"The pipeline overrides were refused ({config.Problems[0].Path}: {config.Problems[0].Message}); the type's own steps ran.",
                parameters: FlagParams.Of(("phase", phase), ("path", config.Problems[0].Path), ("problem", config.Problems[0].Message)))]
            : []);
        var pipeline = config.Pipeline;
        if (config.Refused || pipeline.Phase(phase).Any(s => s.Source is PipelineSources.Group or PipelineSources.Supplier or PipelineSources.Layout) || pipeline.Notes.Count > 0)
        {
            timeline.Record(document, AppliedEvent, null, null, new
            {
                phase,
                hash = pipeline.Hash,
                refused = config.Refused ? config.Problems.Select(p => new { p.Path, p.Message }).ToList() : null,
                steps = pipeline.Phase(phase).Select(s => new { s.Id, s.Kind, s.Enabled, s.Source }).ToList(),
                skipped = pipeline.Notes.Select(n => n.Message).ToList(),
            });
        }

        return pipeline;
    }

    /// <summary>The flag of an LLM step the tenant's budget kept from being called (docs/11 §16), owned by the step that raised it.</summary>
    public static DocumentFlag BudgetFlag(LlmBudgetExceededException exception, string stepId)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var verdict = exception.Verdict;
        return FlagCodes.Create(
            FlagCodes.LlmBudgetExceeded,
            $"{stepId} was not called: {verdict.Describe()}",
            parameters: FlagParams.Of(
                ("step", stepId),
                ("period", verdict.Budget?.Period.ToString().ToLowerInvariant() ?? string.Empty),
                ("limitUsd", FlagParams.Number(verdict.Budget?.LimitUsd ?? 0m)),
                ("spentUsd", FlagParams.Number(Math.Round(verdict.SpentUsd, 4))),
                // A model without a price: the budget could not count the call (docs/11 §16, Correction (P5) fix 4).
                (FlagParams.Reason, verdict.UnpricedModel is null ? null : "unpriced"),
                ("model", verdict.UnpricedModel)));
    }

    /// <summary>Sets (or clears) the budget flag a step owns.</summary>
    public void RecordBudget(Document document, string stepId, LlmBudgetExceededException? exception) =>
        flags.ReplaceForStep(document, stepId, [FlagCodes.LlmBudgetExceeded], exception is null ? [] : [BudgetFlag(exception, stepId)]);
}
