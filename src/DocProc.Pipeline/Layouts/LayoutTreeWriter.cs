using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation;
using DocProc.Mapping.Layouts;
using DocProc.Mapping.Tables;

namespace DocProc.Pipeline.Layouts;

/// <summary>
/// Writes what a layout's field mapping found into the canonical tree of the type version (docs/11 §8.2): every hit as
/// a <c>FieldValue</c> envelope (status Extracted, Inferred for a constant or a default) with
/// <see cref="SourceMethod.Profile"/>, its page, box and printed text and its confidence, converted from the printed
/// reading (<see cref="Mapping.Values.PrintedValues"/>) to the stored shape of the field's kind as the review
/// corrections store it; a member of a composite (<c>supplier.vatId</c>) inside the composite's value; a
/// <c>column-table</c> as the rows of its repeating group, each value with its own place, tier prices as rows of the
/// row's tier group. Every other field is Missing. For the coded invoice record (<c>typedRecord</c>) the
/// record's own <c>kind</c> is set and a unit is stored as the record's unit name.
/// </summary>
internal static class LayoutTreeWriter
{
    /// <summary>The tree, and the paths written (for <c>AppliedConfigJson.layout.hits</c>).</summary>
    public static (JsonObject Tree, IReadOnlyDictionary<string, string> Written) Build(
        DescriptorIndex descriptors,
        LayoutExtraction extraction,
        IReadOnlyList<LayoutDefault> defaults,
        IReadOnlyDictionary<string, string> supplierAttributes,
        string? fallbackCurrency,
        bool typedRecord,
        DocumentTypeKey type,
        double scale = 1.0)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(extraction);
        var root = DefinitionTree.CreateEmpty(descriptors);
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        if (typedRecord && descriptors.Find("kind") is { Kind: FieldKinds.Enum })
        {
            var kind = type == DocumentTypeKey.CreditNote ? nameof(InvoiceKind.CreditNote) : nameof(InvoiceKind.Invoice);
            root["kind"] = Envelope(JsonValue.Create(kind), null, 1.0, inferred: true);
        }

        // The document's currency first: amounts printed without one take it.
        var currency = extraction.Hits.FirstOrDefault(h => h.Path == "currency")?.Value is JsonValue c && c.TryGetValue<string>(out var code) && CurrencyCode.IsWellFormed(CurrencyCode.Normalize(code))
            ? CurrencyCode.Normalize(code)
            : null;
        currency ??= defaults.FirstOrDefault(d => d.Path == "currency")?.Value is { } d0 && CurrencyCode.IsWellFormed(CurrencyCode.Normalize(d0)) ? CurrencyCode.Normalize(d0) : null;
        currency ??= fallbackCurrency;

        foreach (var hit in extraction.Hits)
        {
            if (Write(root, descriptors, hit.Path, hit.Value, new FieldSource(SourceMethod.Profile, hit.Page, hit.Box, hit.Text), Math.Round(hit.Confidence * scale, 4), hit.Inferred, currency, typedRecord))
            {
                written[hit.Path] = hit.RuleRef;
            }
        }

        foreach (var (path, table) in extraction.Tables)
        {
            if (descriptors.Find(path) is { Kind: FieldKinds.Array } group && table.Rows.Count > 0)
            {
                root[path] = Rows(descriptors, group, table, currency, typedRecord, scale);
                written[path] = $"column-table:{table.Rows.Count}";
            }
        }

        foreach (var d in defaults)
        {
            var target = Locate(root, descriptors, d.Path);
            if (target is null || DefinitionTree.ValueOf(target.Value.Current) is not null)
            {
                continue;
            }

            var text = d.Value ?? (d.FromSupplierAttribute is { } a && supplierAttributes.TryGetValue(a, out var v) ? v : null);
            if (text is null)
            {
                continue;
            }

            var parsed = Mapping.Values.PrintedValues.Parse(target.Value.Kind, text, Mapping.Values.ValueLocale.German, target.Value.Options);
            if (Write(root, descriptors, d.Path, parsed.Value, new FieldSource(SourceMethod.Profile), 1.0, inferred: true, currency, typedRecord))
            {
                written[d.Path] = "default";
            }
        }

