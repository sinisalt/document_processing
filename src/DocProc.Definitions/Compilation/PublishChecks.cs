using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace DocProc.Definitions.Compilation;

/// <summary>Something outside the definition that refers to one of its fields (docs/11 §5.7 "dependents").</summary>
/// <param name="Kind">What refers: <c>indexedKey</c>, <c>supplierLayout</c>, <c>outputMapping</c>, <c>binding</c>, ...</param>
/// <param name="Name">A readable name of the referring thing (<c>nordwind-invoice@4</c>).</param>
/// <param name="FieldId">The stable id of the field it refers to, when known.</param>
/// <param name="Path">The path it refers to.</param>
public sealed record DependentReference(string Kind, string Name, string? FieldId, string Path);

/// <summary>How a field changed between the published version and the candidate, by its stable id.</summary>
public enum FieldChangeKind
{
    Added,
    Removed,
    KindChanged,
    Renamed,
}

public sealed record FieldChange(FieldChangeKind Change, string FieldId, string? OldPath, string? NewPath, string? OldKind, string? NewKind);

/// <summary>What the publish checks need besides the candidate.</summary>
public sealed record PublishCheckInput
{
    /// <summary>The candidate exactly as stored (the draft JSON), checked against the meta-schema first.</summary>
    public required string DefinitionJson { get; init; }

    /// <summary>The version published now, if any: field changes are computed against it.</summary>
    public TypeDefinition? Published { get; init; }

    /// <summary>Everything outside the definition that refers to the published version's fields.</summary>
    public IReadOnlyList<DependentReference> Dependents { get; init; } = [];

    public CompileOptions CompileOptions { get; init; } = CompileOptions.Default;

    /// <summary>The LLM project's portability check over a compiled LLM schema (<c>SchemaPortability.Check</c>); null skips it.</summary>
    public Func<JsonObject, IReadOnlyList<string>>? PortabilityCheck { get; init; }
}

/// <summary>The publish report (docs/11 §5.7, §19 "Versions"): what blocks, what to know, what changed, and the artifacts.</summary>
public sealed record PublishReport(
    TypeDefinition? Definition,
    CompiledDefinition? Compiled,
    IReadOnlyList<DefinitionProblem> Problems,
    IReadOnlyList<DefinitionProblem> Warnings,
    IReadOnlyList<FieldChange> Changes,
    IReadOnlyList<DependentReference> BlockingDependents)
{
    public bool CanPublish => Problems.Count == 0 && Compiled is not null;
}

/// <summary>
/// The publish-time checks of docs/11 §5.7, in order: the meta-schema, the strict reader, the structural checks of
/// <see cref="DefinitionChecker"/> (key syntax, roles, registered rule kinds, steps, patterns compiled with
/// NonBacktracking), each rule's parameters against its kind's <c>ParamsSchema</c> and the paths they name, the
/// <c>duplicateKey</c> components being indexed, the compiler's size caps, the portability of the compiled LLM
/// schema, and the dependents of every field that was removed or changed kind. The golden-document regression suite
/// (§15.3) joins with the governance phase.
/// </summary>
public static class PublishChecks
{
    private static readonly ConcurrentDictionary<string, JsonSchema> ParamsSchemas = new(StringComparer.Ordinal);

    public static PublishReport Run(PublishCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var problems = new List<DefinitionProblem>(DefinitionMetaSchema.Validate(input.DefinitionJson));
        if (problems.Count > 0)
        {
            return new PublishReport(null, null, problems, [], [], []);
        }

        TypeDefinition definition;
        try
        {
            definition = DefinitionSerializer.Parse(input.DefinitionJson);
        }
        catch (DefinitionFormatException e)
        {
            return new PublishReport(null, null, [new DefinitionProblem("(root)", e.Message)], [], [], []);
        }

        problems.AddRange(DefinitionChecker.Check(definition, RuleKinds.Keys));
        problems.AddRange(CheckRuleParams(definition));
        problems.AddRange(CheckDuplicateKey(definition));

        var warnings = new List<DefinitionProblem>();
        CompiledDefinition? compiled = null;
        if (problems.Count == 0)
        {
            var result = DefinitionCompiler.Compile(definition, input.CompileOptions);
            problems.AddRange(result.Problems);
            warnings.AddRange(result.Notes);
            compiled = result.Compiled;
            if (compiled is not null && input.PortabilityCheck is { } portability)
            {
                problems.AddRange(portability(compiled.LlmSchema.Schema).Select(v => new DefinitionProblem("llm schema", v)));
            }
        }

        var changes = input.Published is { } published ? Compare(published, definition) : [];
        var blocking = new List<DependentReference>();
        foreach (var change in changes.Where(c => c.Change is FieldChangeKind.Removed or FieldChangeKind.KindChanged))
        {
            var dependents = input.Dependents.Where(d => (d.FieldId is not null && d.FieldId == change.FieldId) || (d.FieldId is null && d.Path == change.OldPath)).ToList();
            blocking.AddRange(dependents);
            foreach (var dependent in dependents)
            {
                problems.Add(new DefinitionProblem(
                    change.OldPath ?? change.FieldId,
                    change.Change == FieldChangeKind.Removed
                        ? $"is removed, but {dependent.Kind} '{dependent.Name}' refers to it."
                        : $"changes kind from {change.OldKind} to {change.NewKind}, but {dependent.Kind} '{dependent.Name}' refers to it."));
            }
        }

        foreach (var change in changes.Where(c => c.Change == FieldChangeKind.Renamed))
        {
            var dependents = input.Dependents.Count(d => d.FieldId == change.FieldId || d.Path == change.OldPath);
            if (dependents > 0)
            {
                warnings.Add(new DefinitionProblem(change.OldPath!, $"is renamed to {change.NewPath}; {dependents} dependent(s) still name the old path and need new draft versions."));
            }
        }

        return new PublishReport(definition, problems.Count == 0 ? compiled : null, problems, warnings, changes, blocking);
    }

