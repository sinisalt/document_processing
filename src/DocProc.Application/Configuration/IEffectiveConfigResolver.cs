using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Domain.Documents;
using DocProc.Domain.Validation;

namespace DocProc.Application.Configuration;

/// <summary>
/// The configuration that applies to one document (docs/11 §7.3 and §10.3): its type's definition now, and from P4
/// the supplier's and its group's settings and the identified layout. Stages ask this seam instead of reading
/// tables and switches of their own, so a later phase changes what it returns, not who asks.
/// <para>
/// With a database the implementation is the type catalogue's (<c>CatalogueConfigResolver</c>, P1): the tenant's
/// published version of the type, or the version the document is pinned to. Without a catalogue row (a host without
/// a database, a tenant whose rows are not seeded yet) it answers like P0's <see cref="StandardTemplateConfigResolver"/>:
/// every tenant has adopted the standard templates under the legacy keys. There is no supplier configuration yet.
/// </para>
/// </summary>
public interface IEffectiveConfigResolver
{
    ValueTask<EffectiveConfig> ResolveAsync(EffectiveConfigRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the configuration is resolved for: the tenant, the document's type, once known its supplier, and the type
/// version the document is pinned to (<c>Documents.DocumentTypeVersionId</c>, docs/11 §5.7), which wins over the
/// current published version as long as it is a version of the same type.
/// </summary>
public sealed record EffectiveConfigRequest(Guid TenantId, DocumentTypeKey Type, Guid? SupplierId = null, Guid? PinnedVersionId = null);

/// <summary>
/// The resolved configuration of a document. <see cref="Type"/> is null when the type has no definition
/// (<see cref="DocumentTypeKey.Unknown"/>). Supplier and layout configuration join in P4.
/// </summary>
public sealed record EffectiveConfig(EffectiveConfigRequest Request, EffectiveType? Type);

/// <summary>How a tenant's type relates to the platform template (docs/11 §6.1, <c>DocumentTypeDefinition.Origin</c>).</summary>
public enum DefinitionOrigin
{
    /// <summary>A platform template itself (TenantId NULL).</summary>
    Platform,

    /// <summary>The tenant follows the template; non-breaking template versions apply automatically.</summary>
    Adopted,

    /// <summary>The tenant forked the template and edits its copy.</summary>
    Copied,

    /// <summary>A type the tenant created.</summary>
    Custom,
}

/// <summary>
/// The definition a document's type resolved to, with the views the running code takes from it: the rule list the
/// validator runs, the role bindings of the list columns and whether the review gate may approve without a reviewer.
/// </summary>
public sealed record EffectiveType
{
    public EffectiveType(string typeKey, TypeDefinition definition, DefinitionOrigin origin, string templateKey, int templateVersion, Guid? versionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        TypeKey = typeKey;
        Definition = definition;
        Origin = origin;
        TemplateKey = templateKey;
        TemplateVersion = templateVersion;
        VersionId = versionId;
        Rules = definition.ToRuleReferences();
        Roles = new RoleBindings(definition.Roles.DocumentNumber, definition.Roles.DocumentDate, definition.Roles.Currency, definition.Roles.Total);
    }

    /// <summary>The tenant's type key: the legacy key (<c>Invoice</c>, <c>Other</c>, ...) for adopted templates.</summary>
    public string TypeKey { get; }

    public TypeDefinition Definition { get; }

    public DefinitionOrigin Origin { get; }

    /// <summary>The template the type follows or was copied from (<c>std.invoice</c>), and its version.</summary>
    public string TemplateKey { get; }

    public int TemplateVersion { get; }

    /// <summary>The catalogue row of the version (<c>DocumentTypeVersions.Id</c>) a document pins; null when it came from the embedded templates.</summary>
    public Guid? VersionId { get; }

    /// <summary>The version of the tenant's type (<c>definition.version</c>).</summary>
    public int Version => Definition.Version;

    /// <summary>The definition's rule list, in order. Empty means the type is not validated and always goes to review.</summary>
    public IReadOnlyList<RuleReference> Rules { get; }

    /// <summary>The summary roles of the list columns (<see cref="RoleProjector"/>).</summary>
    public RoleBindings Roles { get; }

    /// <summary>
    /// Whether the review gate may approve a document of this type without a reviewer (<c>processing.review.autoApprove</c>,
    /// docs/11 §11 layer 3). It also needs at least one rule: a type without rules has nothing that vouches for its data.
    /// </summary>
    public bool AutoApprove => Definition.Processing.Review.AutoApprove && Rules.Count > 0;
}

/// <summary>
/// The P0 <see cref="IEffectiveConfigResolver"/>: the standard template of the document's legacy type, adopted
/// (docs/11 §21 step 2), for every tenant and supplier. The templates' rule lists, roles and review switches equal
/// the coded tables they replace (<see cref="StandardRuleSets"/>, <see cref="RoleBindings.ForLegacyType"/>, the
/// invoice-only auto-approval), which the template parity tests hold, so resolving through it changes nothing.
/// </summary>
public sealed class StandardTemplateConfigResolver : IEffectiveConfigResolver
{
    private static readonly Lazy<IReadOnlyDictionary<DocumentTypeKey, EffectiveType>> Types = new(() =>
        DocumentTypeKey.Legacy
            .Select(type => (type, template: StandardTemplates.ForLegacyType(type)))
            .Where(t => t.template is not null)
            .ToDictionary(
                t => t.type,
                t => new EffectiveType(t.template!.LegacyKey!, t.template, DefinitionOrigin.Adopted, t.template.Key, t.template.Version)));

    public static StandardTemplateConfigResolver Instance { get; } = new();

    public ValueTask<EffectiveConfig> ResolveAsync(EffectiveConfigRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(new EffectiveConfig(request, TypeFor(request.Type)));
    }

    /// <summary>The adopted standard type of a legacy <see cref="DocumentTypeKey"/>, or null for Unknown.</summary>
    public static EffectiveType? TypeFor(DocumentTypeKey type) => Types.Value.GetValueOrDefault(type);
}