        // A single tax row of which only the rate was found (a layout's constant rate; a design that prints one rate and
        // its totals but no breakdown, like Nordwind's web shop) takes its amounts from the net total and the total tax,
        // which are printed, so it is a whole row and not "the taxable amounts sum to 0.00".
        foreach (var taxRows in descriptors.Roots.Where(f => f.Kind == FieldKinds.TaxRow && f.IsList && !f.Provenance))
        {
            if (root[taxRows.Key] is not JsonArray rows || rows.Count == 0 || !rows.All(r => r is JsonObject row && row["taxableAmount"] is null && row["taxAmount"] is null))
            {
                continue;
            }

            var net = DefinitionTree.ValueOf(root["subtotalNet"] as JsonObject);
            var tax = DefinitionTree.ValueOf(root["totalTax"] as JsonObject);
            if (rows.Count == 1 && rows[0] is JsonObject single && net is JsonObject && tax is JsonObject)
            {
                single["taxableAmount"] = net.DeepClone();
                single["taxAmount"] = tax.DeepClone();
                written[$"{taxRows.Key}.taxableAmount"] = "derived:subtotalNet";
                written[$"{taxRows.Key}.taxAmount"] = "derived:totalTax";
            }
        }

        if (currency is not null && Locate(root, descriptors, "currency") is { Kind: FieldKinds.Currency } slot && DefinitionTree.ValueOf(slot.Current) is null && !written.ContainsKey("currency"))
        {
            root["currency"] = Envelope(JsonValue.Create(currency), new FieldSource(SourceMethod.Profile), 1.0, inferred: true);
            written["currency"] = "inferred";
        }

