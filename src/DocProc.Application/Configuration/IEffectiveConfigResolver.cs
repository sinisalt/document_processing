using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
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
    private readonly Lazy<CompiledDefinition?> _compiled;

    public EffectiveType(string typeKey, TypeDefinition definition, DefinitionOrigin origin, string templateKey, int templateVersion, Guid? versionId = null, Func<CompiledDefinition?>? compile = null)
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
        var roles = definition.Roles;
        Roles = new RoleBindings(roles.DocumentNumber, roles.DocumentDate, roles.Currency, roles.Total, roles.ValidFrom, roles.ValidUntil, roles.LineItems);
        ValidateSteps = [.. definition.Processing.Validate.Select(s => s.Kind)];
        _compiled = new Lazy<CompiledDefinition?>(compile ?? (() => null));
        _requiredOutsideSchemaStep = new Lazy<IReadOnlySet<string>>(ComputeRequiredOutsideSchemaStep);
    }

    /// <summary>
    /// The compiled artifacts of the definition (docs/11 §5.5): the descriptor index, the values-view JSON Schema, the
    /// LLM schema and the form spec. Null only where the resolver has no compiler (a host without the LLM project's
    /// adapter schemas), in which case the definition-driven features are not available for the type.
    /// </summary>
    public CompiledDefinition? Compiled => _compiled.Value;

    /// <summary>The step kinds of <c>processing.validate</c>, in order (<c>validate.schema</c>, <c>validate.rules</c>, ...).</summary>
    public IReadOnlyList<string> ValidateSteps { get; }

    // The enabled validate kinds of the document's resolved pipeline (docs/11 §10.1, P5), when a stage applied one.
    private IReadOnlyList<string>? _pipelineValidateSteps;

    /// <summary>
    /// This type with the validate steps of a resolved pipeline (<see cref="DocProc.Definitions.Pipelines.ResolvedPipeline"/>):
    /// the kinds of its enabled <c>validate</c> steps replace the definition's list, so a supplier override that disables
    /// <c>duplicate</c> or <c>rules</c> is what <see cref="RunsValidateStep"/> answers. The pipeline without overrides
    /// lists exactly the steps <see cref="RunsValidateStep"/> answers for the definition alone.
    /// </summary>
    public EffectiveType WithPipeline(DocProc.Definitions.Pipelines.ResolvedPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var kinds = pipeline.Phase(DocProc.Definitions.Pipelines.PipelinePhases.Validate)
            .Where(s => s.Enabled && s.Kind != DocProc.Definitions.Pipelines.StepKindCatalogue.ReviewGate)
            .Select(s => s.Kind)
            .ToList();
        return new EffectiveType(TypeKey, Definition, Origin, TemplateKey, TemplateVersion, VersionId, () => Compiled) { _pipelineValidateSteps = kinds };
    }

    /// <summary>
    /// Whether the validate stage runs the step kind for this type: when <c>processing.validate</c> lists it, or, for a
    /// definition without a validate list, the legacy default (the rules and the coded duplicate check). The coded
    /// duplicate check never runs when a <c>duplicate</c> rule replaces it.
    /// </summary>
    public bool RunsValidateStep(string kind)
    {
        // A duplicate rule replaces the coded duplicate check, so a fork that adds one does not raise DUPLICATE_SUSPECTED twice.
        if (kind == ValidateStepKinds.Duplicate && Definition.Rules.Any(r => r.Kind == "duplicate"))
        {
            return false;
        }

        if (_pipelineValidateSteps is { } resolved)
        {
            return resolved.Contains(kind, StringComparer.Ordinal);
        }

        return ValidateSteps.Count == 0
            ? kind is ValidateStepKinds.Rules or ValidateStepKinds.Duplicate
            : ValidateSteps.Contains(kind, StringComparer.Ordinal);
    }

    /// <summary>
    /// The required fields (definition paths) the validate stage checks for <c>FIELD_REQUIRED</c> although the
    /// definition does not list <c>validate.schema</c> (docs/11 §11 layer 1), so that marking a field required always
    /// has an effect: every required field the template the type comes from does not already require. The required
    /// fields of a template without <c>validate.schema</c> (the invoice and credit note) are the coded
    /// <c>invoice.required</c> rule's, which checks them already. Empty for an adopted type (its definition is the
    /// template) and whenever the definition runs <c>validate.schema</c> itself; every required field of a type of the
    /// tenant's own.
    /// </summary>
    public IReadOnlySet<string> RequiredOutsideSchemaStep => _requiredOutsideSchemaStep.Value;

    private readonly Lazy<IReadOnlySet<string>> _requiredOutsideSchemaStep;

    private HashSet<string> ComputeRequiredOutsideSchemaStep()
    {
        if (RunsValidateStep(ValidateStepKinds.Schema))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var template = Origin == DefinitionOrigin.Custom ? null : StandardTemplates.Find(TemplateKey);
        var covered = template is null || template.Processing.Validate.Any(s => s.Kind == ValidateStepKinds.Schema)
            ? new HashSet<string>(StringComparer.Ordinal)
            : DefinitionPaths.Enumerate(template).Where(f => f.Field.Required && !f.Field.Deprecated).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        return DefinitionPaths.Enumerate(Definition)
            .Where(f => f.Field.Required && !f.Field.Deprecated && !covered.Contains(f.Path))
            .Select(f => f.Path)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The fields marked <c>indexed</c> (docs/11 §6.2 <c>DocumentFieldIndex</c>), in definition order.</summary>
    public IReadOnlyList<FieldDescriptor> IndexedFields => Compiled?.Descriptors.Fields.Where(f => f.Indexed).ToList() ?? [];

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
    /// And it needs what vouches for its data: for an invoice <see cref="VouchedForByTotals"/>; for any other type the
    /// rules it names in <c>review.requirePassedRules</c> (P5, docs/11 §11, <see cref="VouchedForByNamedRules"/>), which
    /// the review gate then requires to have run and passed. A type that names none (a copied purchase order, delivery
    /// note or price list, a tenant's own type without them) always goes to review.
    /// </summary>
    public bool AutoApprove => Definition.Processing.Review.AutoApprove && Rules.Count > 0 && (VouchedForByTotals || VouchedForByNamedRules);

    /// <summary>Whether <c>review.requirePassedRules</c> names at least one rule of the list (docs/11 §11 layer 3).</summary>
    public bool VouchedForByNamedRules =>
        Definition.Processing.Review.RequirePassedRules is { Count: > 0 } named
        && named.All(id => Rules.Any(r => string.Equals(r.Id, id, StringComparison.Ordinal)));

    /// <summary>
    /// Whether the type is of the invoice family (<c>Invoice</c>, <c>CreditNote</c>: the coded invoice record and the
    /// coded extraction read its documents) and lists the coded <c>invoice.totals</c> rule, the check docs/11 §11 lets
    /// an invoice rely on for auto-approval.
    /// </summary>
    public bool VouchedForByTotals =>
        DocumentTypeKey.TryParse(TypeKey, out var key) && key.IsInvoiceFamily
        && Rules.Any(r => string.Equals(r.Kind, InvoiceRuleKinds.Totals, StringComparison.Ordinal));
}

