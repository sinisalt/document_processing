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

    /// <summary>
    /// The keys the fields of the type's earlier published (and deprecated) versions had (<see cref="DescriptorIndex.KeySlotsOf"/>);
    /// the published version's are added from <see cref="Published"/>. A key one field had is not given to another.
    /// </summary>
    public IReadOnlyList<FieldKeySlot> EarlierKeys { get; init; } = [];

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
        problems.AddRange(CheckInvoiceRecordFields(definition));

        var warnings = new List<DefinitionProblem>();
        if (AutoApproveHasNoEffect(definition))
        {
            warnings.Add(new DefinitionProblem(
                "processing.review.autoApprove",
                "has no effect yet: until a type can name the rules that vouch for it (review.requirePassedRules, docs/11 §11), only an invoice or credit note with its invoice.totals rule is approved without a reviewer. Documents of this type always go to review."));
        }

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

        // An id whose key and kind both changed is a new field that took a published field's id (docs/11 §5.2: ids are
        // never reused). Matched by id it would pass as a change of that field; it needs an id of its own.
        foreach (var change in changes.Where(c => c.Change == FieldChangeKind.KindChanged && KeyOf(c.OldPath) != KeyOf(c.NewPath)))
        {
            problems.Add(new DefinitionProblem(
                change.NewPath ?? change.FieldId,
                $"uses the id '{change.FieldId}' of the published field '{change.OldPath}' ({change.OldKind}) with another key and kind ({change.NewKind}); a new field needs a new id, ids are never reused."));
        }

        foreach (var change in changes.Where(c => c.Change == FieldChangeKind.Renamed))
        {
            var dependents = input.Dependents.Count(d => d.FieldId == change.FieldId || d.Path == change.OldPath);
            if (dependents > 0)
            {
                warnings.Add(new DefinitionProblem(change.OldPath!, $"is renamed to {change.NewPath}; {dependents} dependent(s) still name the old path and need new draft versions."));
            }

            // A renamed field keeps its value in documents moved onto the new version (FieldDescriptor.FormerKeys); one
            // moved to another group or table does not, so the reviewer would find it empty there.
            if (ParentIdOf(input.Published!, change.OldPath!) != ParentIdOf(definition, change.NewPath!))
            {
                warnings.Add(new DefinitionProblem(change.OldPath!, $"moves to {change.NewPath}; documents stored under an earlier version keep its value at the old place, so on the new version it starts empty."));
            }
        }

        var earlierKeys = input.Published is { } current ? [.. input.EarlierKeys, .. DescriptorIndex.KeySlotsOf(current)] : input.EarlierKeys;
        CheckReusedKeys(definition, earlierKeys, problems, warnings);

        return new PublishReport(definition, problems.Count == 0 ? compiled : null, problems, warnings, changes, blocking);
    }

    /// <summary>
    /// The key-level twin of "ids are never reused" (docs/11 §5.2, §5.7): a tree stored under an earlier version holds
    /// each value under the key its field had then, and completing it for this version moves a renamed field's value
    /// by its former key (<see cref="FieldDescriptor.FormerKeys"/>). A field that takes the earlier key of another field
    /// of this version would receive that field's value and leave the renamed field empty, so it is refused. A field
    /// that takes the key of a field this version no longer has is allowed, with a warning: documents stored under the
    /// earlier version show the removed field's value as its.
    /// </summary>
    private static void CheckReusedKeys(TypeDefinition definition, IReadOnlyList<FieldKeySlot> earlier, List<DefinitionProblem> problems, List<DefinitionProblem> warnings)
    {
        if (earlier.Count == 0)
        {
            return;
        }

        var candidate = DefinitionPaths.Enumerate(definition).ToList();
        var paths = candidate.GroupBy(f => f.Field.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Path, StringComparer.Ordinal);
        var byKey = earlier.GroupBy(s => (s.ParentId, s.Key)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (path, field) in candidate)
        {
            if (!byKey.TryGetValue((ParentIdOf(definition, path), field.Key), out var slots))
            {
                continue;
            }

            foreach (var other in slots.Where(s => s.FieldId != field.Id).GroupBy(s => s.FieldId, StringComparer.Ordinal).Select(g => g.MaxBy(s => s.Version)!))
            {
                if (paths.TryGetValue(other.FieldId, out var otherPath))
                {
                    problems.Add(new DefinitionProblem(
                        path,
                        $"takes the key '{field.Key}', which the field '{other.FieldId}' (now '{otherPath}') had in version {other.Version}; documents stored under that version hold its value under '{field.Key}', so on this version the value would land in this field and '{otherPath}' would start empty. Give this field another key; a key a field had is not given to another field."));
                }
                else
                {
                    warnings.Add(new DefinitionProblem(
                        path,
                        $"takes the key '{field.Key}' of the field '{other.FieldId}' that version {other.Version} had and this version removes; documents stored under that version show the removed field's value as this field's."));
                }
            }
        }
    }

    /// <summary>The id of the field <paramref name="path"/> is a row field or member of, or null at the top.</summary>
    private static string? ParentIdOf(TypeDefinition definition, string path)
    {
        var dot = path.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        var parent = path[..dot];
        return DefinitionPaths.Find(definition, parent.EndsWith("[*]", StringComparison.Ordinal) ? parent[..^3] : parent)?.Id;
    }

    private static string? KeyOf(string? path) =>
        path is null ? null : path[(path.LastIndexOf('.') + 1)..].Replace("[*]", string.Empty, StringComparison.Ordinal);

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

            if (kind.Kind == "pattern" && parameters["pattern"] is JsonValue patternValue && patternValue.TryGetValue<string>(out var pattern) && !DefinitionChecker.IsValidPattern(pattern))
            {
                yield return new DefinitionProblem(at, "pattern is not a valid regular expression (backreferences, lookarounds and atomic groups are not supported).");
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

    /// <summary>
    /// The template whose tree the coded <c>Invoice</c> record holds for <paramref name="definition"/>, or null: a type of
    /// the invoice family (its key is <c>Invoice</c> or <c>CreditNote</c>, so the coded record, the coded extraction and
    /// the coded invoice rules read its documents), one copied from <c>std.invoice</c> or <c>std.credit-note</c>, or one
    /// extracted by <c>invoice_extraction@1</c>.
    /// </summary>
    public static TypeDefinition? InvoiceRecordTemplate(TypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Base is { Template: "std.invoice" or "std.credit-note" } baseRef)
        {
            return StandardTemplates.Find(baseRef.Template, baseRef.Version) ?? StandardTemplates.Find(baseRef.Template);
        }

        if (Domain.Documents.DocumentTypeKey.TryParse(definition.Key, out var key) && key.IsInvoiceFamily)
        {
            return StandardTemplates.ForLegacyType(key);
        }

        return DefinitionCompiler.AdapterOf(definition) == DefinitionCompiler.InvoiceAdapter ? StandardTemplates.Find("std.invoice") : null;
    }

    /// <summary>
    /// docs/11 §5.1: the coded invoice kinds need the fields of the invoice record as it holds them. The tree of an
    /// invoice-family type is that record's (it deserialises every one of its documents, the coded extraction writes
    /// it, the coded rules and the patcher read it typed), so a field of the template keeps its id, its key and place,
    /// its kind, whether it is a list, whether it carries an envelope, and (for an enum) codes the record knows. Such
    /// a field may be relabelled, made required or deprecated; it cannot be removed, renamed, moved or retyped. The
    /// generic rule kinds that would replace the coded ones for a restructured invoice arrive in P5.
    /// </summary>
    private static IEnumerable<DefinitionProblem> CheckInvoiceRecordFields(TypeDefinition definition)
    {
        var template = InvoiceRecordTemplate(definition);
        if (template is null)
        {
            yield break;
        }

        var candidate = DefinitionPaths.Enumerate(definition).GroupBy(f => f.Field.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (path, field) in DefinitionPaths.Enumerate(template))
        {
            if (!candidate.TryGetValue(field.Id, out var now))
            {
                yield return new DefinitionProblem(path, $"is a field of the invoice record ({template.Key}, id '{field.Id}'), which the coded invoice extraction and rules read; it can be deprecated, not removed.");
                continue;
            }

            if (now.Path != path)
            {
                yield return new DefinitionProblem(now.Path, $"is the invoice record's field '{path}' (id '{field.Id}'); the record reads it under that key and place, so it cannot be renamed or moved.");
            }

            if (now.Field.Kind != field.Kind || now.Field.List != field.List)
            {
                yield return new DefinitionProblem(now.Path, $"is held by the invoice record as {Describe(field)}; it cannot become {Describe(now.Field)}.");
            }
            else if (now.Field.Provenance != field.Provenance)
            {
                yield return new DefinitionProblem(now.Path, field.Provenance
                    ? "carries a FieldValue envelope in the invoice record; it cannot be stored bare (provenance: false)."
                    : "is stored bare by the invoice record (provenance: false); it cannot carry an envelope.");
            }
            else if (field.Kind == FieldKinds.Enum && field.Options is { } known && now.Field.Options is { } options)
            {
                var codes = known.Select(o => o.Code).ToHashSet(StringComparer.Ordinal);
                var unknown = options.Select(o => o.Code).Where(c => !codes.Contains(c)).ToList();
                if (unknown.Count > 0)
                {
                    yield return new DefinitionProblem(now.Path, $"may only list codes the invoice record knows ({string.Join(", ", codes)}), not {string.Join(", ", unknown)}.");
                }
            }
        }

        static string Describe(FieldDefinition field) => field.List ? $"a list of {field.Kind}" : field.Kind;
    }

    /// <summary>
    /// Whether <c>review.autoApprove</c> is set on a type the review gate never approves without a reviewer in P1 (the
    /// rule <c>EffectiveType.AutoApprove</c> applies): anything but an invoice-family type that lists <c>invoice.totals</c>.
    /// </summary>
    public static bool AutoApproveHasNoEffect(TypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Processing.Review.AutoApprove
               && !(Domain.Documents.DocumentTypeKey.TryParse(definition.Key, out var key) && key.IsInvoiceFamily
                    && definition.Rules.Any(r => r.Kind == Domain.Validation.InvoiceRuleKinds.Totals));
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
