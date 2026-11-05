using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Fields;

namespace DocProc.Api.Documents;

/// <summary>A value of a row the table tool read: the field, the value in the shape a correction takes, and where it is on the page.</summary>
public sealed record CapturedCell(string Field, JsonElement Value, FieldSource? Source);

/// <summary>
/// A row to write into a repeating group (docs/11 §12.4): its values and, for a repeating group inside the row (a price
/// list's <c>tiers</c>), that group's rows.
/// </summary>
public sealed record CapturedRow(IReadOnlyList<CapturedCell> Cells, IReadOnlyDictionary<string, IReadOnlyList<CapturedRow>>? Groups = null);

public static partial class DefinitionPatcher
{
    /// <summary>
    /// Replaces every row of the top-level repeating group <paramref name="path"/> with <paramref name="rows"/> as one
    /// correction (docs/11 §12.4 "the result is written as one Correction with op <c>set</c> on <c>lines</c>"): each
    /// value is converted by its field's kind and stored with its own place on the page (<see cref="CapturedCell.Source"/>),
    /// the group's <c>maxItems</c> (or <see cref="DefaultMaxRows"/>, <see cref="DefaultMaxNestedRows"/> for a group inside
    /// a row) holds, so does <see cref="MaxTreeRows"/> for the whole tree, and the audit record carries the rows' values
    /// before and after. Every other field of the tree stays as stored. Throws <see cref="CanonicalPatchException"/> and
    /// changes nothing when a row does not fit.
    /// </summary>
    public static CanonicalPatchResult SetRows(
        DescriptorIndex descriptors,
        string canonicalJson,
        string path,
        IReadOnlyList<CapturedRow> rows,
        string by,
        DateTimeOffset at,
        string? fallbackCurrency = null,
        bool typedRecord = false,
        string? reason = null,
        CorrectionSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(canonicalJson) as JsonObject ?? throw new CanonicalPatchException(string.Empty, "The stored canonical document is not a JSON object.");
        }
        catch (JsonException)
        {
            throw new CanonicalPatchException(string.Empty, "The stored canonical document is not JSON.");
        }

        var field = descriptors.Find(path);
        if (field is not { Kind: FieldKinds.Array } || path.Contains('.', StringComparison.Ordinal) || path.Contains('[', StringComparison.Ordinal))
        {
            throw new CanonicalPatchException(path, $"'{path}' is not a repeating group of the document type.");
        }

        var context = new Context(descriptors, root, by, at, fallbackCurrency, typedRecord);
        var applied = ReplaceRows(context, root, path, path, field, rows, reason, source);
        var json = root.ToJsonString();
        if (typedRecord)
        {
            EnsureInvoiceReads(json);
        }

