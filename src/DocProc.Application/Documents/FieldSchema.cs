using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Domain.DeliveryNotes;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.PurchaseOrders;

namespace DocProc.Application.Documents;

/// <summary>
/// Where the field schema of a document comes from (docs/11 §12.8): what a canonical tree's paths may address and
/// how each member is shaped. The corrections patcher walks paths through it instead of reflecting over the domain
/// records itself. P0's implementation is <see cref="ReflectionFieldSchemaResolver"/>, today's reflection walk; P1
/// adds the descriptor index of compiled definitions behind the same seam.
/// </summary>
public interface IFieldSchemaResolver
{
    /// <summary>The schema of a document type's canonical tree, or null for a type that has no canonical data.</summary>
    FieldSchemaNode? RootFor(DocumentTypeKey type);
}

/// <summary>How a member of a <see cref="FieldSchemaNode"/> is shaped, in the order the patcher tests them.</summary>
public enum FieldSchemaMemberKind
{
    /// <summary>A provenance-carrying leaf (<see cref="FieldValue{T}"/>): what a correction addresses.</summary>
    Field,

    /// <summary>A list; <see cref="FieldSchemaMember.Element"/> says what its entries are.</summary>
    List,

    /// <summary>A nested object without provenance of its own (a line, a tax row): navigated into.</summary>
    Group,

    /// <summary>A plain value without provenance (a line number, a raw unit text): not addressable.</summary>
    Scalar,
}

/// <summary>
/// An object shape of the schema: the root record, a group, a list entry or the value of a structured field (a party,
/// an amount). <see cref="Name"/> is what messages call it.
/// </summary>
public sealed class FieldSchemaNode
{
    private readonly Func<string, FieldSchemaMember?> _find;

    public FieldSchemaNode(string name, Type clrType, Func<string, FieldSchemaMember?> find)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentNullException.ThrowIfNull(find);
        Name = name;
        ClrType = clrType;
        _find = find;
    }

    public string Name { get; }

    /// <summary>The CLR type that reads and writes this shape. Until values are converted by kind (P1) the patcher needs it.</summary>
    public Type ClrType { get; }

    /// <summary>The addressable member with this JSON name, or null.</summary>
    public FieldSchemaMember? Member(string jsonName) => _find(jsonName);
}

/// <summary>
/// One addressable member of a <see cref="FieldSchemaNode"/>. <see cref="Shape"/> is the node of the member's own
/// type (navigated into for a <see cref="FieldSchemaMemberKind.Group"/>); for a <see cref="FieldSchemaMemberKind.Field"/>
/// <see cref="ValueType"/> is the type of its value and <see cref="ValueShape"/> the node of that value.
/// </summary>
public sealed class FieldSchemaMember
{
    private readonly Lazy<FieldSchemaNode> _shape;
    private readonly Lazy<FieldSchemaNode?> _valueShape;
    private readonly Lazy<FieldSchemaMember?> _element;

    public FieldSchemaMember(
        string jsonName,
        FieldSchemaMemberKind kind,
        Type clrType,
        Type? valueType,
        bool isCurrencyCode,
        Func<FieldSchemaNode> shape,
        Func<FieldSchemaNode?> valueShape,
        Func<FieldSchemaMember?> element)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonName);
        ArgumentNullException.ThrowIfNull(clrType);
        JsonName = jsonName;
        Kind = kind;
        ClrType = clrType;
        ValueType = valueType;
        IsCurrencyCode = isCurrencyCode;
        _shape = new Lazy<FieldSchemaNode>(shape);
        _valueShape = new Lazy<FieldSchemaNode?>(valueShape);
        _element = new Lazy<FieldSchemaMember?>(element);
    }

    public string JsonName { get; }

    public FieldSchemaMemberKind Kind { get; }

    /// <summary>The CLR type of the member (for a field the <see cref="FieldValue{T}"/> type).</summary>
    public Type ClrType { get; }

    /// <summary>For a field, the type of its value; else null.</summary>
    public Type? ValueType { get; }

    /// <summary>A document-level currency code (<c>currency</c>): normalised and checked as an ISO 4217 code when corrected.</summary>
    public bool IsCurrencyCode { get; }

    /// <summary>Whether a path may continue through this member into the member's own properties.</summary>
    public bool IsNavigable => Kind is FieldSchemaMemberKind.Field or FieldSchemaMemberKind.Group;

    /// <summary>The node of the member's own type.</summary>
    public FieldSchemaNode Shape => _shape.Value;

    /// <summary>For a field, the node of its value's type; else null.</summary>
    public FieldSchemaNode? ValueShape => _valueShape.Value;

    /// <summary>For a list, what its entries are (same JSON name, currency flag of the list); else null.</summary>
    public FieldSchemaMember? Element => _element.Value;
}

