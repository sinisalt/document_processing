using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Jsonata.Net.Native.Impl;

namespace DocProc.Outputs.Renderer;

/// <summary>
/// The values-view paths an expression reads (<c>OutputMappingVersion.ReferencedPathsJson</c>, docs/11 §6.7), from a
/// scan of its syntax tree: each path of names, with the path of the enclosing step in front of a path inside a row
/// block (<c>doc.lines.{ "x": supplierSku }</c> reads <c>doc.lines.supplierSku</c>), a path from <c>$root</c> or
/// <c>$</c> read from the root. Paths from other variables are left out (their value is not known without running
/// the expression). Also the lookup tables <c>$table</c> names with a string literal.
/// </summary>
public static class ReferencedPaths
{
    /// <summary>At most this many paths are reported.</summary>
    public const int MaxPaths = 2000;

    private const int MaxDepth = 400;

    public sealed record Scan(IReadOnlyList<string> Paths, IReadOnlyList<string> Tables);

    public static Scan Of(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var tables = new SortedSet<string>(StringComparer.Ordinal);
        Walk(root, string.Empty, paths, tables, 0);
        return new Scan([.. paths.Take(MaxPaths)], [.. tables]);
    }

    private static void Walk(Node node, string? prefix, SortedSet<string> paths, SortedSet<string> tables, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        if (node is PathRuntimeNode path)
        {
            WalkPath(path, prefix, paths, tables, depth);
            return;
        }

        if (node is FunctionalNode { procedure: VariableNode { value: "table" }, arguments: [StringNode key, ..] })
        {
            tables.Add(key.value);
        }

        if (node is NameNode name)
        {
            Add(paths, Join(prefix, name.value));
            WalkChildren(node, Join(prefix, name.value), paths, tables, depth);
            return;
        }

        WalkChildren(node, prefix, paths, tables, depth);
    }

    private static void WalkPath(PathRuntimeNode path, string? prefix, SortedSet<string> paths, SortedSet<string> tables, int depth)
    {
        var current = prefix;
        for (var i = 0; i < path.steps.Count; i++)
        {
            var step = path.steps[i];
            switch (step)
            {
                case VariableNode variable when i == 0:
                    current = variable.value is "root" or "" ? string.Empty : null;
                    break;
                case NameNode name:
                    current = Join(current, name.value);
                    Add(paths, current);
                    // Filters and other stages of the step read from the step's items.
                    WalkChildren(step, current, paths, tables, depth);
                    break;
                default:
                    Walk(step, current, paths, tables, depth + 1);
                    break;
            }
        }
    }

    private static void WalkChildren(Node node, string? prefix, SortedSet<string> paths, SortedSet<string> tables, int depth)
    {
        // The tree exposes its children as public fields and properties (a step's stages are a property); a value
        // reachable through both is walked once.
        var type = node.GetType();
        var values = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.GetValue(node))
            .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.CanRead).Select(p => p.GetValue(node)))
            .Where(v => v is not null)
            .Distinct(ReferenceEqualityComparer.Instance);
        foreach (var value in values)
        {
            switch (value)
            {
                case Node child:
                    Walk(child, prefix, paths, tables, depth + 1);
                    break;
                case string:
                    break;
                case IEnumerable items:
                    foreach (var item in items)
                    {
                        if (item is Node element)
                        {
                            Walk(element, prefix, paths, tables, depth + 1);
                        }
                        else if (item is ITuple tuple)
                        {
                            for (var i = 0; i < tuple.Length; i++)
                            {
                                if (tuple[i] is Node member)
                                {
                                    Walk(member, prefix, paths, tables, depth + 1);
                                }
                            }
                        }
                    }

                    break;
            }
        }
    }

    private static string? Join(string? prefix, string name) => prefix is null ? null : prefix.Length == 0 ? name : $"{prefix}.{name}";

    private static void Add(SortedSet<string> paths, string? path)
    {
        if (path is { Length: > 0 } && paths.Count < MaxPaths * 2)
        {
            paths.Add(path);
        }
    }
}