/// <summary>
/// The P0 <see cref="IEffectiveConfigResolver"/>: the standard template of the document's legacy type, adopted
/// (docs/11 §21 step 2), for every tenant and supplier. The templates' rule lists, roles and review switches equal
/// the coded tables they replace (<see cref="StandardRuleSets"/>, <see cref="RoleBindings.ForLegacyType"/>, the
/// invoice-only auto-approval), which the template parity tests hold, so resolving through it changes nothing.
/// </summary>
public sealed class StandardTemplateConfigResolver : IEffectiveConfigResolver
{
    private readonly Lazy<IReadOnlyDictionary<DocumentTypeKey, EffectiveType>> _types;

    /// <summary>
    /// The resolver over the templates; <paramref name="compileOptions"/> supplies the extraction adapters' LLM
    /// schemas the compiler needs for the invoice family (<c>DefinitionSchemaCompiler.Options</c> in the LLM project).
    /// Without them a type whose definition names an adapter has no <see cref="EffectiveType.Compiled"/> artifacts.
    /// </summary>
    public StandardTemplateConfigResolver(CompileOptions? compileOptions = null)
    {
        var options = compileOptions ?? CompileOptions.Default;
        _types = new(() =>
            DocumentTypeKey.Legacy
                .Select(type => (type, template: StandardTemplates.ForLegacyType(type)))
                .Where(t => t.template is not null)
                .ToDictionary(
                    t => t.type,
                    t => new EffectiveType(t.template!.LegacyKey!, t.template, DefinitionOrigin.Adopted, t.template.Key, t.template.Version, compile: () => CompileOrNull(t.template, options))));
    }

    /// <summary>The resolver without adapter schemas: what a host without the LLM project gets.</summary>
    public static StandardTemplateConfigResolver Instance { get; } = new();

    public ValueTask<EffectiveConfig> ResolveAsync(EffectiveConfigRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(new EffectiveConfig(request, Find(request.Type)));
    }

    /// <summary>The adopted standard type of a legacy <see cref="DocumentTypeKey"/>, or null for Unknown.</summary>
    public EffectiveType? Find(DocumentTypeKey type) => _types.Value.GetValueOrDefault(type);

    /// <summary><see cref="Find"/> on <see cref="Instance"/>.</summary>
    public static EffectiveType? TypeFor(DocumentTypeKey type) => Instance.Find(type);

    /// <summary>The compiled artifacts of a definition, or null when it does not compile with these options.</summary>
    public static CompiledDefinition? CompileOrNull(TypeDefinition definition, CompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var result = DefinitionCompiler.Compile(definition, options);
        return result.Succeeded ? result.Compiled : null;
    }
}

/// <summary>The validate step kinds the validate stage runs (docs/11 §10.1, §11).</summary>
public static class ValidateStepKinds
{
    public const string Schema = "validate.schema";
    public const string Rules = "validate.rules";
    public const string Duplicate = "validate.duplicate";
}