/// <summary>
/// The P0 implementation of <see cref="IFieldSchemaResolver"/>: the coded types' records, read by reflection exactly
/// as <c>CanonicalPatcher</c> read them before the seam. A member is a public read-write instance property under its
/// camelCase JSON name; computed get-only properties (the serialiser ignores them on input) and the
/// <c>[JsonExtensionData]</c> bag are not members. Nodes are cached per type.
/// </summary>
public sealed class ReflectionFieldSchemaResolver : IFieldSchemaResolver
{
    private readonly ConcurrentDictionary<Type, FieldSchemaNode> _nodes = new();

    public static ReflectionFieldSchemaResolver Instance { get; } = new();

    /// <summary>The domain record a document's canonical JSON holds, or null for types without canonical data.</summary>
    public static Type? RootTypeFor(DocumentTypeKey type) => type.Value switch
    {
        WellKnownTypeKeys.Invoice or WellKnownTypeKeys.CreditNote => typeof(Invoice),
        WellKnownTypeKeys.PurchaseOrder => typeof(PurchaseOrder),
        WellKnownTypeKeys.DeliveryNote => typeof(DeliveryNote),
        _ => null,
    };

    public FieldSchemaNode? RootFor(DocumentTypeKey type) => RootTypeFor(type) is { } root ? NodeFor(root) : null;

    /// <summary>The node of any CLR type, built from its properties on first use.</summary>
    public FieldSchemaNode NodeFor(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _nodes.GetOrAdd(type, Build);
    }

    private FieldSchemaNode Build(Type type)
    {
        var members = new Lazy<Dictionary<string, FieldSchemaMember>>(() => type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), Member, StringComparer.Ordinal));
        return new FieldSchemaNode(type.Name, type, name => members.Value.GetValueOrDefault(name));
    }

    private FieldSchemaMember Member(PropertyInfo property) =>
        MemberOf(JsonNamingPolicy.CamelCase.ConvertName(property.Name), property.PropertyType, string.Equals(property.Name, "Currency", StringComparison.Ordinal));

    private FieldSchemaMember MemberOf(string jsonName, Type type, bool isCurrencyCode)
    {
        var kind = KindOf(type);
        var valueType = kind == FieldSchemaMemberKind.Field ? type.GetGenericArguments()[0] : null;
        var elementType = kind == FieldSchemaMemberKind.List ? ElementTypeOf(type) : null;
        return new FieldSchemaMember(
            jsonName,
            kind,
            type,
            valueType,
            isCurrencyCode,
            () => NodeFor(type),
            () => valueType is null ? null : NodeFor(valueType),
            () => elementType is null ? null : MemberOf(jsonName, elementType, isCurrencyCode));
    }

    private static FieldSchemaMemberKind KindOf(Type type)
    {
        if (IsFieldValue(type))
        {
            return FieldSchemaMemberKind.Field;
        }

        if (ElementTypeOf(type) is not null)
        {
            return FieldSchemaMemberKind.List;
        }

        return type.IsClass && type != typeof(string) ? FieldSchemaMemberKind.Group : FieldSchemaMemberKind.Scalar;
    }

    private static bool IsFieldValue(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FieldValue<>);

    /// <summary>The element type when the type is a list (<c>IReadOnlyList&lt;T&gt;</c> or anything implementing it), else null.</summary>
    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            return type.GetGenericArguments()[0];
        }

        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            ?.GetGenericArguments()[0];
    }
}
