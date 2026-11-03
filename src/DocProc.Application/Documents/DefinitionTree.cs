using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Fields;

namespace DocProc.Application.Documents;

/// <summary>
/// The canonical tree of a definition-driven document (docs/11 §5.6): the same <c>FieldValue</c> envelope tree the
/// coded records store, with generic leaves (<c>FieldValue&lt;JsonNode&gt;</c>, the same envelope), laid out by the
/// definition's descriptor index. <see cref="CreateEmpty"/> is what a document gets when no extraction path fills it
/// (every field Missing, docs/11 §12.1 step 3), and <see cref="Complete"/> adds the fields a tree lacks, so a tree
/// stored for an earlier version of the type keeps every value it has and gains the new fields.
/// </summary>
public static class DefinitionTree
{
    /// <summary>
    /// <c>Documents.CanonicalSchemaVersion</c> of a tree written against a definition (docs/11 §5.6): version 2 is a
    /// superset of version 1, the coded records' trees, which still deserialise unchanged.
    /// </summary>
    public const int SchemaVersion = 2;

    /// <summary>The legacy version: a tree written by the coded records (<c>CanonicalDocumentSerializer.SchemaVersion</c>).</summary>
    public const int LegacySchemaVersion = 1;

    /// <summary>The envelope of a field that has no value yet, as the coded records write <c>FieldValue&lt;T&gt;.Missing()</c>.</summary>
    public static JsonObject MissingEnvelope() => new() { ["status"] = nameof(FieldStatus.Missing), ["history"] = new JsonArray() };

    /// <summary>A tree with every top-level field of <paramref name="descriptors"/> present and Missing; lists, arrays and bags empty.</summary>
    public static JsonObject CreateEmpty(DescriptorIndex descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var root = new JsonObject();
        CompleteFields(root, descriptors.Roots, descriptors);
        return root;
    }

    /// <summary>
    /// Adds every field of <paramref name="descriptors"/> that <paramref name="root"/> lacks (recursively into objects
    /// and existing array rows), leaving the values it has as they are. Returns whether anything was added.
    /// </summary>
    public static bool Complete(JsonObject root, DescriptorIndex descriptors)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(descriptors);
        return CompleteFields(root, descriptors.Roots, descriptors);
    }

    /// <summary>A new row of the array at <paramref name="arrayPath"/> (a definition path), with every row field Missing.</summary>
    public static JsonObject EmptyRow(DescriptorIndex descriptors, string arrayPath)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var array = descriptors.Find(arrayPath) ?? throw new ArgumentException($"'{arrayPath}' is not a field.", nameof(arrayPath));
        var row = new JsonObject();
        CompleteFields(row, array.Children.Select(c => descriptors.Find(c)!), descriptors);
        return row;
    }

    /// <summary>The empty value of a field: what <see cref="CreateEmpty"/> writes for it, or null for a bare member (no envelope).</summary>
    public static JsonNode? EmptyValue(FieldDescriptor field, DescriptorIndex descriptors)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(descriptors);
        if (field.IsList || field.Kind == FieldKinds.Array)
        {
            return new JsonArray();
        }

        if (field.Kind == FieldKinds.Object)
        {
            var group = new JsonObject();
            CompleteFields(group, field.Children.Select(c => descriptors.Find(c)!), descriptors);
            return group;
        }

        if (field.Kind == FieldKinds.Bag)
        {
            return new JsonObject();
        }

        return field.Provenance ? MissingEnvelope() : null;
    }

    private static bool CompleteFields(JsonObject node, IEnumerable<FieldDescriptor> fields, DescriptorIndex descriptors)
    {
        var added = false;
        foreach (var field in fields)
        {
            if (node.TryGetPropertyValue(field.Key, out var existing) && existing is not null)
            {
                if (field.Kind == FieldKinds.Object && existing is JsonObject group)
                {
                    added |= CompleteFields(group, field.Children.Select(c => descriptors.Find(c)!), descriptors);
                }
                else if (field.Kind == FieldKinds.Array && existing is JsonArray rows)
                {
                    var children = field.Children.Select(c => descriptors.Find(c)!).ToList();
                    foreach (var row in rows.OfType<JsonObject>())
                    {
                        added |= CompleteFields(row, children, descriptors);
                    }
                }

                continue;
            }

            if (EmptyValue(field, descriptors) is { } empty)
            {
                node[field.Key] = empty;
                added = true;
            }
        }

        return added;
    }

    /// <summary>Whether <paramref name="node"/> is a <c>FieldValue</c> envelope: an object with a string <c>status</c> naming a <see cref="FieldStatus"/>.</summary>
    public static bool IsEnvelope(JsonNode? node) =>
        node is JsonObject envelope
        && envelope.TryGetPropertyValue("status", out var status)
        && status is JsonValue value
        && value.GetValueKind() == JsonValueKind.String
        && Enum.TryParse<FieldStatus>(value.GetValue<string>(), ignoreCase: true, out _);

    /// <summary>The status of an envelope (Missing for anything that is not one).</summary>
    public static FieldStatus StatusOf(JsonNode? node) =>
        node is JsonObject envelope && envelope["status"] is JsonValue value && value.GetValueKind() == JsonValueKind.String
        && Enum.TryParse<FieldStatus>(value.GetValue<string>(), ignoreCase: true, out var status)
            ? status
            : FieldStatus.Missing;

    /// <summary>The value of an envelope, or null when it is Missing, Unreadable or not an envelope.</summary>
    public static JsonNode? ValueOf(JsonNode? node) =>
        node is JsonObject envelope && IsEnvelope(envelope) && StatusOf(envelope) is not (FieldStatus.Missing or FieldStatus.Unreadable)
            ? envelope["value"]
            : null;
}
