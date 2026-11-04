using System.Text.Json.Nodes;

namespace DocProc.Definitions.Pipelines;

/// <summary>
/// The steps each phase runs before any override (docs/11 §10.1), which are the steps the phases ran before P5:
/// <list type="bullet">
/// <item><description>Split: <c>split.heuristic</c> (mode <c>auto</c>), <c>split.llm</c>.</description></item>
/// <item><description>Classify: <c>classify.identification</c> (fixed, first), <c>classify.keyword</c>, <c>classify.llm</c>.</description></item>
/// <item><description>Match: <c>match.source</c>, <c>match.vatIban</c>, <c>match.name</c>, <c>match.layout</c> (all fixed).</description></item>
/// <item><description>Extract: the built-in chain <c>xml</c> (xml-einvoice), <c>map</c> (supplier-mapping), <c>prefill</c>
/// (layout-prefill), <c>llm</c>; the type's <c>processing.extract</c> names their ids and parameters, and the steps it
/// adds (<c>defaults</c>, <c>code-list</c>, <c>transform</c>, <c>http-lookup</c>) follow the step listed before them.
/// The definition does not remove a built-in step by leaving it out: the invoice templates list no
/// <c>supplier-mapping</c>, and layouts fill invoices since P4.</description></item>
/// <item><description>Validate: the type's <c>processing.validate</c>, or the legacy default <c>rules</c> and
/// <c>duplicate</c>; then <c>review-gate</c> (fixed, last).</description></item>
/// <item><description>Publish: <c>publish.outbox</c>, <c>publish.fanout</c> (fixed).</description></item>
/// </list>
/// Without <c>configurableSteps</c> the type's step parameters and added steps are left out, so nothing runs
/// differently for a tenant that is not on the feature.
/// </summary>
public static class PipelineDefaults
{
    private static readonly string[] ExtractChain = [StepKindCatalogue.XmlEInvoice, StepKindCatalogue.SupplierMapping, StepKindCatalogue.LayoutPrefill, StepKindCatalogue.Llm];

    private static readonly Dictionary<string, string> BuiltinIds = StepKindCatalogue.Available.Values
        .Where(k => k.Kind is not (StepKindCatalogue.Defaults or StepKindCatalogue.CodeList or StepKindCatalogue.Transform or StepKindCatalogue.HttpLookup))
        .ToDictionary(k => k.DefaultId, k => k.Kind, StringComparer.Ordinal);

    /// <summary>The kind of the built-in step with the default id <paramref name="id"/> (<c>llm</c>, <c>classify.identification</c>), or null.</summary>
    public static string? BuiltinKindOfId(string id) => BuiltinIds.GetValueOrDefault(id);

    internal sealed class Step(string id, string kind, string phase, JsonObject parameters, string source, bool isFixed, bool runs)
    {
        public string Id { get; set; } = id;

        public string Kind { get; } = kind;

        public string Phase { get; } = phase;

        public JsonObject Params { get; set; } = parameters;

        public bool Enabled { get; set; } = true;

        public string Source { get; set; } = source;

        public bool Fixed { get; } = isFixed;

        public bool Runs { get; } = runs;

        public ResolvedStep Freeze() => new(Id, Kind, Phase, Params.DeepClone().AsObject(), Enabled, Source, Fixed, Runs);
    }

    internal static List<Step> Build(TypeDefinition? definition, bool configurable)
    {
        var steps = new List<Step>
        {
            Builtin(StepKindCatalogue.SplitHeuristic, new JsonObject { ["mode"] = "auto" }),
            Builtin(StepKindCatalogue.SplitLlm),
            Builtin(StepKindCatalogue.ClassifyIdentification),
            Builtin(StepKindCatalogue.ClassifyKeyword),
            Builtin(StepKindCatalogue.ClassifyLlm),
            Builtin(StepKindCatalogue.MatchSource),
            Builtin(StepKindCatalogue.MatchVatIban),
            Builtin(StepKindCatalogue.MatchName),
            Builtin(StepKindCatalogue.MatchLayout),
        };

        steps.AddRange(Extract(definition, configurable));

        var validate = definition?.Processing.Validate is { Count: > 0 } listed
            ? listed.Where(s => StepKindCatalogue.Find(s.Kind) is { Phase: PipelinePhases.Validate, Fixed: false })
                .Select(s => Of(s.Id, s.Kind, configurable ? s.Params : null, PipelineSources.Type))
            : [Builtin(StepKindCatalogue.ValidateRules), Builtin(StepKindCatalogue.ValidateDuplicate)];
        steps.AddRange(Unique(validate));
        steps.Add(Builtin(StepKindCatalogue.ReviewGate));
        steps.Add(Builtin(StepKindCatalogue.PublishOutbox));
        steps.Add(Builtin(StepKindCatalogue.PublishFanout));

        // A definition may use one id in two phases (an extract step and a validate step both called "rules"): the later
        // one is named after its phase, so every id addresses one step.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (seen.Add(step.Id))
            {
                continue;
            }

            var renamed = $"{step.Phase}.{step.Id}";
            for (var n = 2; !seen.Add(renamed); n++)
            {
                renamed = $"{step.Phase}.{step.Id}.{n}";
            }

            step.Id = renamed;
        }

        return steps;
    }

    private static List<Step> Extract(TypeDefinition? definition, bool configurable)
    {
        var steps = ExtractChain.Select(k => Builtin(k)).ToList();
        if (definition is null)
        {
            return steps;
        }

        var claimed = new HashSet<Step>();
        var last = -1;
        foreach (var listed in definition.Processing.Extract)
        {
            if (StepKindCatalogue.Find(listed.Kind) is not { Phase: PipelinePhases.Extract } kind)
            {
                continue; // DefinitionChecker refuses it at publish.
            }

            var builtin = steps.FirstOrDefault(s => s.Kind == listed.Kind && ExtractChain.Contains(s.Kind) && !claimed.Contains(s));
            if (builtin is not null)
            {
                claimed.Add(builtin);
                if (!steps.Any(s => !ReferenceEquals(s, builtin) && string.Equals(s.Id, listed.Id, StringComparison.Ordinal)))
                {
                    builtin.Id = listed.Id;
                }

                if (configurable && listed.Params is { Count: > 0 } parameters)
                {
                    builtin.Params = parameters.DeepClone().AsObject();
                    builtin.Source = PipelineSources.Type;
                }

                last = steps.IndexOf(builtin);
                continue;
            }

            if (!configurable || steps.Any(s => string.Equals(s.Id, listed.Id, StringComparison.Ordinal)))
            {
                continue; // Steps a definition adds run only with configurableSteps, as before P5.
            }

            var added = Of(listed.Id, kind.Kind, listed.Params, PipelineSources.Type);
            var at = last < 0 ? steps.Count : last + 1;
            steps.Insert(at, added);
            last = at;
        }

        return steps;
    }

    private static IEnumerable<Step> Unique(IEnumerable<Step> steps)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return steps.Where(s => ids.Add(s.Id));
    }

    private static Step Builtin(string kind, JsonObject? parameters = null)
    {
        var descriptor = StepKindCatalogue.Find(kind)!;
        return new Step(descriptor.DefaultId, kind, descriptor.Phase, parameters ?? [], PipelineSources.Default, descriptor.Fixed, descriptor.Runs);
    }

    private static Step Of(string id, string kind, JsonObject? parameters, string source)
    {
        var descriptor = StepKindCatalogue.Find(kind)!;
        return new Step(id, kind, descriptor.Phase, parameters?.DeepClone().AsObject() ?? [], source, descriptor.Fixed, descriptor.Runs);
    }
}
