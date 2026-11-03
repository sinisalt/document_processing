using System.Globalization;
using System.Text.Json.Nodes;
using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;

namespace DocProc.Application.Outputs;

/// <summary>What the values view says about the document itself (<c>meta</c> of docs/11 §5.6).</summary>
public sealed record DocumentDataMeta(
    Guid DocumentId,
    int Version,
    string TypeKey,
    int? TypeVersion,
    string Status,
    DateTimeOffset? ApprovedAt = null,
    string? ApprovedBy = null,
    IReadOnlyList<string>? Flags = null);

/// <summary>The matched supplier as the values view shows it (<c>supplier</c>).</summary>
public sealed record DocumentDataSupplier(Guid Id, string Name, string? VatId);

/// <summary>
/// Builds the values view of docs/11 §5.6 from a canonical tree: plain values instead of <c>FieldValue</c> envelopes
/// (an identifier becomes its verbatim string, money <c>{amount, currency}</c>, a date <c>yyyy-MM-dd</c>, an enum its
/// code, a Missing or Unreadable field <c>null</c>), laid out by the definition's descriptor index, with the
/// <c>roles</c> resolved from the role bindings and the <c>meta</c> and <c>supplier</c> blocks. It is the input of the
/// structural check (<c>validate.schema</c>), the generic rules and, from P3, output mappings.
/// <para>
/// Not yet in the view: <c>source</c> (the ingestion's source facts arrive with routing, P4), <c>entity</c> (receiving
/// entities, P4) and <c>links</c> (outputs, P3). Members of the tree that the definition does not describe are left
/// out; bags (<c>ext</c>) show every key they hold.
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

    /// <summary>The whole view: <c>doc</c>, <c>roles</c>, <c>meta</c> and, when the document has one, <c>supplier</c>.</summary>
    public static JsonObject Build(JsonObject? canonical, CompiledDefinition compiled, DocumentDataMeta meta, DocumentDataSupplier? supplier = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(meta);
        var doc = Values(canonical, compiled.Descriptors);
        var view = new JsonObject
        {
            ["doc"] = doc,
            ["roles"] = Roles(doc, compiled.Definition.Roles),
            ["meta"] = new JsonObject
            {
                ["documentId"] = meta.DocumentId.ToString(),
                ["version"] = meta.Version,
                ["typeKey"] = meta.TypeKey,
                ["typeVersion"] = meta.TypeVersion,
                ["status"] = meta.Status,
                ["approvedAt"] = meta.ApprovedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["approvedBy"] = meta.ApprovedBy,
                ["flags"] = new JsonArray([.. (meta.Flags ?? []).Select(f => (JsonNode)f)]),
                ["supersedes"] = null,
            },
            ["supplier"] = supplier is null ? null : new JsonObject { ["id"] = supplier.Id.ToString(), ["name"] = supplier.Name, ["vatId"] = supplier.VatId },
        };
        return view;
    }

    /// <summary>The <c>roles</c> block: each bound role's value in the view (line roles with <c>[*]</c> are left out; <c>lineItems</c> is the rows).</summary>
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

            JsonNode? node = doc;
            foreach (var segment in path.Split('.'))
            {
                node = node is JsonObject obj ? obj[segment] : null;
            }

            result[name] = node?.DeepClone();
        }

        return result;
    }

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

    /// <summary>The plain value of an envelope's value by kind: an identifier's verbatim text, everything else as stored.</summary>
    private static JsonNode? Plain(JsonNode? value, string kind) => kind switch
    {
        FieldKinds.Identifier when value is JsonObject identifier => identifier["verbatim"]?.DeepClone(),
        _ => value?.DeepClone(),
    };
}
