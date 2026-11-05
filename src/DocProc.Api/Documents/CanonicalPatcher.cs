using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Application.Documents;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Infrastructure.Persistence;

namespace DocProc.Api.Documents;

/// <summary>A correction that could not be applied; <see cref="Path"/> names the offending entry for the validation problem.</summary>
public sealed class CanonicalPatchException : Exception
{
    public CanonicalPatchException()
        : this(string.Empty, "The correction could not be applied.")
    {
    }

    public CanonicalPatchException(string message)
        : this(string.Empty, message)
    {
    }

    public CanonicalPatchException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Path = string.Empty;
    }

    public CanonicalPatchException(string path, string message)
        : base(message)
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// What one correction changed: the field's value before and after, as JSON, for the <c>Corrections</c> audit table,
/// with the operation and the place the value was taken from (<see cref="CorrectionSource"/>, null when not given).
/// </summary>
public sealed record AppliedCorrection(string Path, string? OldValueJson, string? NewValueJson, string? Reason, string Op = CorrectionOps.Set, CorrectionSource? Source = null, string? From = null);

/// <summary>
/// The correction operations of docs/11 §6.2. The coded records' patcher (<see cref="CanonicalPatcher"/>) applies
/// <see cref="Set"/> and <see cref="Move"/>; the definition patcher (<see cref="DefinitionPatcher"/>, definition-driven
/// documents) also <see cref="Append"/> and <see cref="Remove"/>. <see cref="SetType"/>, <see cref="Split"/> and
/// <see cref="Join"/> are the ops of the Correction rows the review actions write (<c>:set-type</c>, <c>:split</c>,
/// <c>:join</c>); a correction naming one is told which action to use.
/// </summary>
public static class CorrectionOps
{
    public const string Set = "set";
    public const string Append = "append";
    public const string Remove = "remove";
    public const string Move = "move";
    public const string SetType = "setType";
    public const string Split = "split";
    public const string Join = "join";

    /// <summary>Every op a Correction row can carry.</summary>
    public static readonly IReadOnlyList<string> Planned = [Append, Remove, Move, SetType, Split, Join];

    /// <summary>The review action that writes each op that is not a field correction.</summary>
    public static readonly IReadOnlyDictionary<string, string> ReviewActions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SetType] = "POST /documents/{id}:set-type",
        [Split] = "POST /documents/{id}:split",
        [Join] = "POST /documents/{id}:join",
    };

    /// <summary>The message for an op the patcher at hand does not apply (<paramref name="applied"/> lists the ones it does).</summary>
    public static string NotApplied(string op, IReadOnlyList<string> applied) =>
        ReviewActions.TryGetValue(op, out var action) ? $"'{op}' is not a field correction; use {action}."
        : Planned.Contains(op, StringComparer.Ordinal) ? $"The correction operation '{op}' does not apply to this document; only '{string.Join("', '", applied)}' do."
        : $"'{op}' is not a correction operation; use one of '{string.Join("', '", applied)}'.";
}

public sealed record CanonicalPatchResult(string Json, IReadOnlyList<AppliedCorrection> Applied);

/// <summary>
/// Applies reviewer corrections to the stored canonical JSON by path (docs/05 "PATCH /documents/{id}/fields"). The
/// walker follows the path on the document's field schema (<see cref="IFieldSchemaResolver"/>, so only real fields
/// are reachable and values are typed) but edits the JSON tree, so every field that is not corrected stays
/// byte-for-byte as the pipeline wrote it. The addressed <see cref="FieldValue{T}"/> is deserialised, corrected through
/// <see cref="FieldValue{T}.Correct"/> (previous value and provenance go to its history) and written back.
/// </summary>
public static partial class CanonicalPatcher
{
    /// <summary>The domain record a document's canonical JSON holds, or null for types without canonical data.</summary>
    public static Type? RootTypeFor(DocumentTypeKey type) => ReflectionFieldSchemaResolver.RootTypeFor(type);

