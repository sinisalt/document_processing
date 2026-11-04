using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocProc.Definitions.Compilation;

namespace DocProc.Definitions.Pipelines;

/// <summary>The operations of a <c>pipelineOverrides</c> patch (docs/11 §10.1).</summary>
public static class PipelineOverrideOps
{
    /// <summary><c>{op:"disable", step:"llm"}</c>: the step does not run.</summary>
    public const string Disable = "disable";

    /// <summary><c>{op:"param", step:"llm", params:{effort:"low"}}</c>: the params are merged over the step's.</summary>
    public const string Param = "param";

    /// <summary><c>{op:"insertAfter", step:"map", with:{id, kind, params}}</c>: a new step after the named one.</summary>
    public const string InsertAfter = "insertAfter";

    /// <summary><c>{op:"replace", step:"rules", with:{id, kind, params}}</c>: another step in the named one's place.</summary>
    public const string Replace = "replace";

    public static readonly IReadOnlyList<string> All = [Disable, Param, InsertAfter, Replace];
}

/// <summary>
/// One operation of a <c>pipelineOverrides</c> patch, as a supplier's or a group's settings and a layout version's
/// mapping store it (docs/11 §10.1, §8.1).
/// </summary>
public sealed record PipelineOverride
{
    public required string Op { get; init; }

    public required string Step { get; init; }

    public JsonObject? Params { get; init; }

    public StepDefinition? With { get; init; }
}

/// <summary>Where a layer of overrides comes from: the step it changed is attributed to it in the resolved pipeline.</summary>
public static class PipelineSources
{
    public const string Default = "default";
    public const string Type = "type";
    public const string Group = "group";
    public const string Supplier = "supplier";
    public const string Layout = "layout";
}

/// <summary>One layer of overrides (the group's, the supplier's, the layout version's), applied in order.</summary>
/// <param name="Source">One of <see cref="PipelineSources"/>.</param>
/// <param name="Reference">What the layer belongs to: the group or supplier id, the layout key and version.</param>
/// <param name="Overrides">The ordered patch.</param>
public sealed record PipelineLayer(string Source, string? Reference, IReadOnlyList<PipelineOverride> Overrides);

/// <summary>One step of a resolved pipeline.</summary>
/// <param name="Id">The step id, unique in the pipeline.</param>
/// <param name="Kind">Its kind (<see cref="StepKindCatalogue"/>).</param>
/// <param name="Phase">Its phase.</param>
/// <param name="Params">Its parameters after every layer (an empty object for none).</param>
/// <param name="Enabled">False when an override disabled it: it is listed, and not run.</param>
/// <param name="Source">The layer that last changed it (<see cref="PipelineSources"/>).</param>
/// <param name="Fixed">Whether overrides may not move, disable or replace it.</param>
/// <param name="Runs">Whether this build runs its kind.</param>
public sealed record ResolvedStep(string Id, string Kind, string Phase, JsonObject Params, bool Enabled, string Source, bool Fixed, bool Runs)
{
    /// <summary>A string parameter, or null.</summary>
    public string? Text(string name) => Params[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A number parameter, or null.</summary>
    public double? Number(string name) => Params[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}

/// <summary>An override a layer named for a step this pipeline does not have (a supplier's override for another type's step); it is skipped.</summary>
public sealed record PipelineNote(string Source, string? Reference, int Index, string Message);

/// <summary>A refusal: an override or a stored pipeline that breaks a rule of docs/11 §10.1.</summary>
public sealed record PipelineProblem(string Path, string Message);

/// <summary>
/// The pipeline one document runs (docs/11 §10.1): the steps of every phase in order with their parameters, from the
/// type's definition and the group, supplier and layout version overrides. <see cref="Hash"/> identifies it: two
/// documents with the same hash ran the same steps with the same parameters.
/// </summary>
public sealed record ResolvedPipeline
{
    [JsonConstructor]
    public ResolvedPipeline(IReadOnlyList<ResolvedStep> steps, IReadOnlyList<PipelineNote> notes)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(notes);
        Steps = steps;
        Notes = notes;
        Hash = HashOf(steps);
    }

    public IReadOnlyList<ResolvedStep> Steps { get; }

    public IReadOnlyList<PipelineNote> Notes { get; }

    /// <summary>The first 32 hex characters of the SHA-256 of the canonical JSON of the steps.</summary>
    public string Hash { get; }

    /// <summary>Whether any layer changed a step (the pipeline is not the type's own).</summary>
    public bool Overridden => Steps.Any(s => s.Source is PipelineSources.Group or PipelineSources.Supplier or PipelineSources.Layout);

    public IEnumerable<ResolvedStep> Phase(string phase) => Steps.Where(s => s.Phase == phase);

    public ResolvedStep? Find(string id) => Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>The first enabled step of <paramref name="kind"/>, or null when there is none (it was disabled or replaced).</summary>
    public ResolvedStep? Enabled(string kind) => Steps.FirstOrDefault(s => s.Enabled && s.Kind == kind);

    /// <summary>Whether an enabled step of <paramref name="kind"/> is in the pipeline.</summary>
    public bool Runs(string kind) => Enabled(kind) is not null;

    /// <summary>The steps as JSON, the shape the snapshot stores and <c>GET /pipelines/{hash}</c> answers.</summary>
    public JsonArray StepsJson() => new([.. Steps.Select(StepJson)]);

    private static JsonObject StepJson(ResolvedStep step) => new()
    {
        ["id"] = step.Id,
        ["kind"] = step.Kind,
        ["phase"] = step.Phase,
        ["params"] = step.Params.DeepClone(),
        ["enabled"] = step.Enabled,
        ["source"] = step.Source,
        ["fixed"] = step.Fixed,
        ["runs"] = step.Runs,
    };

    private static string HashOf(IReadOnlyList<ResolvedStep> steps)
    {
        // The source is not part of the identity: a group and a supplier that set the same parameter run the same steps.
        var identity = new JsonArray([.. steps.Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id,
            ["kind"] = s.Kind,
            ["phase"] = s.Phase,
            ["params"] = s.Params.DeepClone(),
            ["enabled"] = s.Enabled,
        })]);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(identity)));
        return Convert.ToHexStringLower(bytes)[..32];
    }
}

/// <summary>A resolution: the pipeline, or the problems that refused it (then <see cref="Pipeline"/> is null).</summary>
public sealed record PipelineResolution(ResolvedPipeline? Pipeline, IReadOnlyList<PipelineProblem> Problems)
{
    public bool Succeeded => Pipeline is not null;
}
