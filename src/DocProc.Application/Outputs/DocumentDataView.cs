using System.Globalization;
using System.Text.Json.Nodes;
using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;

namespace DocProc.Application.Outputs;

/// <summary>
/// What the values view says about the document itself (<c>meta</c> of docs/11 §5.6). <see cref="TenantId"/>,
/// <see cref="Event"/> and <see cref="EventAt"/> are what the event being rendered adds (the platform mapping
/// <c>std.event</c> reads them); <see cref="Supersedes"/> the earlier document this one replaces (<c>REVISION_OF</c>, P5).
/// </summary>
public sealed record DocumentDataMeta(
    Guid DocumentId,
    int Version,
    string TypeKey,
    int? TypeVersion,
    string Status,
    DateTimeOffset? ApprovedAt = null,
    string? ApprovedBy = null,
    IReadOnlyList<string>? Flags = null)
{
    public Guid? TenantId { get; init; }

    public string? Event { get; init; }

    public DateTimeOffset? EventAt { get; init; }

    public Guid? Supersedes { get; init; }
}

/// <summary>The matched supplier as the values view shows it (<c>supplier</c>): with its group (P4) and the tenant's attributes (<c>erpVendorId</c>).</summary>
public sealed record DocumentDataSupplier(Guid Id, string Name, string? VatId)
{
    public string? TaxId { get; init; }

    public string? Group { get; init; }

    public JsonObject? Attributes { get; init; }
}

/// <summary>
/// The receiving legal entity as the values view shows it (<c>entity</c>, docs/11 §6.4): the document's receiving entity
/// with its code, name, first VAT id and attributes, or, for a document without one, the tenant itself (no id or code,
/// the tenant's legal name and VAT id).
/// </summary>
public sealed record DocumentDataEntity(Guid? Id, string? Code, string Name, string? VatId)
{
    public JsonObject? Attributes { get; init; }
}

/// <summary>
/// The source facts of the document's ingestion (<c>source</c>, docs/11 §7.1): the channel and its connector, the
/// mailbox, the sender (or who uploaded), the folder, the file name and the subject, when it was received (UTC) and on
/// which day in the tenant's time zone.
/// </summary>
public sealed record DocumentDataSource(
    string? Channel,
    string? Connector,
    string? Mailbox,
    string? Sender,
    string? SubmittedBy,
    string? FolderPath,
    string? FileName,
    string? Subject,
    DateTimeOffset? ReceivedAt,
    DateOnly? ReceivedDate);

/// <summary>Links to the document in the API (<c>links</c>).</summary>
public sealed record DocumentDataLinks(string Original, string Api);

/// <summary>Everything of the values view that does not come from the canonical tree.</summary>
public sealed record DocumentDataContext(DocumentDataMeta Meta)
{
    public DocumentDataSupplier? Supplier { get; init; }

    public DocumentDataEntity? Entity { get; init; }

    public DocumentDataSource? Source { get; init; }

    public DocumentDataLinks? Links { get; init; }
}

/// <summary>
/// Builds the values view of docs/11 §5.6 from a canonical tree: plain values instead of <c>FieldValue</c> envelopes
/// (an identifier becomes its verbatim string, money <c>{amount, currency}</c>, a date <c>yyyy-MM-dd</c>, an enum its
/// code, a Missing or Unreadable field <c>null</c>), laid out by the definition's descriptor index, with the
/// <c>roles</c> resolved from the role bindings. It is the input of the structural check (<c>validate.schema</c>), the
/// generic rules and output mappings.
/// <para>
/// The whole view (<see cref="Build(JsonObject?, CompiledDefinition, DocumentDataContext)"/>) adds <c>meta</c>,
/// <c>supplier</c>, <c>entity</c>, <c>source</c> and <c>links</c>; <c>IDocumentDataViewBuilder</c> reads them from the
/// database. A coded invoice or credit note (a version 1 tree) and a definition tree of the same data give the same
/// view, because the standard templates' paths are the records' paths. Members of the tree that the definition does
/// not describe are left out; bags (<c>ext</c>) show every key they hold.
/// </para>
/// </summary>
public static class DocumentDataView
{
    /// <summary>The <c>doc</c> block alone: the plain values of <paramref name="canonical"/> under <paramref name="descriptors"/>.</summary>
    public static JsonObject Values(JsonObject? canonical, DescriptorIndex descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        return Group(canonical, descriptors.Roots, descriptors);
    }

