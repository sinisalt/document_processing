using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace DocProc.Definitions.Pipelines;

/// <summary>
/// Builds the pipeline of a document (docs/11 §10.1): the built-in steps of every phase (<see cref="PipelineDefaults"/>)
/// with the type's step list and parameters, then each layer of overrides in order (group, supplier, layout version).
/// Pure; the caller loads the layers.
/// <list type="bullet">
/// <item><description>An override for a step this pipeline does not have is skipped and noted: a supplier's override for
/// a price list's step does not break its invoices.</description></item>
/// <item><description>A fixed step (<c>classify.identification</c>, the review gate, the match and publish steps) is
/// never disabled or replaced, and only an extract-phase kind may be inserted, after an extract step, so nothing is
/// ever inserted ahead of <c>classify.identification</c>; such an override is refused (<see cref="PipelineProblem"/>), and so
/// is a stored pipeline (an imported bundle) that would break it.</description></item>
/// <item><description>Override parameters are checked against their kind's <c>ParamsSchema</c> after the merge; the
/// type's own step parameters are taken as the definition holds them (its publish checks own them).</description></item>
/// </list>
/// </summary>
public static partial class PipelineResolver
{
    /// <summary>The most steps a resolved pipeline holds.</summary>
    public const int MaxSteps = 64;

    /// <summary>The most <c>http-lookup</c> steps one pipeline runs.</summary>
    public const int MaxLookupSteps = 5;

    /// <summary>The most operations one layer of overrides holds.</summary>
    public const int MaxOverridesPerLayer = 50;

    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    /// <summary>
    /// The pipeline for <paramref name="definition"/> (null for a document whose type is not known yet, at Split and
    /// Classify, or Unknown) under <paramref name="layers"/>. With <paramref name="configurable"/> false (the tenant is
    /// not on <c>configurableSteps</c>) the layers and the type's step parameters and added steps are left out: the
    /// pipeline is exactly the steps the phases ran before P5.
    /// </summary>
    public static PipelineResolution Resolve(TypeDefinition? definition, IReadOnlyList<PipelineLayer> layers, bool configurable = true)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var steps = PipelineDefaults.Build(definition, configurable);
        var notes = new List<PipelineNote>();
        var problems = new List<PipelineProblem>();
        foreach (var layer in configurable ? layers : [])
        {
            Apply(steps, layer, notes, problems);
        }

        problems.AddRange(Invariants(steps));
        if (problems.Count > 0)
        {
            return new PipelineResolution(null, problems);
        }

