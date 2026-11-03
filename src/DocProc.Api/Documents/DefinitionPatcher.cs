using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation;

namespace DocProc.Api.Documents;

/// <summary>
/// The correction patcher of definition-driven documents (docs/11 §12.8): <see cref="CanonicalPatcher"/>'s contract
/// (the path grammar, editing the JSON tree so untouched fields stay as stored, all or nothing, the history push and the
/// <see cref="AppliedCorrection"/> audit, the reviewer's shorthands) with the field schema taken from the type version's
/// descriptor index instead of the domain records, and values converted by field kind instead of by CLR type. Every
/// leaf is a generic <c>FieldValue&lt;JsonNode&gt;</c> envelope, so the stored envelope shape is the records' one.
/// <list type="bullet">
/// <item><description><c>set</c> (the default) corrects a field, or a member of a composite value
/// (<c>supplier.vatId</c>), or an entry of a list (<c>orderReferences[0]</c>), or a key of a bag (<c>ext.storeNo</c>);
/// a field the tree does not hold yet is created (an added field, a bag key).</description></item>
/// <item><description><c>append</c> adds a row to a repeating group (<c>lines</c>, with an optional object of row
/// values) or an entry to a list.</description></item>
/// <item><description><c>remove</c> removes a row (<c>lines[2]</c>), a list entry or a bag key.</description></item>
/// <item><description><c>move</c> (docs/11 §12.3 "match fields") takes the value of the field <c>from</c> names and puts it
/// into the field <c>path</c> names, converted to that field's kind and with the place it was read from; <c>from</c> is
/// left Missing. Both fields keep their earlier state in their history. Either end is a single field, a list entry or a
/// field of a row.</description></item>
/// </list>
/// <c>setType</c>, <c>split</c> and <c>join</c> are review actions of their own, not field corrections.
/// </summary>
public static partial class DefinitionPatcher
{
    /// <summary>The operations this patcher applies.</summary>
    public static readonly IReadOnlyList<string> Operations = [CorrectionOps.Set, CorrectionOps.Append, CorrectionOps.Remove, CorrectionOps.Move];

    /// <summary>The most rows an append may grow a repeating group to when the definition sets no <c>maxItems</c>.</summary>
    public const int DefaultMaxRows = 20_000;

    /// <summary>
    /// Applies every correction in order and returns the new JSON; throws <see cref="CanonicalPatchException"/> (and
    /// changes nothing) for an unknown path, an operation that does not fit the field, or a value that does not fit its kind.
    /// </summary>
    /// <param name="descriptors">The descriptor index of the document's type version.</param>
    /// <param name="canonicalJson">The stored tree.</param>
    /// <param name="corrections">What to change.</param>
    /// <param name="by">The reviewer.</param>
    /// <param name="at">When.</param>
    /// <param name="fallbackCurrency">The currency of a bare amount when neither the field nor the document carries one.</param>
    /// <param name="typedRecord">
    /// True when the coded <see cref="Invoice"/> record also reads the tree (the invoice family, docs/11 §5.6), so the
    /// patched tree must stay one it reads: a field whose record type has no empty value (a date, a number, an enum, a
    /// unit) cannot be cleared, as with <see cref="CanonicalPatcher"/>; a unit is stored as the record's
    /// <see cref="UnitOfMeasure"/> name (a printed alias or a UN/ECE code is converted, anything else refused); a new
    /// row gets the record's bare members (its next <c>lineNumber</c>); and a patched tree the record cannot read is
    /// refused as a whole, so nothing is saved.
    /// </param>
    public static CanonicalPatchResult Apply(DescriptorIndex descriptors, string canonicalJson, IReadOnlyList<FieldCorrection> corrections, string by, DateTimeOffset at, string? fallbackCurrency = null, bool typedRecord = false)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(corrections);
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

        var context = new Context(descriptors, root, by, at, fallbackCurrency, typedRecord);
        var applied = new List<AppliedCorrection>(corrections.Count);
        foreach (var correction in corrections)
        {
            applied.Add(ApplyOne(context, correction));
        }

        var json = root.ToJsonString();
        if (typedRecord)
        {
            EnsureInvoiceReads(json);
        }