    /// <summary>The view with <c>doc</c>, <c>roles</c>, <c>meta</c> and, when the document has one, <c>supplier</c>.</summary>
    public static JsonObject Build(JsonObject? canonical, CompiledDefinition compiled, DocumentDataMeta meta, DocumentDataSupplier? supplier = null) =>
        Build(canonical, compiled, new DocumentDataContext(meta) { Supplier = supplier });

    /// <summary>
    /// The whole view of docs/11 §5.6: <c>doc</c>, <c>roles</c> (with <c>roles.lines</c>, the rows by line role),
    /// <c>meta</c>, <c>supplier</c>, <c>entity</c>, <c>source</c> and <c>links</c>; a block the context does not have is null.
    /// </summary>
    public static JsonObject Build(JsonObject? canonical, CompiledDefinition compiled, DocumentDataContext context)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(context);
        var doc = Values(canonical, compiled.Descriptors);
        return Assemble(doc, Roles(doc, compiled.Definition.Roles), context);
    }

    /// <summary>The view of a document without a definition (an Unknown document): empty <c>doc</c> and <c>roles</c>, the rest as for any document.</summary>
    public static JsonObject BuildWithoutDefinition(DocumentDataContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Assemble(new JsonObject(), new JsonObject(), context);
    }

    /// <summary>
    /// The envelopes <c>$field(path)</c> answers from: for every field of the tree that holds an envelope, its path
    /// (<c>lines[0].price</c>) and <c>{status, confidence, method, normalized}</c> (an identifier's normalized form,
    /// else the plain value), at most <paramref name="max"/> of them.
    /// </summary>
    public static JsonObject FieldEnvelopes(JsonObject? canonical, DescriptorIndex descriptors, int max = 200_000)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var result = new JsonObject();
        Envelopes(canonical, descriptors.Roots, descriptors, null, result, max);
        return result;
    }

    /// <summary>
    /// The <c>roles</c> block: each bound role's value in the view (line roles with <c>[*]</c> are not top-level
    /// roles; <c>lineItems</c> is the rows), and <c>lines</c>, the rows by line role (<c>{net, quantity, unitPrice,
    /// sku}</c> from <c>lineNet</c>, <c>lineQuantity</c>, <c>lineUnitPrice</c>, <c>lineSku</c>) when the type binds any.
    /// </summary>
    public static JsonObject Roles(JsonObject doc, DefinitionRoles roles)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(roles);
        var result = new JsonObject();
        foreach (var (name, path) in new (string, string?)[]
        {
            ("documentNumber", roles.DocumentNumber), ("documentDate", roles.DocumentDate), ("dueDate", roles.DueDate),
            ("currency", roles.Currency), ("total", roles.Total), ("net", roles.Net), ("tax", roles.Tax),
            ("validFrom", roles.ValidFrom), ("validUntil", roles.ValidUntil), ("supplierParty", roles.SupplierParty),
            ("customerParty", roles.CustomerParty), ("lineItems", roles.LineItems), ("taxBreakdown", roles.TaxBreakdown),
        })
        {
            if (path is null || path.Contains('[', StringComparison.Ordinal))
            {
                continue;
            }

            result[name] = At(doc, path)?.DeepClone();
        }

        if (LineRoles(roles) is { Count: > 0 } lineRoles && At(doc, roles.LineItems!) is JsonArray rows)
        {
            result["lines"] = new JsonArray([.. rows.Select(row => (JsonNode)LineOf(row as JsonObject, lineRoles))]);
        }

        return result;
    }

    private static JsonNode? At(JsonNode? node, string path)
    {
        foreach (var segment in path.Split('.'))
        {
            node = node is JsonObject obj ? obj[segment] : null;
        }

        return node;
    }

    /// <summary>The line roles bound to a member of the rows (<c>lines[*].lineNet</c>): the name in <c>roles.lines</c> and the member.</summary>
    private static List<(string Name, string Member)> LineRoles(DefinitionRoles roles)
    {
        var result = new List<(string, string)>();
        if (roles.LineItems is not { } items || items.Contains('[', StringComparison.Ordinal))
        {
            return result;
        }

        foreach (var (name, path) in new (string, string?)[]
        {
            ("net", roles.LineNet), ("quantity", roles.LineQuantity), ("unitPrice", roles.LineUnitPrice), ("sku", roles.LineSku),
        })
        {
            if (path is not null && path.StartsWith(items + "[*].", StringComparison.Ordinal))
            {
                result.Add((name, path[(items.Length + 4)..]));
            }
        }

        return result;
    }

    private static JsonObject LineOf(JsonObject? row, List<(string Name, string Member)> lineRoles)
    {
        var line = new JsonObject();
        foreach (var (name, member) in lineRoles)
        {
            line[name] = At(row, member)?.DeepClone();
        }

        return line;
    }

    private static JsonObject Assemble(JsonObject doc, JsonObject roles, DocumentDataContext context)
    {
        var meta = context.Meta;
        var supplier = context.Supplier;
        var entity = context.Entity;
        var source = context.Source;
        return new JsonObject
        {
            ["doc"] = doc,
            ["roles"] = roles,
            ["meta"] = new JsonObject
            {
                ["documentId"] = meta.DocumentId.ToString(),
                ["tenantId"] = meta.TenantId?.ToString(),
                ["version"] = meta.Version,
                ["typeKey"] = meta.TypeKey,
                ["typeVersion"] = meta.TypeVersion,
                ["status"] = meta.Status,
                ["approvedAt"] = Utc(meta.ApprovedAt),
                ["approvedBy"] = meta.ApprovedBy,
                ["event"] = meta.Event,
                ["eventAt"] = Utc(meta.EventAt),
                ["flags"] = new JsonArray([.. (meta.Flags ?? []).Select(f => (JsonNode)f)]),
                ["supersedes"] = meta.Supersedes?.ToString(),
            },
            ["supplier"] = supplier is null ? null : new JsonObject
            {
                ["id"] = supplier.Id.ToString(),
                ["name"] = supplier.Name,
                ["vatId"] = supplier.VatId,
                ["taxId"] = supplier.TaxId,
                ["group"] = supplier.Group,
                ["attributes"] = supplier.Attributes?.DeepClone() ?? new JsonObject(),
            },
            ["entity"] = entity is null ? null : new JsonObject
            {
                ["id"] = entity.Id?.ToString(),
                ["code"] = entity.Code,
                ["name"] = entity.Name,
                ["vatId"] = entity.VatId,
                ["attributes"] = entity.Attributes?.DeepClone() ?? new JsonObject(),
            },
            ["source"] = source is null ? null : new JsonObject
            {
                ["channel"] = source.Channel,
                ["connector"] = source.Connector,
                ["mailbox"] = source.Mailbox,
                ["sender"] = source.Sender,
                ["submittedBy"] = source.SubmittedBy,
                ["folderPath"] = source.FolderPath,
                ["fileName"] = source.FileName,
                ["subject"] = source.Subject,
                ["receivedAt"] = Utc(source.ReceivedAt),
                ["receivedDate"] = source.ReceivedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            ["links"] = context.Links is { } links ? new JsonObject { ["original"] = links.Original, ["api"] = links.Api } : null,
        };
    }

    private static string? Utc(DateTimeOffset? at) => at?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static JsonObject Group(JsonObject? node, IEnumerable<FieldDescriptor> fields, DescriptorIndex descriptors)
    {
        var result = new JsonObject();
        foreach (var field in fields)
        {
            var stored = node?[field.Key];
            result[field.Key] = Value(stored, field, descriptors);
        }

        return result;
    }

    private static JsonNode? Value(JsonNode? stored, FieldDescriptor field, DescriptorIndex descriptors)
    {
        if (field.Kind == FieldKinds.Array)
        {
            if (stored is not JsonArray rows)
            {
                return null;
            }

            var children = field.Children.Select(c => descriptors.Find(c)!).ToList();
            return new JsonArray([.. rows.Select(r => (JsonNode)Group(r as JsonObject, children, descriptors))]);
        }

        if (field.Kind == FieldKinds.Object)
        {
            return stored is JsonObject group ? Group(group, field.Children.Select(c => descriptors.Find(c)!), descriptors) : null;
        }

        if (field.Kind == FieldKinds.Bag)
        {
            if (stored is not JsonObject bag)
            {
                return null;
            }

            var values = new JsonObject();
            foreach (var (key, entry) in bag)
            {
                values[key] = DefinitionTree.IsEnvelope(entry) ? Plain(DefinitionTree.ValueOf(entry), FieldKinds.String) : entry?.DeepClone();
            }

            return values;
        }

        if (!field.Provenance)
        {
            return stored?.DeepClone();
        }

        if (field.IsList)
        {
            return stored is JsonArray list ? new JsonArray([.. list.Select(e => Plain(DefinitionTree.ValueOf(e), field.Kind))]) : null;
        }

        return Plain(DefinitionTree.ValueOf(stored), field.Kind);
    }

    private static void Envelopes(JsonNode? node, IEnumerable<FieldDescriptor> fields, DescriptorIndex descriptors, string? prefix, JsonObject into, int max)
    {
        foreach (var field in fields)
        {
            if (into.Count >= max)
            {
                return;
            }

            var path = prefix is null ? field.Key : $"{prefix}.{field.Key}";
            var stored = (node as JsonObject)?[field.Key];
            if (field.Kind == FieldKinds.Array)
            {
                if (stored is JsonArray rows)
                {
                    var children = field.Children.Select(c => descriptors.Find(c)!).ToList();
                    for (var i = 0; i < rows.Count; i++)
                    {
                        Envelopes(rows[i], children, descriptors, $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]", into, max);
                    }
                }

                continue;
            }

            if (field.Kind == FieldKinds.Object)
            {
                Envelopes(stored, field.Children.Select(c => descriptors.Find(c)!), descriptors, path, into, max);
                continue;
            }

            if (DefinitionTree.IsEnvelope(stored))
            {
                into[path] = EnvelopeInfo(stored!.AsObject(), field.Kind);
            }
            else if (stored is JsonArray list && field.IsList)
            {
                for (var i = 0; i < list.Count && into.Count < max; i++)
                {
                    if (DefinitionTree.IsEnvelope(list[i]))
                    {
                        into[$"{path}[{i.ToString(CultureInfo.InvariantCulture)}]"] = EnvelopeInfo(list[i]!.AsObject(), field.Kind);
                    }
                }
            }
        }
    }

    private static JsonObject EnvelopeInfo(JsonObject envelope, string kind)
    {
        var value = DefinitionTree.ValueOf(envelope);
        return new JsonObject
        {
            ["status"] = envelope["status"]?.DeepClone(),
            ["confidence"] = envelope["confidence"]?.DeepClone(),
            ["method"] = envelope["source"]?["method"]?.DeepClone(),
            ["normalized"] = kind == FieldKinds.Identifier && value is JsonObject identifier ? identifier["normalized"]?.DeepClone() : Plain(value, kind),
        };
    }

    /// <summary>The plain value of an envelope's value by kind: an identifier's verbatim text, everything else as stored.</summary>
    private static JsonNode? Plain(JsonNode? value, string kind) => kind switch
    {
        FieldKinds.Identifier when value is JsonObject identifier => identifier["verbatim"]?.DeepClone(),
        _ => value?.DeepClone(),
    };
}
