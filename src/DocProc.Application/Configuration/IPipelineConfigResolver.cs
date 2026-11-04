using DocProc.Definitions.Pipelines;

namespace DocProc.Application.Configuration;

/// <summary>
/// What a document's pipeline is resolved for (docs/11 §10.1): the tenant, its type when known (null at Split and
/// Classify, and for Unknown), the supplier whose and whose group's <c>pipelineOverrides</c> apply, and the layout
/// version that identified it.
/// </summary>
public sealed record PipelineRequest(Guid TenantId, EffectiveType? Type, Guid? SupplierId = null, Guid? LayoutVersionId = null);

/// <summary>
/// A resolved pipeline for one document. With <see cref="Problems"/> the layers were refused (a fixed step, invalid
/// parameters: an imported bundle, or settings written outside the API) and <see cref="Pipeline"/> is the type's own,
/// and the stage raises <c>PIPELINE_INVALID</c>. <see cref="Configurable"/> is the tenant's <c>configurableSteps</c>:
/// off, the pipeline is the built-in one and stages record no hash.
/// </summary>
public sealed record PipelineConfig(ResolvedPipeline Pipeline, IReadOnlyList<PipelineProblem> Problems, bool Configurable)
{
    public bool Refused => Problems.Count > 0;

    /// <summary>The built-in pipeline of <paramref name="type"/> for a tenant without <c>configurableSteps</c>.</summary>
    public static PipelineConfig Builtin(EffectiveType? type) => new(PipelineResolver.Baseline(type?.Definition, configurable: false), [], false);
}

/// <summary>
/// Resolves the pipeline of a document (docs/11 §10.1): the built-in steps, the type's step list and parameters, the
/// group's, the supplier's and the layout version's overrides, each stage asking for its phase. Every resolved
/// pipeline is kept by its hash (<c>PipelineSnapshots</c>), which documents record.
/// </summary>
public interface IPipelineConfigResolver
{
    Task<PipelineConfig> ResolveAsync(PipelineRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The resolver of a host without a database: every tenant runs the built-in steps.</summary>
public sealed class BuiltinPipelineConfigResolver : IPipelineConfigResolver
{
    public static BuiltinPipelineConfigResolver Instance { get; } = new();

    public Task<PipelineConfig> ResolveAsync(PipelineRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(PipelineConfig.Builtin(request.Type));
    }
}