        return new CanonicalPatchResult(json, applied);
    }

    /// <summary>
    /// Refuses a patched tree the coded <see cref="Invoice"/> record cannot read (the validate stage, the inline
    /// re-validation, approval and the exports read it as one), naming where it fails.
    /// </summary>
    private static void EnsureInvoiceReads(string json)
    {
        string problem;
        string? at = null;
        try
        {
            _ = JsonSerializer.Deserialize<Invoice>(json, CanonicalJson.Options) ?? throw new JsonException("The tree is null.");
            return;
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
            at = ex.Path is { Length: > 2 } jsonPath && jsonPath.StartsWith("$.", StringComparison.Ordinal) ? EnvelopeValue().Replace(jsonPath[2..], string.Empty) : null;
        }
        catch (ArgumentException ex)
        {
            problem = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            problem = ex.Message;
        }

        throw new CanonicalPatchException(
            at ?? string.Empty,
            $"The corrections would leave a document the invoice record cannot read{(at is null ? string.Empty : $" (at {at})")}; nothing was changed. {problem}");
    }

    private sealed record Context(DescriptorIndex Descriptors, JsonObject Root, string By, DateTimeOffset At, string? FallbackCurrency, bool TypedRecord);

    private static AppliedCorrection ApplyOne(Context context, FieldCorrection correction)
    {
        var path = correction.Path?.Trim() ?? string.Empty;
        var op = string.IsNullOrEmpty(correction.Op) ? CorrectionOps.Set : correction.Op;
        if (!Operations.Contains(op, StringComparer.Ordinal))
        {
            throw new CanonicalPatchException(path, CorrectionOps.NotApplied(op, Operations));
        }

        CorrectionSources.Check(path, correction.Source);
        if (op == CorrectionOps.Move)
        {
            return Move(context, path, correction);
        }

        if (correction.From is not null)
        {
            throw new CanonicalPatchException(path, $"'from' belongs to op '{CorrectionOps.Move}' only.");
        }

        var segments = ParsePath(path);
        var node = context.Root;
        string? prefix = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var definitionPath = prefix is null ? segment.Name : $"{prefix}.{segment.Name}";
            var field = context.Descriptors.Find(definitionPath) ?? throw new CanonicalPatchException(path, $"'{segment.Name}' is not a field of the document type.");
            var rest = segments.Skip(i + 1).ToList();
            var here = string.Join('.', segments.Take(i + 1).Select(s => s.ToString()));

            if (field.Kind == FieldKinds.Bag)
            {
                return Bag(context, path, op, node, field, segment, rest, correction);
            }

            if (field.Kind == FieldKinds.Array)
            {
                var rows = ArrayOf(node, segment.Name);
                if (segment.Index is null)
                {
                    if (rest.Count > 0 || op != CorrectionOps.Append)
                    {
                        throw new CanonicalPatchException(path, $"'{segment.Name}' is a repeating group; address a row as {segment.Name}[n], or append one with op 'append'.");
                    }

                    return AppendRow(context, path, rows, field, definitionPath, correction);
                }

                if (segment.Index.Value >= rows.Count)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' has no row at index {segment.Index.Value}.");
                }

                if (rest.Count == 0)
                {
                    if (op != CorrectionOps.Remove)
                    {
                        throw new CanonicalPatchException(path, $"A row is corrected field by field ({here}.<field>); remove it with op 'remove'.");
                    }

                    var removed = rows[segment.Index.Value];
                    rows.RemoveAt(segment.Index.Value);
                    return new AppliedCorrection(path, removed?.ToJsonString(), null, correction.Reason, op, correction.Source);
                }

                node = rows[segment.Index.Value] as JsonObject ?? throw new CanonicalPatchException(path, $"'{here}' is not an object.");
                prefix = $"{definitionPath}[*]";
                continue;
            }

            if (field.Kind == FieldKinds.Object)
            {
                if (segment.Index is not null || rest.Count == 0)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' is a group of fields; address one of its fields.");
                }

                if (node[segment.Name] is not JsonObject group)
                {
                    group = [];
                    node[segment.Name] = group;
                }

                node = group;
                prefix = definitionPath;
                continue;
            }

            if (!field.Provenance || field.ReadOnly)
            {
                throw new CanonicalPatchException(path, $"'{segment.Name}' carries no provenance or is read-only and cannot be corrected through this endpoint.");
            }

            if (field.IsList)
            {
                return ListEntry(context, path, op, node, field, segment, rest, correction);
            }

            if (segment.Index is not null)
            {
                throw new CanonicalPatchException(path, $"'{segment.Name}' is a single field, not a list.");
            }

            if (op != CorrectionOps.Set)
            {
                throw new CanonicalPatchException(path, $"'{segment.Name}' is a single field; only 'set' applies to it.");
            }

            var (envelope, record) = Correct(context, path, field, node[segment.Name], rest, correction);
            node[segment.Name] = envelope;
            return record;
        }

        throw new CanonicalPatchException(path, "The path does not address a field.");
    }

    /// <summary>
    /// <c>move</c>: the value of <c>from</c> into <paramref name="path"/> (converted to the target's kind, with the place it
    /// was read from unless the correction names one), and <c>from</c> left Missing; both keep their history.
    /// </summary>
    private static AppliedCorrection Move(Context context, string path, FieldCorrection correction)
    {
        var from = correction.From?.Trim();
        if (string.IsNullOrEmpty(from))
        {
            throw new CanonicalPatchException(path, "A move names the field the value comes from in 'from'.");
        }

        if (correction.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw new CanonicalPatchException(path, "A move takes its value from the field 'from' names; send no value.");
        }

        if (string.Equals(from, path, StringComparison.Ordinal))
        {
            throw new CanonicalPatchException(path, "A field cannot be moved onto itself.");
        }

        var source = Resolve(context, from, create: false);
        var sourceNode = source.Get();
        var moved = DefinitionTree.IsEnvelope(sourceNode) && sourceNode!["value"] is { } value && !IsWithoutValue(sourceNode)
            ? value
            : throw new CanonicalPatchException(from, $"'{from}' has no value to move.");
        var sourceField = ReadEnvelope(from, sourceNode);

        var target = Resolve(context, path, create: true);
        var targetNode = target.Get();
        var oldValue = DefinitionTree.IsEnvelope(targetNode) ? targetNode!["value"] : null;
        JsonNode? newValue;
        try
        {
            newValue = Convert(context, path, target.Field, MovableValue(source.Field, target.Field, moved), oldValue, target.Row);
        }
        catch (CanonicalPatchException ex)
        {
            throw new CanonicalPatchException(path, $"The value of '{from}' does not fit '{path}': {ex.Message}");
        }

        var location = CorrectionSources.ToFieldSource(correction.Source) ?? sourceField.Source;
        var corrected = CorrectEnvelope(context, path, targetNode, newValue, location is { Page: null, BBox: null, RawText: null } ? null : location);
        target.Set(corrected);
        source.Set(MovedOut(context, sourceField));
        return new AppliedCorrection(path, oldValue?.ToJsonString(), corrected["value"]?.ToJsonString(), correction.Reason, CorrectionOps.Move, correction.Source, from);
    }

    private static bool IsWithoutValue(JsonNode envelope) =>
        envelope["status"] is JsonValue status && status.TryGetValue<string>(out var text)
        && (string.Equals(text, nameof(FieldStatus.Missing), StringComparison.OrdinalIgnoreCase) || string.Equals(text, nameof(FieldStatus.Unreadable), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The moved value in a shape the target's kind takes: an identifier's verbatim text for any other kind, an amount's
    /// number for a plain number, the text of a number or a yes/no for a string, anything else as stored.
    /// </summary>
    private static JsonElement MovableValue(FieldDescriptor sourceField, FieldDescriptor targetField, JsonNode value)
    {
        JsonNode adapted = value;
        if (sourceField.Kind == FieldKinds.Identifier && targetField.Kind != FieldKinds.Identifier && value["verbatim"] is JsonValue verbatim)
        {
            adapted = verbatim;
        }
        else if (sourceField.Kind == FieldKinds.Money && targetField.Kind != FieldKinds.Money && value["amount"] is JsonValue amount)
        {
            adapted = amount;
        }

        if (targetField.Kind is FieldKinds.String or FieldKinds.Text && adapted is JsonValue scalar && scalar.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            adapted = JsonValue.Create(scalar.ToJsonString());
        }

        using var document = JsonDocument.Parse(adapted.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>The field a value moved out of: its earlier state in the history, Missing now, the reviewer's provenance.</summary>
    private static JsonObject MovedOut(Context context, FieldValue<JsonNode> field)
    {
        var moved = field.Correct(null, context.By, context.At) with { Status = FieldStatus.Missing, Confidence = null };
        return (JsonObject)JsonSerializer.SerializeToNode(moved, CanonicalJson.Options)!;
    }

    private static FieldValue<JsonNode> ReadEnvelope(string path, JsonNode? stored)
    {
        try
        {
            return stored is JsonObject envelope && DefinitionTree.IsEnvelope(envelope)
                ? envelope.Deserialize<FieldValue<JsonNode>>(CanonicalJson.Options) ?? FieldValue<JsonNode>.Missing()
                : FieldValue<JsonNode>.Missing();
        }
        catch (JsonException ex)
        {
            throw new CanonicalPatchException(path, $"The stored field could not be read: {ex.Message}");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new CanonicalPatchException(path, $"The stored field could not be read: {ex.Message}");
        }
    }

    /// <summary>Where one field envelope of a move sits: a property of an object (the root, a group, a row) or an entry of a list.</summary>
    private sealed record Slot(JsonObject? Owner, string? Key, JsonArray? List, int Index, FieldDescriptor Field, JsonObject Row)
    {
        public JsonNode? Get() => Owner is not null ? Owner[Key!] : List![Index];

        public void Set(JsonNode value)
        {
            if (Owner is not null)
            {
                Owner[Key!] = value;
            }
            else
            {
                List![Index] = value;
            }
        }
    }

    /// <summary>
    /// The field envelope a move reads or writes: a single field (<c>issueDate</c>, <c>totals.net</c>), a list entry
    /// (<c>orderReferences[0]</c>) or a field of a row (<c>lines[2].price</c>). A member inside a composite value and a
    /// bag attribute are not moved (they are corrected with <c>set</c>). <paramref name="create"/> makes a missing group.
    /// </summary>
    private static Slot Resolve(Context context, string path, bool create)
    {
        var segments = ParsePath(path);
        var node = context.Root;
        string? prefix = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var last = i == segments.Count - 1;
            var definitionPath = prefix is null ? segment.Name : $"{prefix}.{segment.Name}";
            var field = context.Descriptors.Find(definitionPath) ?? throw new CanonicalPatchException(path, $"'{segment.Name}' is not a field of the document type.");
            switch (field.Kind)
            {
                case FieldKinds.Bag:
                    throw new CanonicalPatchException(path, "An attribute of a bag is not moved; set it instead.");
                case FieldKinds.Array:
                    if (segment.Index is not { } row || last)
                    {
                        throw new CanonicalPatchException(path, $"A move addresses a field of a row ({segment.Name}[n].<field>), not the repeating group.");
                    }

                    var rows = node[segment.Name] as JsonArray;
                    node = rows is not null && row < rows.Count && rows[row] is JsonObject rowObject
                        ? rowObject
                        : throw new CanonicalPatchException(path, $"'{segment.Name}' has no row at index {row}.");
                    prefix = $"{definitionPath}[*]";
                    continue;
                case FieldKinds.Object:
                    if (segment.Index is not null || last)
                    {
                        throw new CanonicalPatchException(path, $"'{segment.Name}' is a group of fields; address one of its fields.");
                    }

                    if (node[segment.Name] is not JsonObject group)
                    {
                        if (!create)
                        {
                            throw new CanonicalPatchException(path, $"'{path}' has no value to move.");
                        }

                        group = [];
                        node[segment.Name] = group;
                    }

                    node = group;
                    prefix = definitionPath;
                    continue;
            }

            if (!last)
            {
                throw new CanonicalPatchException(path, "A value inside a composite field is not moved; move the whole field, or set the value.");
            }

            if (!field.Provenance || field.ReadOnly)
            {
                throw new CanonicalPatchException(path, $"'{segment.Name}' carries no provenance or is read-only and cannot be corrected through this endpoint.");
            }

            if (field.IsList)
            {
                var list = node[segment.Name] as JsonArray;
                return segment.Index is { } entry && list is not null && entry < list.Count
                    ? new Slot(null, null, list, entry, field, node)
                    : throw new CanonicalPatchException(path, segment.Index is null ? $"'{segment.Name}' is a list; address an entry as {segment.Name}[n]." : $"'{segment.Name}' has no entry at index {segment.Index}.");
            }

            if (segment.Index is not null)
            {
                throw new CanonicalPatchException(path, $"'{segment.Name}' is a single field, not a list.");
            }

            return new Slot(node, segment.Name, null, 0, field, node);
        }

        throw new CanonicalPatchException(path, "The path does not address a field.");
    }

    private static AppliedCorrection Bag(Context context, string path, string op, JsonObject node, FieldDescriptor field, PathSegment segment, List<PathSegment> rest, FieldCorrection correction)
    {
        if (segment.Index is not null || rest.Count != 1 || rest[0].Index is not null)
        {
            throw new CanonicalPatchException(path, $"'{segment.Name}' holds attributes by key; address one as {segment.Name}.<key>.");
        }

        if (node[segment.Name] is not JsonObject bag)
        {
            bag = [];
            node[segment.Name] = bag;
        }

        var key = rest[0].Name;
        switch (op)
        {
            case CorrectionOps.Remove:
                if (!bag.TryGetPropertyValue(key, out var old))
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' has no attribute '{key}'.");
                }

                bag.Remove(key);
                return new AppliedCorrection(path, DefinitionTree.ValueOf(old)?.ToJsonString(), null, correction.Reason, op, correction.Source);
            case CorrectionOps.Set:
                var value = correction.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element
                    ? element.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                        ? JsonNode.Parse(element.GetRawText())
                        : throw new CanonicalPatchException(path, "A bag attribute is a single string, number or boolean.")
                    : null;
                var current = bag[key];
                var corrected = CorrectEnvelope(context, path, current, value, correction);
                bag[key] = corrected;
                return new AppliedCorrection(path, DefinitionTree.ValueOf(current)?.ToJsonString(), corrected["value"]?.ToJsonString(), correction.Reason, op, correction.Source);
            default:
                throw new CanonicalPatchException(path, $"An attribute is set or removed; '{op}' does not apply to it.");
        }
    }

    private static AppliedCorrection AppendRow(Context context, string path, JsonArray rows, FieldDescriptor field, string definitionPath, FieldCorrection correction)
    {
        var max = field.MaxItems ?? DefaultMaxRows;
        if (rows.Count >= max)
        {
            throw new CanonicalPatchException(path, $"'{field.Key}' already has {rows.Count} rows, the most it may have.");
        }

        var row = DefinitionTree.EmptyRow(context.Descriptors, definitionPath);
        if (context.Descriptors.Find($"{definitionPath}[*].{LineNumberKey}") is { Provenance: false, Kind: FieldKinds.Integer })
        {
            // The line number is a bare member no correction sets, and the coded records' lines require it (an invoice
            // tree without it is one the Invoice record cannot read): the next one.
            row[LineNumberKey] = NextLineNumber(rows);
        }

        if (correction.Value is { ValueKind: JsonValueKind.Object } values)
        {
            foreach (var property in values.EnumerateObject())
            {
                var childPath = $"{definitionPath}[*].{property.Name}";
                var child = context.Descriptors.Find(childPath);
                if (child is null || !child.IsEnvelope || child.ReadOnly || child.IsList)
                {
                    throw new CanonicalPatchException(path, $"'{property.Name}' is not a field a new row of '{field.Key}' can be given.");
                }

                var value = Convert(context, $"{path}.{property.Name}", child, property.Value, null, row);
                row[property.Name] = CorrectEnvelope(context, path, row[property.Name], value, correction);
            }
        }
        else if (correction.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw new CanonicalPatchException(path, "A new row is given as an object of its field values, or without a value for an empty row.");
        }

        rows.Add(row);
        return new AppliedCorrection($"{path}[{(rows.Count - 1).ToString(CultureInfo.InvariantCulture)}]", null, row.ToJsonString(), correction.Reason, CorrectionOps.Append, correction.Source);
    }

    private const string LineNumberKey = "lineNumber";

    private static long NextLineNumber(JsonArray rows)
    {
        long highest = 0;
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (row[LineNumberKey] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var number))
            {
                highest = Math.Max(highest, number);
            }
        }

        return Math.Max(highest, rows.Count) + 1;
    }

    private static AppliedCorrection ListEntry(Context context, string path, string op, JsonObject node, FieldDescriptor field, PathSegment segment, List<PathSegment> rest, FieldCorrection correction)
    {
        if (rest.Count > 0)
        {
            throw new CanonicalPatchException(path, "An entry of a list is corrected as a whole.");
        }

        var list = ArrayOf(node, segment.Name);
        if (segment.Index is null)
        {
            if (op != CorrectionOps.Append)
            {
                throw new CanonicalPatchException(path, $"'{segment.Name}' is a list; address an entry as {segment.Name}[n], or append one with op 'append'.");
            }

            var value = Convert(context, path, field, correction.Value, null, node) ?? throw new CanonicalPatchException(path, "An appended entry needs a value.");
            var entry = CorrectEnvelope(context, path, null, value, correction);
            list.Add(entry);
            return new AppliedCorrection($"{path}[{(list.Count - 1).ToString(CultureInfo.InvariantCulture)}]", null, entry["value"]?.ToJsonString(), correction.Reason, op, correction.Source);
        }

        if (segment.Index.Value >= list.Count)
        {
            throw new CanonicalPatchException(path, $"'{segment.Name}' has no entry at index {segment.Index.Value}.");
        }

        if (op == CorrectionOps.Remove)
        {
            var removed = list[segment.Index.Value];
            list.RemoveAt(segment.Index.Value);
            return new AppliedCorrection(path, DefinitionTree.ValueOf(removed)?.ToJsonString(), null, correction.Reason, op, correction.Source);
        }

        if (op != CorrectionOps.Set)
        {
            throw new CanonicalPatchException(path, $"An entry of a list is set or removed; '{op}' does not apply to it.");
        }

        var (envelope, record) = Correct(context, path, field, list[segment.Index.Value], [], correction);
        list[segment.Index.Value] = envelope;
        return record;
    }

    private static (JsonObject Envelope, AppliedCorrection Record) Correct(Context context, string path, FieldDescriptor field, JsonNode? current, List<PathSegment> nested, FieldCorrection correction)
    {
        var oldValue = DefinitionTree.IsEnvelope(current) ? current!["value"] : null;
        var newValue = nested.Count > 0
            ? Member(context, path, field, oldValue, nested, correction.Value)
            : Convert(context, path, field, correction.Value, oldValue, null);
        if (newValue is null && context.TypedRecord && field.Kind is FieldKinds.Date or FieldKinds.Integer or FieldKinds.Decimal or FieldKinds.Percent or FieldKinds.Boolean or FieldKinds.Enum or FieldKinds.Unit)
        {
            throw new CanonicalPatchException(path, $"A {field.Kind} field cannot be cleared; provide a value.");
        }

        var envelope = CorrectEnvelope(context, path, current, newValue, correction);
        return (envelope, new AppliedCorrection(path, oldValue?.ToJsonString(), envelope["value"]?.ToJsonString(), correction.Reason, CorrectionOps.Set, correction.Source));
    }

    /// <summary>The corrected envelope: the stored one (or Missing) read as <c>FieldValue&lt;JsonNode&gt;</c>, through <see cref="FieldValue{T}.Correct"/>.</summary>
    private static JsonObject CorrectEnvelope(Context context, string path, JsonNode? current, JsonNode? newValue, FieldCorrection correction) =>
        CorrectEnvelope(context, path, current, newValue, CorrectionSources.ToFieldSource(correction.Source));

    private static JsonObject CorrectEnvelope(Context context, string path, JsonNode? current, JsonNode? newValue, FieldSource? source)
    {
        var field = ReadEnvelope(path, current);
        var corrected = field.Correct(newValue?.DeepClone(), context.By, context.At, source);
        return (JsonObject)JsonSerializer.SerializeToNode(corrected, CanonicalJson.Options)!;
    }

    /// <summary>A member inside a composite value (<c>supplier.vatId</c>, <c>supplier.address.city</c>): set on a copy of the current value.</summary>
    private static JsonObject Member(Context context, string path, FieldDescriptor field, JsonNode? current, List<PathSegment> nested, JsonElement? value)
    {
        if (field.Kind == FieldKinds.Identifier)
        {
            throw new CanonicalPatchException(path, "An identifier is corrected as a whole: send the new verbatim text for the field; the normalized form is derived from it.");
        }

        if (!FieldKinds.CompositeMemberKinds.ContainsKey(field.Kind))
        {
            throw new CanonicalPatchException(path, $"'{nested[0].Name}' cannot be addressed inside a {field.Kind} value.");
        }

        var target = current?.DeepClone() as JsonObject ?? [];
        var container = target;
        var kind = field.Kind;
        for (var i = 0; i < nested.Count; i++)
        {
            var segment = nested[i];
            if (segment.Index is not null)
            {
                throw new CanonicalPatchException(path, "Lists inside a field value cannot be addressed by index; correct the whole field.");
            }

            if (!FieldKinds.CompositeMemberKinds.TryGetValue(kind, out var members) || members.All(m => m.Member != segment.Name))
            {
                throw new CanonicalPatchException(path, $"'{segment.Name}' is not a property of a {kind} value.");
            }

            var memberKind = members.First(m => m.Member == segment.Name).Kind;
            if (i == nested.Count - 1)
            {
                var member = new FieldDescriptor { Path = path, FieldId = field.FieldId, Key = segment.Name, Kind = memberKind };
                container[segment.Name] = Convert(context, path, member, value, container[segment.Name], null);
                break;
            }

            if (container[segment.Name] is not JsonObject child)
            {
                child = [];
                container[segment.Name] = child;
            }

            container = child;
            kind = memberKind;
        }

        return target;
    }

    /// <summary>
    /// The new value in the stored shape of the field's kind, with the reviewers' shorthands: a bare string for an
    /// identifier (its normalised form is always derived), a bare number for an amount (currency from the stored value,
    /// the field's <c>currencyFrom</c>, the document or the tenant default), a currency code normalised and checked.
    /// </summary>
    private static JsonNode? Convert(Context context, string path, FieldDescriptor field, JsonElement? value, JsonNode? oldValue, JsonObject? row)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var element = value.Value;
        switch (field.Kind)
        {
            case FieldKinds.Identifier:
                var text = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Object when element.TryGetProperty("verbatim", out var verbatim) && verbatim.ValueKind == JsonValueKind.String => verbatim.GetString(),
                    _ => throw new CanonicalPatchException(path, "An identifier is a string (or an object with a verbatim property)."),
                };
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new CanonicalPatchException(path, "An identifier cannot be blank; send null to clear the field.");
                }

                return JsonSerializer.SerializeToNode(DocumentIdentifier.Create(text), CanonicalJson.Options);

            case FieldKinds.Money:
                decimal amount;
                string? currency;
                if (element.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                {
                    amount = Decimal(path, element, "The amount is not a decimal number.");
                    currency = Text(oldValue?["currency"]) ?? CurrencyOf(context, field, row) ?? context.FallbackCurrency;
                    if (string.IsNullOrWhiteSpace(currency))
                    {
                        throw new CanonicalPatchException(path, "The document has no currency; send {\"amount\": …, \"currency\": \"EUR\"} instead of a bare number.");
                    }
                }
                else if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("amount", out var amountElement))
                {
                    amount = Decimal(path, amountElement, "The amount is not a decimal number.");
                    currency = element.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : CurrencyOf(context, field, row) ?? context.FallbackCurrency;
                }
                else
                {
                    throw new CanonicalPatchException(path, "An amount is a number, or {\"amount\": …, \"currency\": \"EUR\"}.");
                }

                return JsonSerializer.SerializeToNode(new Money(amount, NormalizeCurrency(path, currency)), CanonicalJson.Options);

            case FieldKinds.Currency:
                return JsonValue.Create(NormalizeCurrency(path, element.ValueKind == JsonValueKind.String ? element.GetString() : null));

            case FieldKinds.Date:
                if (element.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    throw new CanonicalPatchException(path, "A date is a string of the form yyyy-MM-dd.");
                }

                return JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            case FieldKinds.Integer:
                var integer = Decimal(path, element, "The value is not a whole number.");
                if (integer != decimal.Truncate(integer) || integer is > long.MaxValue or < long.MinValue)
                {
                    throw new CanonicalPatchException(path, "The value is not a whole number.");
                }

                return JsonValue.Create(decimal.ToInt64(integer));

            case FieldKinds.Decimal or FieldKinds.Percent:
                return JsonValue.Create(Decimal(path, element, "The value is not a decimal number."));

            case FieldKinds.Boolean:
                return element.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? JsonValue.Create(element.GetBoolean())
                    : throw new CanonicalPatchException(path, "The value is true or false.");

            case FieldKinds.Enum:
                var code = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                if (string.IsNullOrWhiteSpace(code))
                {
                    throw new CanonicalPatchException(path, "A code is a non-empty string.");
                }

                if (field.Options is { Count: > 0 } options && !options.Contains(code, StringComparer.Ordinal))
                {
                    throw new CanonicalPatchException(path, $"'{code}' is not one of {string.Join(", ", options)}.");
                }

                return JsonValue.Create(code);

            case FieldKinds.Unit when context.TypedRecord:
                return JsonValue.Create(element.ValueKind == JsonValueKind.String
                    ? UnitOfMeasureName(path, element.GetString())
                    : throw new CanonicalPatchException(path, "The value is a string."));

            case FieldKinds.String or FieldKinds.Text or FieldKinds.Unit:
                return element.ValueKind == JsonValueKind.String
                    ? JsonValue.Create(element.GetString())
                    : throw new CanonicalPatchException(path, "The value is a string.");

            case FieldKinds.Period:
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new CanonicalPatchException(path, "A period is {\"from\": \"yyyy-MM-dd\", \"to\": \"yyyy-MM-dd\"}.");
                }

                var period = new JsonObject();
                foreach (var end in new[] { "from", "to" })
                {
                    if (element.TryGetProperty(end, out var part) && part.ValueKind != JsonValueKind.Null)
                    {
                        period[end] = Convert(context, path, new FieldDescriptor { Path = path, FieldId = field.FieldId, Key = end, Kind = FieldKinds.Date }, part, null, null);
                    }
                }

                return period;

            default:
                if (!FieldKinds.CompositeMemberKinds.TryGetValue(field.Kind, out var members))
                {
                    throw new CanonicalPatchException(path, $"A {field.Kind} field cannot be corrected through this endpoint.");
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new CanonicalPatchException(path, $"A {field.Kind} value is an object of its members ({string.Join(", ", members.Select(m => m.Member))}).");
                }

                var composite = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    var member = members.FirstOrDefault(m => m.Member == property.Name);
                    if (member.Member is null)
                    {
                        throw new CanonicalPatchException(path, $"'{property.Name}' is not a property of a {field.Kind} value.");
                    }

                    composite[property.Name] = Convert(context, path, new FieldDescriptor { Path = path, FieldId = field.FieldId, Key = property.Name, Kind = member.Kind }, property.Value, null, null);
                }

                return composite;
        }
    }

    /// <summary>
    /// A unit as the coded record holds it: the <see cref="UnitOfMeasure"/> name, from the name itself, a UN/ECE code of
    /// the platform <c>uom</c> list (<c>KGM</c>) or a printed alias it maps (<c>kg</c>); anything else is refused, because
    /// the record could not read it.
    /// </summary>
    private static string UnitOfMeasureName(string path, string? text)
    {
        var trimmed = text?.Trim();
        if (string.Equals(trimmed, nameof(UnitOfMeasure.Other), StringComparison.OrdinalIgnoreCase))
        {
            return nameof(UnitOfMeasure.Other);
        }

        var code = CodeLists.Platform[CodeLists.Uom].Resolve(trimmed);
        foreach (var (unit, unece) in CodeLists.UneceCodes)
        {
            if (code is not null && string.Equals(unece, code, StringComparison.Ordinal))
            {
                return unit.ToString();
            }
        }

        throw new CanonicalPatchException(path, $"'{text}' is not a unit of an invoice line; use one of {string.Join(", ", Enum.GetNames<UnitOfMeasure>())}, or its UN/ECE code.");
    }

    /// <summary>The value of the field's <c>currencyFrom</c> (or the document's <c>currency</c>): looked up in the row first, then at the top.</summary>
    private static string? CurrencyOf(Context context, FieldDescriptor field, JsonObject? row)
    {
        var name = field.CurrencyFrom ?? "currency";
        var key = name.Contains('.', StringComparison.Ordinal) ? name[(name.LastIndexOf('.') + 1)..] : name;
        return Text(DefinitionTree.ValueOf(row?[key])) ?? Text(DefinitionTree.ValueOf(context.Root[name])) ?? Text(DefinitionTree.ValueOf(context.Root["currency"]));
    }

    private static string NormalizeCurrency(string path, string? code)
    {
        var normalized = code is null ? null : CurrencyCode.Normalize(code);
        if (!CurrencyCode.IsWellFormed(normalized))
        {
            throw new CanonicalPatchException(path, $"'{code}' is not a currency; use the three-letter ISO 4217 code (EUR, USD, …).");
        }

        return normalized!;
    }

    private static decimal Decimal(string path, JsonElement element, string message)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new CanonicalPatchException(path, message);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static JsonArray ArrayOf(JsonObject node, string name)
    {
        if (node[name] is JsonArray array)
        {
            return array;
        }

        array = [];
        node[name] = array;
        return array;
    }

    private static List<PathSegment> ParsePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CanonicalPatchException(path, "A field path is required.");
        }

        var segments = new List<PathSegment>();
        foreach (var part in path.Split('.'))
        {
            var match = SegmentPattern().Match(part);
            if (!match.Success)
            {
                throw new CanonicalPatchException(path, $"'{part}' is not a valid path segment; expected name or name[index].");
            }

            int? index = match.Groups["index"].Success ? int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture) : null;
            segments.Add(new PathSegment(match.Groups["name"].Value, index));
        }

        return segments;
    }

    /// <summary>The <c>.value</c> segments of an envelope in a JSON path (<c>lines[2].unit.value</c> is the field <c>lines[2].unit</c>).</summary>
    [GeneratedRegex(@"\.value(?=\.|$)", RegexOptions.CultureInvariant)]
    private static partial Regex EnvelopeValue();

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_-]*)(?:\[(?<index>\d{1,6})\])?$")]
    private static partial Regex SegmentPattern();

    private readonly record struct PathSegment(string Name, int? Index)
    {
        public override string ToString() => Index is { } i ? $"{Name}[{i.ToString(CultureInfo.InvariantCulture)}]" : Name;
    }
}