    /// <summary>
    /// <see cref="Apply(FieldSchemaNode, string, IReadOnlyList{FieldCorrection}, string, DateTimeOffset, string?)"/> on
    /// the reflection schema of a domain record (<see cref="RootTypeFor"/>).
    /// </summary>
    public static CanonicalPatchResult Apply(Type rootType, string canonicalJson, IReadOnlyList<FieldCorrection> corrections, string by, DateTimeOffset at, string? fallbackCurrency = null)
    {
        ArgumentNullException.ThrowIfNull(rootType);
        return Apply(ReflectionFieldSchemaResolver.Instance.NodeFor(rootType), canonicalJson, corrections, by, at, fallbackCurrency);
    }

    /// <summary>
    /// Applies every correction in order and returns the new JSON. Throws <see cref="CanonicalPatchException"/> for an
    /// unknown path, an index past the end of a list, a value of the wrong shape, or an attempt to clear a field whose
    /// type has no empty value; nothing is partially applied in that case.
    /// </summary>
    /// <param name="schema">The schema of the document's tree (<see cref="IFieldSchemaResolver.RootFor"/>).</param>
    /// <param name="canonicalJson">The stored canonical JSON.</param>
    /// <param name="corrections">What to change.</param>
    /// <param name="by">The reviewer, recorded in each field's history.</param>
    /// <param name="at">When, recorded in each field's history.</param>
    /// <param name="fallbackCurrency">Currency for a bare amount when neither the field nor the document carries one (the tenant default).</param>
    public static CanonicalPatchResult Apply(FieldSchemaNode schema, string canonicalJson, IReadOnlyList<FieldCorrection> corrections, string by, DateTimeOffset at, string? fallbackCurrency = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        if (JsonNode.Parse(canonicalJson) is not JsonObject root)
        {
            throw new CanonicalPatchException(string.Empty, "The stored canonical document is not a JSON object.");
        }

        var documentCurrency = root["currency"]?["value"]?.GetValue<string>() ?? fallbackCurrency;
        var applied = new List<AppliedCorrection>(corrections.Count);
        foreach (var correction in corrections)
        {
            applied.Add(ApplyOne(schema, root, correction, by, at, documentCurrency));
        }

        return new CanonicalPatchResult(root.ToJsonString(), applied);
    }

    private static AppliedCorrection ApplyOne(FieldSchemaNode schema, JsonObject root, FieldCorrection correction, string by, DateTimeOffset at, string? documentCurrency)
    {
        var path = correction.Path?.Trim() ?? string.Empty;
        CheckOp(path, correction.Op);
        CorrectionSources.Check(path, correction.Source);
        if (correction.Op == CorrectionOps.Move)
        {
            return Move(schema, root, path, correction, by, at, documentCurrency);
        }

        if (correction.From is not null)
        {
            throw new CanonicalPatchException(path, $"'from' belongs to op '{CorrectionOps.Move}' only.");
        }

        var segments = ParsePath(path);
        JsonObject node = root;
        var shape = schema;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var member = shape.Member(segment.Name) ?? throw new CanonicalPatchException(path, $"'{segment.Name}' is not a field of {shape.Name}.");
            var remaining = segments.Skip(i + 1).ToList();

            if (member.Kind == FieldSchemaMemberKind.Field)
            {
                if (segment.Index is not null)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' is a single field, not a list.");
                }

                var (newNode, record) = Correct(path, member, node[segment.Name], remaining, correction, by, at, documentCurrency);
                node[segment.Name] = newNode;
                return record;
            }

            if (member is { Kind: FieldSchemaMemberKind.List, Element: { } element })
            {
                if (segment.Index is null)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' is a list; address an entry as {segment.Name}[n].");
                }

