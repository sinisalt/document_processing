namespace DocProc.Definitions;

/// <summary>A field of a definition with its full path (<c>supplier</c>, <c>orderReferences[*]</c>, <c>lines[*].lineNet</c>).</summary>
public sealed record DefinitionField(string Path, FieldDefinition Field);

/// <summary>
/// The path grammar of definitions (docs/11 §5.2 and §11): segments joined by dots, <c>[*]</c> after a list or an
/// array for "every entry". A list field's values sit at <c>key[*]</c>; an array's row fields at <c>key[*].child</c>.
/// </summary>
public static class DefinitionPaths
{
    /// <summary>Every field of the tree, depth first in definition order, including the fields of array rows and objects.</summary>
    public static IEnumerable<DefinitionField> Enumerate(TypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Enumerate(definition.Fields, prefix: null);
    }

    /// <summary>
    /// The paths of the values that carry their own <c>FieldValue</c> envelope: what a correction addresses and what
    /// <c>Invoice.EnumerateAllFields</c> lists for the invoice record (with its indexes written as <c>[*]</c>).
    /// </summary>
    public static IEnumerable<string> EnvelopePaths(TypeDefinition definition) =>
        Enumerate(definition)
            .Where(f => f.Field.Provenance && f.Field.Kind is not (FieldKinds.Array or FieldKinds.Object))
            .Select(f => f.Field.List ? $"{f.Path}[*]" : f.Path);

    /// <summary>The field at <paramref name="path"/> (with or without a trailing <c>[*]</c> on a list or array), or null.</summary>
    public static FieldDefinition? Find(TypeDefinition definition, string path)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        IReadOnlyList<FieldDefinition>? level = definition.Fields;
        FieldDefinition? current = null;
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            if (level is null)
            {
                return null;
            }

            var segment = segments[i];
            var many = segment.EndsWith("[*]", StringComparison.Ordinal);
            var key = many ? segment[..^3] : segment;
            current = level.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
            if (current is null || (many && !(current.List || current.Kind == FieldKinds.Array)))
            {
                return null;
            }

            var last = i == segments.Length - 1;
            if (!last && current.Kind == FieldKinds.Array && !many)
            {
                // A row field is reached through the rows: lines[*].lineNet, never lines.lineNet.
                return null;
            }

            level = current.Kind switch
            {
                FieldKinds.Array => current.Item?.Fields,
                FieldKinds.Object => current.Fields,
                _ => null,
            };
        }

        return current;
    }

    /// <summary>
    /// The kind of the value at <paramref name="path"/>: a field's path, or a field's path followed by members of its
    /// value shape (<c>lines[*].price.amount</c>, <c>supplier.address.city</c>), as rule parameters address them
    /// (docs/11 §11). Null when the path names nothing.
    /// </summary>
    public static string? FindValue(TypeDefinition definition, string path)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = path.Split('.');
        for (var take = segments.Length; take >= 1; take--)
        {
            if (Find(definition, string.Join('.', segments[..take])) is not { } field)
            {
                continue;
            }

            var kind = field.Kind;
            foreach (var member in segments[take..])
            {
                var next = FieldKinds.CompositeMemberKinds.TryGetValue(kind, out var members)
                    ? members.FirstOrDefault(m => string.Equals(m.Member, member, StringComparison.Ordinal)).Kind
                    : null;
                if (next is null)
                {
                    return null;
                }

                kind = next;
            }

            return kind;
        }

        return null;
    }

    private static IEnumerable<DefinitionField> Enumerate(IReadOnlyList<FieldDefinition> fields, string? prefix)
    {
        foreach (var field in fields)
        {
            var path = prefix is null ? field.Key : $"{prefix}.{field.Key}";
            yield return new DefinitionField(path, field);

            var children = field.Kind switch
            {
                FieldKinds.Array => (field.Item?.Fields, $"{path}[*]"),
                FieldKinds.Object => (field.Fields, path),
                _ => (null, path),
            };
            if (children.Item1 is { } nested)
            {
                foreach (var child in Enumerate(nested, children.Item2))
                {
                    yield return child;
                }
            }
        }
    }
}
