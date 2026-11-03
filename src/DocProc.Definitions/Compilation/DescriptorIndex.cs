using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DocProc.Definitions.Compilation;

/// <summary>
/// One field of a compiled definition (docs/11 §5.5, artifact 4): what the patcher, the role projector, the rule
/// engine, the mapping engine and the values-view builder need to know about a path without walking the definition.
/// <see cref="Path"/> uses the definition grammar (<c>lines[*].lineNet</c>); <see cref="Children"/> are the paths of
/// the fields of an array's rows or an object's members, in definition order. Composite members (a party's
/// <c>name</c>) are parts of one value, listed in <see cref="Members"/>, not fields of their own.
/// </summary>
public sealed record FieldDescriptor
{
    public required string Path { get; init; }

    public required string FieldId { get; init; }

    public required string Key { get; init; }

    public required string Kind { get; init; }

    /// <summary>A list of values (<c>orderReferences</c>), each with its own envelope.</summary>
    public bool IsList { get; init; }

    public IReadOnlyList<string> Children { get; init; } = [];

    /// <summary>The members of a composite or value-shaped kind with their kinds (<c>money</c>: amount, currency).</summary>
    public IReadOnlyList<MemberDescriptor> Members { get; init; } = [];

    /// <summary>The path of the currency field of a money field.</summary>
    public string? CurrencyFrom { get; init; }

    public bool Required { get; init; }

    public bool Sensitive { get; init; }

    public bool Indexed { get; init; }

    /// <summary>False for a member the tree stores bare, without the <c>FieldValue</c> envelope.</summary>
    public bool Provenance { get; init; } = true;

    public bool ReadOnly { get; init; }

    public bool Deprecated { get; init; }

    /// <summary>The enum codes, when the field lists them inline.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    public string? CodeList { get; init; }

    /// <summary>The row field that anchors a row on the page (arrays).</summary>
    public string? Anchor { get; init; }

    public int? MaxItems { get; init; }

    /// <summary>
    /// The keys the same field (by <see cref="FieldId"/>, under the same parent) had in the type's earlier published
    /// versions, when it was renamed (docs/11 §5.7): a tree stored for such a version holds its value under one of them,
    /// and completing the tree for this version carries the value over (<c>DefinitionTree.Complete</c>). Null when the
    /// key never changed. Set at publish (<see cref="DescriptorIndex.WithFormerKeys"/>), not by the compiler.
    /// </summary>
    public IReadOnlyList<string>? FormerKeys { get; init; }

    /// <summary>Whether the value carries a <c>FieldValue</c> envelope a correction can address.</summary>
    [JsonIgnore]
    public bool IsEnvelope => Provenance && Kind is not (FieldKinds.Array or FieldKinds.Object);
}

/// <summary>A member of a composite value and its kind.</summary>
public sealed record MemberDescriptor(string Key, string Kind);

/// <summary>
/// The descriptor index of a compiled definition: every field by path (artifact 4 of docs/11 §5.5), stored as
/// <c>DocumentTypeVersion.DescriptorsJson</c>. <see cref="Find"/> takes concrete paths too (<c>lines[2].lineNet</c>).
/// </summary>
public sealed partial class DescriptorIndex
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Nulls are left out; every flag is written, because Provenance defaults to true and false must survive.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly FrozenDictionary<string, FieldDescriptor> _byPath;

    public DescriptorIndex(IEnumerable<FieldDescriptor> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Fields = [.. fields];
        _byPath = Fields.ToFrozenDictionary(f => f.Path, StringComparer.Ordinal);
    }

    /// <summary>Every field, depth first in definition order.</summary>
    public IReadOnlyList<FieldDescriptor> Fields { get; }

    /// <summary>The top-level fields.</summary>
    public IEnumerable<FieldDescriptor> Roots => Fields.Where(f => !f.Path.Contains('.', StringComparison.Ordinal));

    /// <summary>
    /// The field at <paramref name="path"/>: a definition path (<c>lines[*].lineNet</c>) or a concrete one with row or
    /// list indexes (<c>lines[3].lineNet</c>, <c>orderReferences[0]</c>); null when no field is there.
    /// </summary>
    public FieldDescriptor? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = IndexPattern().Replace(path, "[*]");
        if (_byPath.TryGetValue(normalized, out var field))
        {
            return field;
        }

        // A list value (orderReferences[0]) is the list field itself.
        return normalized.EndsWith("[*]", StringComparison.Ordinal) && _byPath.TryGetValue(normalized[..^3], out var list) && list.IsList ? list : null;
    }

    /// <summary>
    /// This index with <see cref="FieldDescriptor.FormerKeys"/> set from <paramref name="earlier"/>, the type's earlier
    /// published versions: for each field, the other keys a field with the same id had under the same parent (the same
    /// parent id, or both at the top). A field moved to another parent is not followed.
    /// </summary>
    public DescriptorIndex WithFormerKeys(IEnumerable<TypeDefinition> earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        var former = new Dictionary<(string FieldId, string? ParentId), HashSet<string>>();
        foreach (var definition in earlier)
        {
            var fields = DefinitionPaths.Enumerate(definition).ToList();
            var idsByPath = fields.GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Field.Id, StringComparer.Ordinal);
            foreach (var (path, field) in fields)
            {
                var slot = (field.Id, ParentPath(path) is { } parent ? idsByPath.GetValueOrDefault(parent) : null);
                if (!former.TryGetValue(slot, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    former[slot] = keys;
                }

                keys.Add(field.Key);
            }
        }

        return new DescriptorIndex(Fields.Select(field =>
        {
            var parentId = ParentPath(field.Path) is { } parent && _byPath.TryGetValue(parent, out var parentField) ? parentField.FieldId : null;
            var keys = former.TryGetValue((field.FieldId, parentId), out var found)
                ? found.Where(k => k != field.Key).Order(StringComparer.Ordinal).ToList()
                : [];
            return field with { FormerKeys = keys.Count > 0 ? keys : null };
        }));
    }

    /// <summary>The definition path of the field a path is a row field or member of (<c>lines</c> for <c>lines[*].lineNet</c>), or null at the top.</summary>
    private static string? ParentPath(string path)
    {
        var dot = path.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        var parent = path[..dot];
        return parent.EndsWith("[*]", StringComparison.Ordinal) ? parent[..^3] : parent;
    }

    public string ToJson() => JsonSerializer.Serialize(new Stored(FormatVersion, Fields), Json);

    public static DescriptorIndex FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var stored = JsonSerializer.Deserialize<Stored>(json, Json) ?? throw new JsonException("Descriptors JSON was null.");
        if (stored.Version != FormatVersion)
        {
            throw new JsonException($"Descriptors format version {stored.Version} is not {FormatVersion}.");
        }

        return new DescriptorIndex(stored.Fields);
    }

    [GeneratedRegex(@"\[\d+\]", RegexOptions.CultureInvariant)]
    private static partial Regex IndexPattern();

    private sealed record Stored(int Version, IReadOnlyList<FieldDescriptor> Fields);
}