                if (node[segment.Name] is not JsonArray array || segment.Index.Value >= array.Count)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' has no entry at index {segment.Index.Value}.");
                }

                if (element.Kind == FieldSchemaMemberKind.Field)
                {
                    var (newNode, record) = Correct(path, element, array[segment.Index.Value], remaining, correction, by, at, documentCurrency);
                    array[segment.Index.Value] = newNode;
                    return record;
                }

                node = array[segment.Index.Value] as JsonObject ?? throw new CanonicalPatchException(path, $"'{segment.Name}[{segment.Index.Value}]' is not an object.");
                shape = element.Shape;
                continue;
            }

            if (member.Kind == FieldSchemaMemberKind.Group)
            {
                if (segment.Index is not null)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' is not a list.");
                }

                node = node[segment.Name] as JsonObject ?? throw new CanonicalPatchException(path, $"'{segment.Name}' is empty; it cannot be navigated into.");
                shape = member.Shape;
                continue;
            }

            throw new CanonicalPatchException(path, $"'{segment.Name}' carries no provenance and cannot be corrected through this endpoint.");
        }

        throw new CanonicalPatchException(path, "The path does not address a field.");
    }

    /// <summary>Deserialises the field, applies the correction and returns the new field node plus the audit record.</summary>
    private static (JsonNode NewNode, AppliedCorrection Record) Correct(string path, FieldSchemaMember field, JsonNode? fieldNode, List<PathSegment> nested, FieldCorrection correction, string by, DateTimeOffset at, string? documentCurrency)
    {
        var fieldType = field.ClrType;
        var valueType = field.ValueType!;
        var options = CanonicalDocumentSerializer.Options;
        object current;
        try
        {
            current = fieldNode is null
                ? Activator.CreateInstance(fieldType)!
                : JsonSerializer.Deserialize(fieldNode, fieldType, options) ?? Activator.CreateInstance(fieldType)!;
        }
        catch (JsonException ex)
        {
            throw new CanonicalPatchException(path, $"The stored field could not be read: {ex.Message}");
        }

        var oldValueNode = fieldNode?["value"];
        var newValue = nested.Count > 0
            ? NestedValue(path, field, oldValueNode, nested, correction.Value)
            : ConvertValue(path, valueType, correction.Value, oldValueNode, documentCurrency);
        newValue = Validate(path, newValue, field.IsCurrencyCode);

        if (newValue is null && valueType.IsValueType && Nullable.GetUnderlyingType(valueType) is null)
        {
            throw new CanonicalPatchException(path, $"A {valueType.Name} field cannot be cleared; provide a value.");
        }

        var correct = fieldType.GetMethod(nameof(FieldValue<object>.Correct), BindingFlags.Public | BindingFlags.Instance)!;
        object corrected;
        try
        {
            corrected = correct.Invoke(current, [newValue, by, at, CorrectionSources.ToFieldSource(correction.Source)])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new CanonicalPatchException(path, ex.InnerException.Message);
        }

        var newNode = JsonSerializer.SerializeToNode(corrected, fieldType, options)!;
        return (newNode, new AppliedCorrection(path, oldValueNode?.ToJsonString(), newNode["value"]?.ToJsonString(), correction.Reason, CorrectionOps.Set, correction.Source));
    }

    /// <summary>The operations this patcher applies: a coded record's lists and rows are fixed by its type, so neither append nor remove.</summary>
    public static readonly IReadOnlyList<string> Operations = [CorrectionOps.Set, CorrectionOps.Move];

    private static void CheckOp(string path, string? op)
    {
        if (op is null || Operations.Contains(op, StringComparer.Ordinal))
        {
            return;
        }

        throw new CanonicalPatchException(path, CorrectionOps.NotApplied(op, Operations));
    }

    /// <summary>
    /// <c>move</c> (docs/11 §12.3 "match fields"): the value of the field <c>from</c> names into <paramref name="path"/>,
    /// converted to the target's type (an identifier's verbatim text for a string, an amount's number for a number) and
    /// with the place it was read from unless the correction names one; <c>from</c> is left Missing. Both keep their
    /// earlier state in their history. Either end is a single field, an entry of a list of fields or a field of a row.
    /// </summary>
    private static AppliedCorrection Move(FieldSchemaNode schema, JsonObject root, string path, FieldCorrection correction, string by, DateTimeOffset at, string? documentCurrency)
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

        var source = Resolve(schema, root, from);
        var target = Resolve(schema, root, path);
        var sourceNode = source.Get() as JsonObject;
        if (sourceNode?["value"] is not { } moved || sourceNode["status"] is JsonValue status && status.TryGetValue<string>(out var text)
            && (string.Equals(text, nameof(FieldStatus.Missing), StringComparison.OrdinalIgnoreCase) || string.Equals(text, nameof(FieldStatus.Unreadable), StringComparison.OrdinalIgnoreCase)))
        {
            throw new CanonicalPatchException(from, $"'{from}' has no value to move.");
        }

        var options = CanonicalDocumentSerializer.Options;
        object sourceField;
        try
        {
            sourceField = JsonSerializer.Deserialize(sourceNode, source.Field.ClrType, options) ?? throw new JsonException("The field is null.");
        }
        catch (JsonException ex)
        {
            throw new CanonicalPatchException(from, $"The stored field could not be read: {ex.Message}");
        }

        var location = correction.Source ?? LocationOf(((IFieldValue)sourceField).Source);
        var asSet = correction with { Value = MovableValue(source.Field.ValueType!, target.Field.ValueType!, moved), Op = CorrectionOps.Set, Source = location, From = null };
        (JsonNode NewNode, AppliedCorrection Record) result;
        try
        {
            result = Correct(path, target.Field, target.Get(), [], asSet, by, at, documentCurrency);
        }
        catch (CanonicalPatchException ex)
        {
            throw new CanonicalPatchException(path, $"The value of '{from}' does not fit '{path}': {ex.Message}");
        }

        target.Set(result.NewNode);
        source.Set(MovedOut(source.Field, sourceField, by, at));
        return result.Record with { Op = CorrectionOps.Move, Source = correction.Source, From = from };
    }

    /// <summary>The place a moved value was read from, as a correction's source; null when the field records none.</summary>
    private static CorrectionSource? LocationOf(FieldSource? source) =>
        source is null || (source.Page is null && source.BBox is null && source.RawText is null)
            ? null
            : new CorrectionSource(Page: source.Page, BBox: source.BBox is { } b ? new SourceBox(b.X0, b.Y0, b.X1, b.Y1) : null, RawText: source.RawText);

    /// <summary>The moved value in a shape the target's type reads (see <see cref="Move"/>).</summary>
    private static JsonElement MovableValue(Type sourceType, Type targetType, JsonNode value)
    {
        var target = Nullable.GetUnderlyingType(targetType) ?? targetType;
        JsonNode adapted = value;
        if (sourceType == typeof(DocumentIdentifier) && target != typeof(DocumentIdentifier) && value["verbatim"] is JsonValue verbatim)
        {
            adapted = verbatim;
        }
        else if (sourceType == typeof(Money) && target != typeof(Money) && value["amount"] is JsonValue amount)
        {
            adapted = amount;
        }

        if (target == typeof(string) && adapted is JsonValue scalar && scalar.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            adapted = JsonValue.Create(scalar.ToJsonString());
        }

        using var document = JsonDocument.Parse(adapted.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>The field a value moved out of: its earlier state in the history, Missing now (no value, no confidence), the reviewer's provenance.</summary>
    private static JsonObject MovedOut(FieldSchemaMember field, object current, string by, DateTimeOffset at)
    {
        var correct = field.ClrType.GetMethod(nameof(FieldValue<object>.Correct), BindingFlags.Public | BindingFlags.Instance)!;
        var valueType = field.ValueType!;
        var empty = valueType.IsValueType && Nullable.GetUnderlyingType(valueType) is null ? Activator.CreateInstance(valueType) : null;
        var corrected = correct.Invoke(current, [empty, by, at, null])!;
        var node = (JsonObject)JsonSerializer.SerializeToNode(corrected, field.ClrType, CanonicalDocumentSerializer.Options)!;
        node.Remove("value");
        node.Remove("confidence");
        node["status"] = nameof(FieldStatus.Missing);
        return node;
    }

    /// <summary>Where one field of a move sits: a property of an object (the root, a group, a row) or an entry of a list.</summary>
    private sealed record Slot(JsonObject? Owner, string? Key, JsonArray? List, int Index, FieldSchemaMember Field)
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

    /// <summary>The field a move reads or writes, walked on the record's schema like a <c>set</c>; a value inside a field's value is not moved.</summary>
    private static Slot Resolve(FieldSchemaNode schema, JsonObject root, string path)
    {
        var segments = ParsePath(path);
        var node = root;
        var shape = schema;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var last = i == segments.Count - 1;
            var member = shape.Member(segment.Name) ?? throw new CanonicalPatchException(path, $"'{segment.Name}' is not a field of {shape.Name}.");
            if (member.Kind == FieldSchemaMemberKind.Field)
            {
                if (segment.Index is not null)
                {
                    throw new CanonicalPatchException(path, $"'{segment.Name}' is a single field, not a list.");
                }

                return last ? new Slot(node, segment.Name, null, 0, member) : throw new CanonicalPatchException(path, "A value inside a field is not moved; move the whole field, or set the value.");
            }

            if (member is { Kind: FieldSchemaMemberKind.List, Element: { } element })
            {
                if (segment.Index is not { } index || node[segment.Name] is not JsonArray array || index >= array.Count)
                {
                    throw new CanonicalPatchException(path, segment.Index is null ? $"'{segment.Name}' is a list; address an entry as {segment.Name}[n]." : $"'{segment.Name}' has no entry at index {segment.Index}.");
                }

                if (element.Kind == FieldSchemaMemberKind.Field)
                {
                    return last ? new Slot(null, null, array, index, element) : throw new CanonicalPatchException(path, "A value inside a field is not moved; move the whole field, or set the value.");
                }

                node = array[index] as JsonObject ?? throw new CanonicalPatchException(path, $"'{segment.Name}[{index}]' is not an object.");
                shape = element.Shape;
                continue;
            }

            if (member.Kind == FieldSchemaMemberKind.Group && segment.Index is null && !last)
            {
                node = node[segment.Name] as JsonObject ?? throw new CanonicalPatchException(path, $"'{segment.Name}' is empty; it cannot be navigated into.");
                shape = member.Shape;
                continue;
            }

            throw new CanonicalPatchException(path, $"'{segment.Name}' carries no provenance and cannot be corrected through this endpoint.");
        }

        throw new CanonicalPatchException(path, "The path does not address a field.");
    }

    /// <summary>A property inside the field's value (<c>supplier.vatId</c>): patch that property on the current value and re-read the whole value.</summary>
    private static object? NestedValue(string path, FieldSchemaMember field, JsonNode? currentValueNode, List<PathSegment> nested, JsonElement? value)
    {
        var valueType = field.ValueType!;
        if (!valueType.IsClass || valueType == typeof(string))
        {
            throw new CanonicalPatchException(path, $"'{nested[0].Name}' cannot be addressed inside a {valueType.Name} value.");
        }

        if (valueType == typeof(DocumentIdentifier))
        {
            throw new CanonicalPatchException(path, "An identifier is corrected as a whole: send the new verbatim text for the field; the normalized form is derived from it.");
        }

        var target = currentValueNode?.DeepClone() as JsonObject ?? [];
        var container = target;
        var shape = field.ValueShape!;
        for (var i = 0; i < nested.Count; i++)
        {
            var segment = nested[i];
            if (segment.Index is not null)
            {
                throw new CanonicalPatchException(path, "Lists inside a field value cannot be addressed by index; correct the whole field.");
            }

            var member = shape.Member(segment.Name) ?? throw new CanonicalPatchException(path, $"'{segment.Name}' is not a property of {shape.Name}.");
            if (i == nested.Count - 1)
            {
                container[segment.Name] = ToNode(value);
                break;
            }

            if (!member.IsNavigable)
            {
                throw new CanonicalPatchException(path, $"'{segment.Name}' cannot be navigated into.");
            }

            if (container[segment.Name] is not JsonObject child)
            {
                child = [];
                container[segment.Name] = child;
            }

            container = child;
            shape = member.Shape;
        }

        try
        {
            return JsonSerializer.Deserialize(target, valueType, CanonicalDocumentSerializer.Options);
        }
        catch (JsonException ex)
        {
            throw new CanonicalPatchException(path, $"The value is not a valid {valueType.Name}: {ex.Message}");
        }
    }

    /// <summary>The new value typed as the field expects, with the shorthands reviewers use: a bare string for an identifier, a bare number for an amount.</summary>
    private static object? ConvertValue(string path, Type valueType, JsonElement? value, JsonNode? oldValueNode, string? documentCurrency)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var element = value.Value;
        if (valueType == typeof(DocumentIdentifier))
        {
            // Whatever the shape (a bare string or {verbatim, normalized}), only the verbatim text counts: the
            // normalized form is always recomputed, so a client cannot store an inconsistent pair.
            var text = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Object when element.TryGetProperty("verbatim", out var verbatim) && verbatim.ValueKind == JsonValueKind.String => verbatim.GetString(),
                JsonValueKind.Object => null,
                _ => throw new CanonicalPatchException(path, "An identifier is a string (or an object with a verbatim property)."),
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new CanonicalPatchException(path, "An identifier cannot be blank; send null to clear the field.");
            }

            return DocumentIdentifier.Create(text);
        }

        if (valueType == typeof(Money) && element.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            if (!TryReadDecimal(element, out var amount))
            {
                throw new CanonicalPatchException(path, "The amount is not a decimal number.");
            }

            var currency = oldValueNode?["currency"]?.GetValue<string>() ?? documentCurrency;
            if (string.IsNullOrWhiteSpace(currency))
            {
                throw new CanonicalPatchException(path, "The document has no currency; send {\"amount\": …, \"currency\": \"EUR\"} instead of a bare number.");
            }

            return new Money(amount, currency);
        }

        try
        {
            return JsonSerializer.Deserialize(element, valueType, CanonicalDocumentSerializer.Options);
        }
        catch (JsonException ex)
        {
            throw new CanonicalPatchException(path, $"The value is not a valid {valueType.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks the converted value against what the stored columns accept: a currency (the document's <c>currency</c>
    /// field or <see cref="Money.Currency"/>) is normalised and must be a three-letter code, because
    /// <c>Document.Currency</c> is <c>char(3)</c> and every arithmetic check compares codes verbatim; an identifier is
    /// always rebuilt from its verbatim text.
    /// </summary>
    private static object? Validate(string path, object? value, bool currencyField)
    {
        switch (value)
        {
            case string code when currencyField:
                return NormalizeCurrency(path, code);

            case Money money:
                return money with { Currency = NormalizeCurrency(path, money.Currency) };

            case DocumentIdentifier identifier:
                if (string.IsNullOrWhiteSpace(identifier.Verbatim))
                {
                    throw new CanonicalPatchException(path, "An identifier cannot be blank; send null to clear the field.");
                }

                return DocumentIdentifier.Create(identifier.Verbatim);

            default:
                return value;
        }
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

    private static bool TryReadDecimal(JsonElement element, out decimal amount)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDecimal(out amount);
        }

        return decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private static JsonNode? ToNode(JsonElement? value) =>
        value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : JsonNode.Parse(value.Value.GetRawText());

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

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\[(?<index>\d{1,6})\])?$")]
    private static partial Regex SegmentPattern();

    private readonly record struct PathSegment(string Name, int? Index);
}