        DefinitionTree.Complete(root, descriptors);
        return (root, written);
    }

    private readonly record struct Slot(JsonNode? Current, string Kind, IReadOnlyList<string>? Options);

    /// <summary>
    /// Writes one value an <c>http-lookup</c> step gave (docs/11 §10.2) at a top-level field, a bag key or a composite's
    /// member, as <c>Inferred</c> in the stored shape of the field's kind. With <paramref name="overrideExisting"/>
    /// false (<c>mode: fillMissing</c>) a field that has a value keeps it; a value a reviewer corrected is never
    /// replaced. False when the path is not a field of the type, the value does not convert, or nothing was written.
    /// </summary>
    public static bool Assign(JsonObject root, DescriptorIndex descriptors, string path, JsonNode value, FieldSource source, double confidence, bool overrideExisting, string? currency, bool typedRecord)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        if (Locate(root, descriptors, path) is not { } slot)
        {
            return false;
        }

        var current = slot.Current;
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        JsonNode? owner = null;
        if (current is null && dot > 0 && descriptors.Find(path[..dot]) is { Kind: not FieldKinds.Bag } composite)
        {
            // A composite's member: its value inside the composite's envelope (the first entry of a list of them).
            owner = composite.IsList ? (root[composite.Key] as JsonArray)?.FirstOrDefault() : root[composite.Key];
            JsonNode? member = DefinitionTree.ValueOf(owner);
            foreach (var segment in path[(dot + 1)..].Split('.'))
            {
                member = (member as JsonObject)?[segment];
            }

            current = member;
            if (DefinitionTree.StatusOf(owner) == FieldStatus.Corrected)
            {
                return false;
            }
        }
        else if (DefinitionTree.StatusOf(current) == FieldStatus.Corrected)
        {
            return false;
        }

        var has = owner is null ? DefinitionTree.ValueOf(current) is not null : current is not null;
        if (has && !overrideExisting)
        {
            return false;
        }

        return Write(root, descriptors, path, value, source, confidence, inferred: true, currency, typedRecord);
    }

    /// <summary>The current envelope and kind at a top-level field, a key of a bag (<c>ext.storeNo</c>) or a member of a composite.</summary>
    private static Slot? Locate(JsonObject root, DescriptorIndex descriptors, string path)
    {
        if (descriptors.Find(path) is { } direct && direct.IsEnvelope && !path.Contains('[', StringComparison.Ordinal))
        {
            return new Slot(Navigate(root, path), direct.Kind, direct.Options);
        }

        var dot = path.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && descriptors.Find(path[..dot]) is { } owner)
        {
            if (owner.Kind == FieldKinds.Bag)
            {
                return new Slot((root[owner.Key] as JsonObject)?[path[(dot + 1)..]], FieldKinds.String, null);
            }

            if (MemberKind(owner.Kind, path[(dot + 1)..]) is { } memberKind)
            {
                return new Slot(null, memberKind, null);
            }
        }

        return null;
    }

    private static JsonNode? Navigate(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            node = (node as JsonObject)?[part];
        }

        return node;
    }

    private static string? MemberKind(string kind, string memberPath)
    {
        var current = kind;
        foreach (var segment in memberPath.Split('.'))
        {
            if (!FieldKinds.CompositeMemberKinds.TryGetValue(current, out var members))
            {
                return null;
            }

            var member = members.FirstOrDefault(m => m.Member == segment);
            if (member.Member is null)
            {
                return null;
            }

            current = member.Kind;
        }

        return current;
    }

    /// <summary>The kind of a mapped path (what the field rules read its text as), or null for a path the type does not have.</summary>
    public static LayoutFieldKind? KindOf(DescriptorIndex descriptors, string path)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        if (descriptors.Find(path) is { } field && field.IsEnvelope && !field.IsList && !path.Contains('[', StringComparison.Ordinal))
        {
            return FieldKinds.CompositeMemberKinds.ContainsKey(field.Kind) && field.Kind is not (FieldKinds.Identifier or FieldKinds.Money)
                ? null
                : new LayoutFieldKind(field.Kind, field.Options);
        }

        var dot = path.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && descriptors.Find(path[..dot]) is { } owner)
        {
            if (owner.Kind == FieldKinds.Bag)
            {
                return new LayoutFieldKind(FieldKinds.String);
            }

            return MemberKind(owner.Kind, path[(dot + 1)..]) is { } kind && (FieldKinds.IsScalar(kind) || kind == FieldKinds.Money) ? new LayoutFieldKind(kind) : null;
        }

        return null;
    }

    /// <summary>The row fields of a top-level repeating group, as the table engine reads them.</summary>
    public static IReadOnlyDictionary<string, TableField>? RowFieldsOf(DescriptorIndex descriptors, string path)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        if (descriptors.Find(path) is not { Kind: FieldKinds.Array } group)
        {
            return null;
        }

        var fields = new Dictionary<string, TableField>(StringComparer.Ordinal);
        foreach (var child in group.Children.Select(descriptors.Find).OfType<FieldDescriptor>())
        {
            var bare = !child.Provenance && FieldKinds.IsScalar(child.Kind);
            if ((child.IsEnvelope || bare) && !child.IsList && !child.ReadOnly && (FieldKinds.IsScalar(child.Kind) || child.Kind is FieldKinds.Identifier or FieldKinds.Money))
            {
                fields[child.Key] = new TableField(child.Key, child.Kind, Options: child.Options);
            }
        }

        return fields;
    }

    private static bool Write(JsonObject root, DescriptorIndex descriptors, string path, JsonNode? reading, FieldSource source, double confidence, bool inferred, string? currency, bool typedRecord)
    {
        if (reading is null)
        {
            return false;
        }

        if (descriptors.Find(path) is { } field && field.IsEnvelope && !path.Contains('.', StringComparison.Ordinal))
        {
            var value = Stored(field.Kind, reading, currency, typedRecord);
            if (value is null)
            {
                return false;
            }

            root[path] = Envelope(value, source, confidence, inferred);
            return true;
        }

        var dot = path.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || descriptors.Find(path[..dot]) is not { } owner)
        {
            return false;
        }

        var memberPath = path[(dot + 1)..];
        if (owner.Kind == FieldKinds.Bag)
        {
            if (root[owner.Key] is not JsonObject bag)
            {
                bag = [];
                root[owner.Key] = bag;
            }

            bag[memberPath] = Envelope(reading.DeepClone(), source, confidence, inferred);
            return true;
        }

        if (MemberKind(owner.Kind, memberPath) is not { } memberKind || Stored(memberKind, reading, currency, typedRecord) is not { } memberValue)
        {
            return false;
        }

        if (owner.IsList && !owner.Provenance && !memberPath.Contains('.', StringComparison.Ordinal))
        {
            // A list of bare composites (the invoice record's taxBreakdown rows, values without envelopes): the members go
            // into its first entry as they are.
            var rows = root[owner.Key] as JsonArray ?? [];
            if (rows.Count == 0)
            {
                rows.Add(new JsonObject());
            }

            if (rows[0] is not JsonObject entry)
            {
                return false;
            }

            entry[memberPath] = memberValue;
            root[owner.Key] = rows;
            return true;
        }

        // A member inside the composite's value: the composite's envelope takes the place of the member read first. A
        // list of composites takes the members into its first entry.
        var list = owner.IsList ? root[owner.Key] as JsonArray ?? [] : null;
        var existing = list is not null ? (list.Count > 0 ? list[0] as JsonObject : null) : root[owner.Key] as JsonObject;
        var composite = DefinitionTree.ValueOf(existing)?.DeepClone() as JsonObject ?? [];
        var container = composite;
        var segments = memberPath.Split('.');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (container[segments[i]] is not JsonObject child)
            {
                child = [];
                container[segments[i]] = child;
            }

            container = child;
        }

        container[segments[^1]] = memberValue;
        var envelopeSource = DefinitionTree.ValueOf(existing) is not null && existing?["source"] is JsonObject kept
            ? kept.Deserialize<FieldSource>(CanonicalJson.Options) ?? source
            : source;
        var envelope = Envelope(composite, envelopeSource, confidence, inferred);
        if (list is null)
        {
            root[owner.Key] = envelope;
            return true;
        }

        if (list.Count == 0)
        {
            list.Add(envelope);
        }
        else
        {
            list[0] = envelope;
        }

        root[owner.Key] = list;
        return true;
    }

    private static JsonArray Rows(DescriptorIndex descriptors, FieldDescriptor group, TableResult table, string? currency, bool typedRecord, double scale)
    {
        var rows = new JsonArray();
        var tierGroup = group.Children.Select(descriptors.Find).OfType<FieldDescriptor>().FirstOrDefault(c => c.Kind == FieldKinds.Array);
        var ordinal = 0;
        foreach (var row in table.Rows)
        {
            ordinal++;
            var target = DefinitionTree.EmptyRow(descriptors, group.Path);
            foreach (var cell in row.Cells)
            {
                if (descriptors.Find($"{group.Path}[*].{cell.Field}") is not { } field || Stored(field.Kind, cell.Value, currency, typedRecord) is not { } value)
                {
                    continue;
                }

                // A bare member (the record's line number) holds its value as it is; every other field its envelope.
                target[cell.Field] = field.IsEnvelope
                    ? Envelope(value, new FieldSource(SourceMethod.Profile, cell.Page, cell.Box, cell.Text), Math.Round(Mapping.Layouts.LayoutEngine.LabelAndPattern * scale, 4), inferred: false)
                    : value;
            }

            // The coded record numbers its lines; a design that prints no position gets the row's place.
            if (descriptors.Find($"{group.Path}[*].lineNumber") is { Provenance: false, Kind: FieldKinds.Integer } && target["lineNumber"] is null)
            {
                target["lineNumber"] = ordinal;
            }

            if (tierGroup is not null && row.Tiers.Count > 0)
            {
                var tiers = new JsonArray();
                foreach (var tier in row.Tiers)
                {
                    var tierRow = DefinitionTree.EmptyRow(descriptors, tierGroup.Path);
                    var quantityField = tierGroup.Children.Select(descriptors.Find).OfType<FieldDescriptor>().FirstOrDefault(f => f.Kind is FieldKinds.Decimal or FieldKinds.Integer);
                    var priceField = tierGroup.Children.Select(descriptors.Find).OfType<FieldDescriptor>().FirstOrDefault(f => f.Kind == FieldKinds.Money);
                    if (quantityField is null || priceField is null || Stored(priceField.Kind, tier.Price.Value, currency, typedRecord) is not { } price)
                    {
                        continue;
                    }

                    var source = new FieldSource(SourceMethod.Profile, tier.Price.Page, tier.Price.Box, tier.Price.Text);
                    JsonNode quantity = quantityField.Kind == FieldKinds.Integer ? JsonValue.Create(decimal.ToInt64(decimal.Truncate(tier.MinQuantity))) : JsonValue.Create(tier.MinQuantity);
                    tierRow[quantityField.Key] = Envelope(quantity, source, 1.0, inferred: true);
                    tierRow[priceField.Key] = Envelope(price, source, Math.Round(Mapping.Layouts.LayoutEngine.LabelAndPattern * scale, 4), inferred: false);
                    tiers.Add(tierRow);
                }

                target[tierGroup.Key] = tiers;
            }

            rows.Add(target);
        }

        return rows;
    }

    private static JsonObject Envelope(JsonNode value, FieldSource? source, double confidence, bool inferred)
    {
        var envelope = inferred
            ? FieldValue<JsonNode>.Inferred(value, source, confidence)
            : FieldValue<JsonNode>.Extracted(value, source, confidence);
        return (JsonObject)JsonSerializer.SerializeToNode(envelope, CanonicalJson.Options)!;
    }

    /// <summary>
    /// The printed reading in the stored shape of <paramref name="kind"/> (the shapes the definition patcher stores): an
    /// identifier with its normalised form, an amount with its currency, a currency code, a unit of the coded record by
    /// its name; null when it does not convert.
    /// </summary>
    public static JsonNode? Stored(string kind, JsonNode? reading, string? currency, bool typedRecord)
    {
        if (reading is null)
        {
            return null;
        }

        switch (kind)
        {
            case FieldKinds.Identifier:
                return reading is JsonValue iv && iv.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                    ? JsonSerializer.SerializeToNode(DocumentIdentifier.Create(text), CanonicalJson.Options)
                    : null;

            case FieldKinds.Money:
                decimal amount;
                string? own = null;
                if (reading is JsonObject money && money["amount"] is JsonValue a && a.TryGetValue(out amount))
                {
                    own = money["currency"] is JsonValue cur && cur.TryGetValue<string>(out var s) ? s : null;
                }
                else if (reading is JsonValue number && number.TryGetValue(out amount))
                {
                }
                else
                {
                    return null;
                }

                var code = own ?? currency;
                return code is not null && CurrencyCode.IsWellFormed(CurrencyCode.Normalize(code))
                    ? JsonSerializer.SerializeToNode(new Money(amount, CurrencyCode.Normalize(code)), CanonicalJson.Options)
                    : null;

            case FieldKinds.Currency:
                return reading is JsonValue cv && cv.TryGetValue<string>(out var c) && CurrencyCode.IsWellFormed(CurrencyCode.Normalize(c))
                    ? JsonValue.Create(CurrencyCode.Normalize(c))
                    : null;

            case FieldKinds.Unit when typedRecord:
                return reading is JsonValue uv && uv.TryGetValue<string>(out var unit) && UnitName(unit) is { } name ? JsonValue.Create(name) : null;

            case FieldKinds.Integer:
                // The printed reading of an integer is a long (PrintedValues), which TryGetValue<decimal> does not read.
                if (reading is JsonValue n && (n.TryGetValue<long>(out var asLong) || (n.TryGetValue<int>(out var asInt) && (asLong = asInt) == asInt)))
                {
                    return JsonValue.Create(asLong);
                }

                return reading is JsonValue d && d.TryGetValue<decimal>(out var whole) && whole == decimal.Truncate(whole)
                    ? JsonValue.Create(decimal.ToInt64(whole))
                    : null;

            default:
                return reading.DeepClone();
        }
    }

    private static string? UnitName(string? text)
    {
        var code = CodeLists.Platform[CodeLists.Uom].Resolve(text?.Trim());
        foreach (var (unit, unece) in CodeLists.UneceCodes)
        {
            if (code is not null && string.Equals(unece, code, StringComparison.Ordinal))
            {
                return unit.ToString();
            }
        }

        return null;
    }

    /// <summary>The ordinal suffix of a rule reference (<c>#invoiceNumber[0]</c> to 0).</summary>
    public static int RuleIndex(string ruleRef)
    {
        ArgumentNullException.ThrowIfNull(ruleRef);
        var open = ruleRef.LastIndexOf('[');
        return open >= 0 && int.TryParse(ruleRef.AsSpan(open + 1, ruleRef.Length - open - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
    }
}