    /// <summary>The field changes from <paramref name="published"/> to <paramref name="candidate"/>, matched by stable field id.</summary>
    public static IReadOnlyList<FieldChange> Compare(TypeDefinition published, TypeDefinition candidate)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(candidate);
        var before = DefinitionPaths.Enumerate(published).GroupBy(f => f.Field.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var after = DefinitionPaths.Enumerate(candidate).GroupBy(f => f.Field.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var changes = new List<FieldChange>();
        foreach (var (id, old) in before)
        {
            if (!after.TryGetValue(id, out var now))
            {
                changes.Add(new FieldChange(FieldChangeKind.Removed, id, old.Path, null, old.Field.Kind, null));
                continue;
            }

            if (old.Field.Kind != now.Field.Kind || old.Field.List != now.Field.List)
            {
                changes.Add(new FieldChange(FieldChangeKind.KindChanged, id, old.Path, now.Path, Describe(old.Field), Describe(now.Field)));
            }
            else if (old.Path != now.Path)
            {
                changes.Add(new FieldChange(FieldChangeKind.Renamed, id, old.Path, now.Path, old.Field.Kind, now.Field.Kind));
            }
        }

        foreach (var (id, now) in after.Where(a => !before.ContainsKey(a.Key)))
        {
            changes.Add(new FieldChange(FieldChangeKind.Added, id, null, now.Path, null, now.Field.Kind));
        }

        return changes;

        static string Describe(FieldDefinition field) => field.List ? $"list of {field.Kind}" : field.Kind;
    }

    /// <summary>The fields of <paramref name="definition"/> that other data refers to by themselves: its indexed fields (index rows of documents).</summary>
    public static IEnumerable<DependentReference> IndexedKeys(TypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return DefinitionPaths.Enumerate(definition)
            .Where(f => f.Field.Indexed)
            .Select(f => new DependentReference("indexedKey", $"{definition.Key} index '{f.Path}'", f.Field.Id, f.Path));
    }

    private static IEnumerable<DefinitionProblem> CheckRuleParams(TypeDefinition definition)
    {
        for (var i = 0; i < definition.Rules.Count; i++)
        {
            var rule = definition.Rules[i];
            if (!RuleKinds.Available.TryGetValue(rule.Kind, out var kind))
            {
                continue; // DefinitionChecker reports the unknown kind.
            }

            var at = $"rules[{i}] ({rule.Id}).params";
            var parameters = rule.Params ?? [];
            var schema = ParamsSchemas.GetOrAdd(kind.Kind, _ => JsonSchema.Build(JsonSerializer.SerializeToElement(kind.ParamsSchema), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));
            var result = schema.Evaluate(JsonSerializer.SerializeToElement(parameters), new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!result.IsValid)
            {
                var messages = (result.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{(d.InstanceLocation.ToString() is { Length: > 0 } l ? l + ": " : string.Empty)}{e.Value}")).Distinct().ToList();
                yield return new DefinitionProblem(at, $"do not fit the kind '{kind.Kind}': {(messages.Count > 0 ? string.Join("; ", messages) : "invalid")}.");
                continue;
            }

            foreach (var name in kind.PathParams)
            {
                foreach (var path in PathsOf(parameters[name]))
                {
                    if (DefinitionPaths.FindValue(definition, path) is null)
                    {
                        yield return new DefinitionProblem(at, $"{name} '{path}' is not a field of the definition.");
                    }
                }
            }
        }

        static IEnumerable<string> PathsOf(JsonNode? node) => node switch
        {
            JsonValue value when value.TryGetValue<string>(out var path) => [path],
            JsonArray array => array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var p) ? p : null).OfType<string>(),
            _ => [],
        };
    }

    private static IEnumerable<DefinitionProblem> CheckDuplicateKey(TypeDefinition definition)
    {
        // The duplicate check reads every component other than the document number from DocumentFieldIndex (§11).
        foreach (var component in definition.Roles.DuplicateKey ?? [])
        {
            if (component == "$supplierId" || component == definition.Roles.DocumentNumber)
            {
                continue;
            }

            if (DefinitionPaths.Find(definition, component) is { Indexed: false })
            {
                yield return new DefinitionProblem("roles.duplicateKey", $"'{component}' is not indexed; every component other than $supplierId and the documentNumber field must be.");
            }
        }
    }
}
