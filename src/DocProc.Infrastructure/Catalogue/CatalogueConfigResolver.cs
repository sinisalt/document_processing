using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm.Schema;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Catalogue;

/// <summary>
/// The parsed definitions of published (immutable) type versions, by version id, for the life of the process, with
/// their artifacts: the ones the version stored when it was published (docs/11 §5.5), compiled again only for a row
/// without them. A version never changes once published, so an entry never goes stale; the cache is bounded by
/// clearing it.
/// </summary>
public sealed class PublishedDefinitionCache
{
    private const int MaxEntries = 2048;
    private readonly ConcurrentDictionary<Guid, Entry> _definitions = new();

    public bool TryGet(Guid versionId, [NotNullWhen(true)] out TypeDefinition? definition)
    {
        definition = _definitions.TryGetValue(versionId, out var entry) ? entry.Definition : null;
        return definition is not null;
    }

    public TypeDefinition Add(Guid versionId, TypeDefinition definition, StoredArtifacts? stored = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_definitions.Count >= MaxEntries)
        {
            _definitions.Clear();
        }

        return _definitions.GetOrAdd(versionId, _ => new Entry(definition, stored)).Definition;
    }

    /// <summary>
    /// The artifacts of a cached version (docs/11 §5.5): the stored ones, read once on first use; for a row without
    /// them, compiled with this deployment's adapter schemas.
    /// </summary>
    public CompiledDefinition? CompiledOf(Guid versionId) => _definitions.TryGetValue(versionId, out var entry) ? entry.Compiled.Value : null;

    private sealed class Entry(TypeDefinition definition, StoredArtifacts? stored)
    {
        public TypeDefinition Definition { get; } = definition;

        public Lazy<CompiledDefinition?> Compiled { get; } = new(() =>
            (stored is null ? null : DefinitionCompiler.FromStored(definition, stored))
            ?? StandardTemplateConfigResolver.CompileOrNull(definition, DefinitionSchemaCompiler.Options));
    }
}

/// <summary>
/// <see cref="IEffectiveConfigResolver"/> over the type catalogue (docs/11 §6.1, §7.3): a document's type resolves to
/// the version it is pinned to (<see cref="EffectiveConfigRequest.PinnedVersionId"/>, while that is a published or
/// deprecated version of the same type), otherwise to its tenant's published version of the type.
/// <para>
/// The <c>configurableTypes</c> switch (docs/11 §3) decides which types count: while it is off only the adopted
/// standard types do, which are the embedded templates under the tenant's key, so a tenant that has not been moved
/// onto the feature behaves exactly as before even if copies or custom types were saved while it was on. A type of
/// the tenant's own also needs <c>customTypes</c>: without it, its documents resolve as if it did not exist. A type
/// without a catalogue row falls back to <see cref="StandardTemplateConfigResolver"/>, the P0 answer.
/// </para>
/// </summary>
public sealed class CatalogueConfigResolver(DocProcDbContext db, ITenantFeatures features, PublishedDefinitionCache cache) : IEffectiveConfigResolver
{
    /// <summary>The P0 answer for a type without a catalogue row, with the artifacts compiled against this deployment's adapters.</summary>
    public static StandardTemplateConfigResolver Templates { get; } = new(DefinitionSchemaCompiler.Options);

    public async ValueTask<EffectiveConfig> ResolveAsync(EffectiveConfigRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Type.IsUnknown)
        {
            return new EffectiveConfig(request, null);
        }

        var configurable = await features.IsEnabledAsync(request.TenantId, TenantFeature.ConfigurableTypes, cancellationToken).ConfigureAwait(false);
        var custom = configurable && await features.IsEnabledAsync(request.TenantId, TenantFeature.CustomTypes, cancellationToken).ConfigureAwait(false);
        var key = request.Type.Value;
        var tenantId = request.TenantId;
        var query = db.DocumentTypeVersions.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.Definition!.TenantId == tenantId && v.Definition.Key == key)
            .Where(v => v.Definition!.Origin == DefinitionOrigin.Adopted || (configurable && v.Definition.Origin == DefinitionOrigin.Copied) || (custom && v.Definition.Origin == DefinitionOrigin.Custom));

        Row? row = null;
        if (request.PinnedVersionId is { } pinned)
        {
            row = await Select(query.Where(v => v.Id == pinned && (v.State == DocumentTypeVersionState.Published || v.State == DocumentTypeVersionState.Deprecated)))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        row ??= await Select(query.Where(v => v.State == DocumentTypeVersionState.Published && v.Version == v.Definition!.PublishedVersion && v.Definition.Status == DocumentTypeStatus.Active))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return new EffectiveConfig(request, Templates.Find(request.Type));
        }

        var definition = await DefinitionOfAsync(row.Id, cancellationToken).ConfigureAwait(false);
        var templateKey = definition.Base?.Template ?? row.BaseKey ?? definition.Key;
        var templateVersion = definition.Base?.Version ?? row.BaseVersion ?? definition.Version;
        var versionId = row.Id;
        return new EffectiveConfig(request, new EffectiveType(key, definition, row.Origin, templateKey, templateVersion, versionId, () => cache.CompiledOf(versionId) ?? StandardTemplateConfigResolver.CompileOrNull(definition, DefinitionSchemaCompiler.Options)));
    }

    private async Task<TypeDefinition> DefinitionOfAsync(Guid versionId, CancellationToken cancellationToken)
    {
        if (cache.TryGet(versionId, out var cached))
        {
            return cached;
        }

        var row = await db.DocumentTypeVersions.AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => new { v.DefinitionJson, v.CompiledJsonSchema, v.CompiledLlmSchema, v.LlmSchemaName, v.LlmSchemaVersion, v.LlmSchemaHash, v.FormSpecJson, v.DescriptorsJson, v.ContentHash })
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var stored = new StoredArtifacts(row.CompiledJsonSchema, row.CompiledLlmSchema, row.LlmSchemaName, row.LlmSchemaVersion, row.LlmSchemaHash, row.FormSpecJson, row.DescriptorsJson, row.ContentHash);
        return cache.Add(versionId, DefinitionSerializer.Parse(row.DefinitionJson), stored);
    }

    private static IQueryable<Row> Select(IQueryable<DocumentTypeVersion> versions) =>
        versions.Select(v => new Row(v.Id, v.Definition!.Origin, v.Definition.BaseDefinition != null ? v.Definition.BaseDefinition.Key : null, v.Definition.BaseVersion));

    private sealed record Row(Guid Id, DefinitionOrigin Origin, string? BaseKey, int? BaseVersion);
}