        return new CanonicalPatchResult(json, [applied]);
    }

    /// <summary>
    /// <c>set</c> on a whole repeating group (<c>{path: "lines", op: "set", value: [{…}, …]}</c>): the reviewer replaces
    /// every row; each row is an object of its field values (a nested group, such as <c>tiers</c>, as an array of
    /// such objects), all with the correction's source.
    /// </summary>
    private static AppliedCorrection SetGroup(Context context, JsonObject owner, string path, FieldDescriptor field, string definitionPath, FieldCorrection correction)
    {
        if (correction.Value is not { ValueKind: JsonValueKind.Array } array)
        {
            throw new CanonicalPatchException(path, $"'{field.Key}' is a repeating group: setting it replaces all its rows, so send an array of row objects (an empty array removes every row).");
        }

        var source = CorrectionSources.ToFieldSource(correction.Source);
        var rows = new List<CapturedRow>();
        foreach (var element in array.EnumerateArray())
        {
            rows.Add(RowOf(context, path, definitionPath, element, source));
        }

        return ReplaceRows(context, owner, path, definitionPath, field, rows, correction.Reason, correction.Source);
    }

    private static CapturedRow RowOf(Context context, string path, string definitionPath, JsonElement element, FieldSource? source)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new CanonicalPatchException(path, "Each row is an object of its field values.");
        }

        var cells = new List<CapturedCell>();
        var groups = new Dictionary<string, IReadOnlyList<CapturedRow>>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var child = context.Descriptors.Find($"{definitionPath}[*].{property.Name}");
            if (child?.Kind == FieldKinds.Array)
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new CanonicalPatchException(path, $"'{property.Name}' is a repeating group; give its rows as an array.");
                }

                groups[property.Name] = [.. property.Value.EnumerateArray().Select(e => RowOf(context, path, $"{definitionPath}[*].{property.Name}", e, source))];
            }
            else
            {
                cells.Add(new CapturedCell(property.Name, property.Value.Clone(), source));
            }
        }

        return new CapturedRow(cells, groups);
    }

    private static AppliedCorrection ReplaceRows(Context context, JsonObject owner, string path, string definitionPath, FieldDescriptor field, IReadOnlyList<CapturedRow> rows, string? reason, CorrectionSource? source)
    {
        var old = owner[field.Key] as JsonArray;
        // Counted before anything is built: every row, nested ones included, against the group's cap and the tree's.
        CheckCaps(context, path, definitionPath, field, rows);
        TakeRows(context, path, removed: CountRows(old), added: CountCaptured(rows));
        var oldValues = old is null ? null : RowValues(old).ToJsonString();
        var built = BuildRows(context, path, definitionPath, field, rows);
        owner[field.Key] = built;
        return new AppliedCorrection(path, oldValues, RowValues(built).ToJsonString(), reason, CorrectionOps.Set, source);
    }

    /// <summary>The rows given, nested ones included.</summary>
    private static int CountCaptured(IReadOnlyList<CapturedRow> rows)
    {
        var count = rows.Count;
        foreach (var row in rows)
        {
            foreach (var (_, nested) in row.Groups ?? new Dictionary<string, IReadOnlyList<CapturedRow>>())
            {
                count += CountCaptured(nested);
            }
        }

        return count;
    }

    /// <summary>Refuses rows beyond the cap of their group (<see cref="MaxRowsOf"/>), at every level, before any row is built.</summary>
    private static void CheckCaps(Context context, string path, string definitionPath, FieldDescriptor field, IReadOnlyList<CapturedRow> rows)
    {
        var max = MaxRowsOf(field, definitionPath);
        if (rows.Count > max)
        {
            throw new CanonicalPatchException(path, $"{rows.Count.ToString(CultureInfo.InvariantCulture)} rows do not fit: '{field.Key}' holds at most {max.ToString(CultureInfo.InvariantCulture)}.");
        }

        for (var i = 0; i < rows.Count; i++)
        {
            foreach (var (name, nested) in rows[i].Groups ?? new Dictionary<string, IReadOnlyList<CapturedRow>>())
            {
                var groupPath = $"{definitionPath}[*].{name}";
                if (context.Descriptors.Find(groupPath) is { Kind: FieldKinds.Array } group)
                {
                    CheckCaps(context, $"{path}[{i.ToString(CultureInfo.InvariantCulture)}].{name}", groupPath, group, nested);
                }
            }
        }
    }

    private static JsonArray BuildRows(Context context, string path, string definitionPath, FieldDescriptor field, IReadOnlyList<CapturedRow> rows)
    {

        var numbered = context.Descriptors.Find($"{definitionPath}[*].{LineNumberKey}") is { Provenance: false, Kind: FieldKinds.Integer };
        var array = new JsonArray();
        for (var i = 0; i < rows.Count; i++)
        {
            var rowPath = $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]";
            var row = DefinitionTree.EmptyRow(context.Descriptors, definitionPath);
            if (numbered)
            {
                row[LineNumberKey] = i + 1;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cell in rows[i].Cells)
            {
                var child = context.Descriptors.Find($"{definitionPath}[*].{cell.Field}");
                if (child is null || !child.IsEnvelope || child.ReadOnly || child.IsList)
                {
                    throw new CanonicalPatchException($"{rowPath}.{cell.Field}", $"'{cell.Field}' is not a field a row of '{field.Key}' can be given.");
                }

                if (!seen.Add(cell.Field))
                {
                    throw new CanonicalPatchException($"{rowPath}.{cell.Field}", $"'{cell.Field}' is given twice for one row.");
                }

                var value = Convert(context, $"{rowPath}.{cell.Field}", child, cell.Value, null, row);
                row[cell.Field] = CorrectEnvelope(context, $"{rowPath}.{cell.Field}", row[cell.Field], value, cell.Source);
            }

            foreach (var (name, nested) in rows[i].Groups ?? new Dictionary<string, IReadOnlyList<CapturedRow>>())
            {
                var groupPath = $"{definitionPath}[*].{name}";
                var group = context.Descriptors.Find(groupPath);
                if (group is not { Kind: FieldKinds.Array })
                {
                    throw new CanonicalPatchException($"{rowPath}.{name}", $"'{name}' is not a repeating group of a row of '{field.Key}'.");
                }

                row[name] = BuildRows(context, $"{rowPath}.{name}", groupPath, group, nested);
            }

            array.Add(row);
        }

        return array;
    }

    /// <summary>The rows as their bare values (Missing fields left out), as the audit record keeps them.</summary>
    private static JsonArray RowValues(JsonArray rows)
    {
        var values = new JsonArray();
        foreach (var row in rows)
        {
            if (row is not JsonObject fields)
            {
                continue;
            }

            var compact = new JsonObject();
            foreach (var (key, node) in fields)
            {
                if (node is JsonArray nested)
                {
                    if (nested.Count > 0)
                    {
                        compact[key] = nested.All(n => n is JsonObject o && !DefinitionTree.IsEnvelope(o)) ? RowValues(nested) : new JsonArray([.. nested.Select(n => DefinitionTree.ValueOf(n)?.DeepClone())]);
                    }
                }
                else if (DefinitionTree.IsEnvelope(node))
                {
                    if (DefinitionTree.ValueOf(node) is { } value)
                    {
                        compact[key] = value.DeepClone();
                    }
                }
                else if (node is JsonValue bare)
                {
                    compact[key] = bare.DeepClone();
                }
            }

            values.Add(compact);
        }

        return values;
    }
}