        return new PipelineResolution(new ResolvedPipeline([.. steps.Select(s => s.Freeze())], notes), []);
    }

    /// <summary>The pipeline without any override: the type's own (or the built-in one without a type).</summary>
    public static ResolvedPipeline Baseline(TypeDefinition? definition, bool configurable = true) =>
        Resolve(definition, [], configurable).Pipeline!;

    /// <summary>
    /// The save-time check of one layer (a supplier's or a group's <c>settings.pipelineOverrides</c>, a layout version's
    /// <c>pipelineOverrides</c>), without the type it will meet: the operations, the step ids, the fixed steps by their
    /// ids, the inserted kinds and every parameter whose kind is known. <paramref name="at"/> prefixes the paths.
    /// </summary>
    public static IReadOnlyList<PipelineProblem> Check(IReadOnlyList<PipelineOverride>? overrides, string at)
    {
        var problems = new List<PipelineProblem>();
        if (overrides is null)
        {
            return problems;
        }

        if (overrides.Count > MaxOverridesPerLayer)
        {
            problems.Add(new(at, $"holds at most {MaxOverridesPerLayer} operations."));
            return problems;
        }

        var lookups = 0;
        for (var i = 0; i < overrides.Count; i++)
        {
            var o = overrides[i];
            var path = $"{at}[{i}]";
            if (!PipelineOverrideOps.All.Contains(o.Op, StringComparer.Ordinal))
            {
                problems.Add(new($"{path}.op", $"'{o.Op}' is not disable, param, insertAfter or replace."));
                continue;
            }

            if (!IsStepId(o.Step))
            {
                problems.Add(new($"{path}.step", "step is a step id (a letter, then letters, digits, '_', '.' or '-'; at most 64)."));
                continue;
            }

            var builtin = PipelineDefaults.BuiltinKindOfId(o.Step);
            var fixedKind = builtin is { } b && StepKindCatalogue.Find(b) is { Fixed: true } ? b : null;
            switch (o.Op)
            {
                case PipelineOverrideOps.Disable:
                    if (fixedKind is not null)
                    {
                        problems.Add(new($"{path}.step", FixedMessage(fixedKind, "disabled")));
                    }

                    if (o.Params is not null || o.With is not null)
                    {
                        problems.Add(new(path, "disable takes only step."));
                    }

                    break;
                case PipelineOverrideOps.Param:
                    if (o.Params is not { Count: > 0 })
                    {
                        problems.Add(new($"{path}.params", "param needs params (an object with at least one parameter)."));
                        break;
                    }

                    if (o.With is not null)
                    {
                        problems.Add(new(path, "param takes step and params, not with."));
                    }

                    if (builtin is { } known)
                    {
                        problems.AddRange(CheckParams(known, WithoutNulls(o.Params), $"{path}.params", partial: true));
                    }

                    break;
                case PipelineOverrideOps.InsertAfter:
                case PipelineOverrideOps.Replace:
                    if (o.Op == PipelineOverrideOps.Replace && fixedKind is not null)
                    {
                        problems.Add(new($"{path}.step", FixedMessage(fixedKind, "replaced")));
                    }

                    if (o.Params is not null)
                    {
                        problems.Add(new(path, $"{o.Op} takes step and with; the new step's parameters go in with.params."));
                    }

                    if (o.With is not { } with)
                    {
                        problems.Add(new($"{path}.with", $"{o.Op} needs with: the step {{id, kind, params}}."));
                        break;
                    }

                    var stepProblems = CheckNewStep(with, $"{path}.with", insert: o.Op == PipelineOverrideOps.InsertAfter).ToList();
                    problems.AddRange(stepProblems);
                    if (with.Kind == StepKindCatalogue.HttpLookup && ++lookups > MaxLookupSteps)
                    {
                        problems.Add(new($"{path}.with", $"a pipeline runs at most {MaxLookupSteps} http-lookup steps."));
                    }

                    if (stepProblems.Count == 0 && builtin is { } target && StepKindCatalogue.Find(target) is { } targetKind && StepKindCatalogue.Find(with.Kind) is { } newKind && targetKind.Phase != newKind.Phase)
                    {
                        problems.Add(new($"{path}.step", PhaseMessage(newKind.Phase, o.Step, targetKind.Phase)));
                    }

                    break;
            }
        }

        return problems;
    }

    /// <summary>The parameters of <paramref name="kind"/> against its schema (<paramref name="partial"/>: a <c>param</c> patch, whose required members may come from the step).</summary>
    public static IEnumerable<PipelineProblem> CheckParams(string kind, JsonObject? parameters, string at, bool partial)
    {
        if (StepKindCatalogue.Find(kind) is not { } descriptor)
        {
            yield return new PipelineProblem(at, $"'{kind}' is not a step kind.");
            yield break;
        }

        var schemaNode = descriptor.ParamsSchema.DeepClone().AsObject();
        if (partial)
        {
            schemaNode.Remove("required");
        }

        var schema = Schemas.GetOrAdd(
            $"{kind}|{partial}",
            _ => JsonSchema.Build(JsonSerializer.SerializeToElement(schemaNode), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));
        var result = schema.Evaluate(JsonSerializer.SerializeToElement(parameters ?? []), new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!result.IsValid)
        {
            var messages = (result.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{(d.InstanceLocation.ToString() is { Length: > 0 } l ? l + ": " : string.Empty)}{e.Value}"))
                .Distinct()
                .ToList();
            yield return new PipelineProblem(at, $"do not fit the step kind '{kind}': {(messages.Count > 0 ? string.Join("; ", messages) : "invalid")}.");
        }

        if (kind == StepKindCatalogue.HttpLookup && parameters?["destinationId"] is JsonValue id && id.TryGetValue<string>(out var text) && !Guid.TryParse(text, out _))
        {
            yield return new PipelineProblem($"{at}.destinationId", "destinationId is the id of an http destination.");
        }
    }

    /// <summary>Whether <paramref name="id"/> is a step id: a letter, then letters, digits, <c>_</c>, <c>.</c> or <c>-</c>, at most 64 characters.</summary>
    public static bool IsStepId(string? id) => id is { Length: > 0 and <= 64 } && StepIdPattern().IsMatch(id);

    private static IEnumerable<PipelineProblem> CheckNewStep(StepDefinition with, string at, bool insert)
    {
        if (!IsStepId(with.Id))
        {
            yield return new PipelineProblem($"{at}.id", "id is a step id (a letter, then letters, digits, '_', '.' or '-'; at most 64).");
        }
        else if (PipelineDefaults.BuiltinKindOfId(with.Id) is { } taken && insert)
        {
            yield return new PipelineProblem($"{at}.id", $"'{with.Id}' is the id of the built-in {taken} step; choose another.");
        }

        if (StepKindCatalogue.Find(with.Kind) is not { } kind)
        {
            yield return new PipelineProblem($"{at}.kind", $"'{with.Kind}' is not a step kind (GET /step-kinds).");
            yield break;
        }

        if (kind.Fixed)
        {
            yield return new PipelineProblem($"{at}.kind", $"{kind.Kind} is a fixed step; no override adds one.");
            yield break;
        }

        if (insert && !kind.Insertable)
        {
            yield return new PipelineProblem($"{at}.kind", $"{kind.Kind} cannot be inserted; insertAfter adds {string.Join(", ", StepKindCatalogue.Available.Values.Where(k => k.Insertable).Select(k => k.Kind))} steps.");
            yield break;
        }

        if (!kind.Runs)
        {
            yield return new PipelineProblem($"{at}.kind", $"{kind.Kind} does not run in this build; no override adds one.");
            yield break;
        }

        foreach (var problem in CheckParams(kind.Kind, with.Params, $"{at}.params", partial: false))
        {
            yield return problem;
        }
    }

    private static string PhaseMessage(string phase, string step, string stepPhase) =>
        $"a step of the {phase} phase goes in the {phase} phase; '{step}' is a step of the {stepPhase} phase.";

    private static string FixedMessage(string kind, string what) => kind == StepKindCatalogue.ClassifyIdentification
        ? $"classify.identification is the fixed first step of Classify (docs/11 §10.1): it cannot be {what}, and nothing goes ahead of it."
        : $"{kind} is a fixed step: it cannot be {what}.";

    private static void Apply(List<PipelineDefaults.Step> steps, PipelineLayer layer, List<PipelineNote> notes, List<PipelineProblem> problems)
    {
        var at = layer.Reference is { } reference ? $"{layer.Source}:{reference}.pipelineOverrides" : $"{layer.Source}.pipelineOverrides";
        if (layer.Overrides.Count > MaxOverridesPerLayer)
        {
            problems.Add(new(at, $"holds at most {MaxOverridesPerLayer} operations."));
            return;
        }

        for (var i = 0; i < layer.Overrides.Count; i++)
        {
            var o = layer.Overrides[i];
            var path = $"{at}[{i}]";
            var index = steps.FindIndex(s => string.Equals(s.Id, o.Step, StringComparison.Ordinal));
            if (index < 0)
            {
                notes.Add(new PipelineNote(layer.Source, layer.Reference, i, $"{o.Op} of step '{o.Step}' skipped: this pipeline has no such step."));
                continue;
            }

            var target = steps[index];
            switch (o.Op)
            {
                case PipelineOverrideOps.Disable:
                    if (target.Fixed)
                    {
                        problems.Add(new($"{path}.step", FixedMessage(target.Kind, "disabled")));
                        continue;
                    }

                    target.Enabled = false;
                    target.Source = layer.Source;
                    break;

                case PipelineOverrideOps.Param:
                    if (o.Params is not { Count: > 0 })
                    {
                        problems.Add(new($"{path}.params", "param needs params."));
                        continue;
                    }

                    var merged = target.Params.DeepClone().AsObject();
                    foreach (var (name, value) in o.Params)
                    {
                        if (value is null)
                        {
                            merged.Remove(name);
                        }
                        else
                        {
                            merged[name] = value.DeepClone();
                        }
                    }

                    var paramProblems = CheckParams(target.Kind, merged, $"{path}.params", partial: false).ToList();
                    if (paramProblems.Count > 0)
                    {
                        problems.AddRange(paramProblems);
                        continue;
                    }

                    target.Params = merged;
                    target.Source = layer.Source;
                    break;

                case PipelineOverrideOps.InsertAfter:
                case PipelineOverrideOps.Replace:
                    var insert = o.Op == PipelineOverrideOps.InsertAfter;
                    if (!insert && target.Fixed)
                    {
                        problems.Add(new($"{path}.step", FixedMessage(target.Kind, "replaced")));
                        continue;
                    }

                    if (o.With is not { } with)
                    {
                        problems.Add(new($"{path}.with", $"{o.Op} needs with."));
                        continue;
                    }

                    var stepProblems = CheckNewStep(with, $"{path}.with", insert).ToList();
                    if (stepProblems.Count == 0 && StepKindCatalogue.Find(with.Kind) is { } kind && kind.Phase != target.Phase)
                    {
                        stepProblems.Add(new($"{path}.step", PhaseMessage(kind.Phase, target.Id, target.Phase)));
                    }

                    if (steps.Any(s => string.Equals(s.Id, with.Id, StringComparison.Ordinal) && (insert || !ReferenceEquals(s, target))))
                    {
                        stepProblems.Add(new($"{path}.with.id", $"the pipeline already has a step '{with.Id}'."));
                    }

                    if (stepProblems.Count > 0)
                    {
                        problems.AddRange(stepProblems);
                        continue;
                    }

                    var descriptor = StepKindCatalogue.Find(with.Kind)!;
                    var added = new PipelineDefaults.Step(with.Id, descriptor.Kind, descriptor.Phase, with.Params?.DeepClone().AsObject() ?? [], layer.Source, descriptor.Fixed, descriptor.Runs);
                    if (insert)
                    {
                        steps.Insert(index + 1, added);
                    }
                    else
                    {
                        steps[index] = added;
                    }

                    break;
            }
        }
    }

    /// <summary>What every pipeline holds, whatever its layers did (also the check of a stored one).</summary>
    private static IEnumerable<PipelineProblem> Invariants(List<PipelineDefaults.Step> steps)
    {
        var classify = steps.Where(s => s.Phase == PipelinePhases.Classify).ToList();
        if (classify.Count == 0 || classify[0].Kind != StepKindCatalogue.ClassifyIdentification || !classify[0].Enabled)
        {
            yield return new PipelineProblem("classify", "classify.identification must be the first, enabled step of Classify (docs/11 §10.1).");
        }

        var validate = steps.Where(s => s.Phase == PipelinePhases.Validate).ToList();
        if (validate.Count == 0 || validate[^1].Kind != StepKindCatalogue.ReviewGate || !validate[^1].Enabled)
        {
            yield return new PipelineProblem("validate", "review-gate must be the last, enabled step of Validate.");
        }

        foreach (var fixedStep in steps.Where(s => s.Fixed && !s.Enabled))
        {
            yield return new PipelineProblem(fixedStep.Id, FixedMessage(fixedStep.Kind, "disabled"));
        }

        if (steps.Count > MaxSteps)
        {
            yield return new PipelineProblem("steps", $"a pipeline holds at most {MaxSteps} steps.");
        }

        if (steps.Count(s => s.Kind == StepKindCatalogue.HttpLookup) > MaxLookupSteps)
        {
            yield return new PipelineProblem("extract", $"a pipeline runs at most {MaxLookupSteps} http-lookup steps.");
        }

        var duplicate = steps.GroupBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            yield return new PipelineProblem(duplicate.Key, $"the step id '{duplicate.Key}' is used twice.");
        }

        // Phases stay in their order: a step of one phase never lands among another's.
        var order = steps.Select(s => PipelinePhases.All.ToList().IndexOf(s.Phase)).ToList();
        for (var i = 1; i < order.Count; i++)
        {
            if (order[i] < order[i - 1])
            {
                yield return new PipelineProblem(steps[i].Id, "a step is out of its phase's place.");
                yield break;
            }
        }
    }

    private static JsonObject WithoutNulls(JsonObject parameters)
    {
        var copy = new JsonObject();
        foreach (var (name, value) in parameters)
        {
            if (value is not null)
            {
                copy[name] = value.DeepClone();
            }
        }

        return copy;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex StepIdPattern();
}
