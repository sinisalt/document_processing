using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Catalogue;

/// <summary>
/// The parsed definitions of published (immutable) type versions, by version id, for the life of the process. A
/// version never changes once published, so an entry never goes stale; the cache is bounded by clearing it.
/// </summary>
public sealed class PublishedDefinitionCache
{
    private const int MaxEntries = 2048;
    private readonly ConcurrentDictionary<Guid, TypeDefinition> _definitions = new();

    public bool TryGet(Guid versionId, [NotNullWhen(true)] out TypeDefinition? definition) => _definitions.TryGetValue(versionId, out definition);

    public TypeDefinition Add(Guid versionId, TypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_definitions.Count >= MaxEntries)
        {
            _definitions.Clear();
        }

        return _definitions.GetOrAdd(versionId, definition);
    }
}

/// <summary>
/// <see cref="IEffectiveConfigResolver"/> over the type catalogue (docs/11 §6.1, §7.3): a document's type resolves to
/// the version it is pinned to (<see cref="EffectiveConfigRequest.PinnedVersionId"/>, while that is a published or
/// deprecated version of the same type), otherwise to its tenant's published version of the type.
/// <para>
/// The <c>configurableTypes</c> switch (docs/11 §3) decides which types count: while it is off only the adopted
/// standard types do, which are the embedded templates under the tenant's key, so a tenant that has not been moved
/// onto the feature behaves exactly as before even if copies or custom types were saved while it was on. A type
/// without a catalogue row falls back to <see cref="StandardTemplateConfigResolver"/>, the P0 answer.
/// </para>
/// </summary>
public sealed class CatalogueConfigResolver(DocProcDbContext db, ITenantFeatures features, PublishedDefinitionCache cache) : IEffectiveConfigResolver
{
    public async ValueTask<EffectiveConfig> ResolveAsync(EffectiveConfigRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Type.IsUnknown)
        {
            return new EffectiveConfig(request, null);
        }

        var configurable = await features.IsEnabledAsync(request.TenantId, TenantFeature.ConfigurableTypes, cancellationToken).ConfigureAwait(false);
        var key = request.Type.Value;
        var tenantId = request.TenantId;
        var query = db.DocumentTypeVersions.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.Definition!.TenantId == tenantId && v.Definition.Key == key)
            .Where(v => configurable || v.Definition!.Origin == DefinitionOrigin.Adopted);

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
            return new EffectiveConfig(request, StandardTemplateConfigResolver.TypeFor(request.Type));
        }

        var definition = await DefinitionOfAsync(row.Id, cancellationToken).ConfigureAwait(false);
        var templateKey = definition.Base?.Template ?? row.BaseKey ?? definition.Key;
        var templateVersion = definition.Base?.Version ?? row.BaseVersion ?? definition.Version;
        return new EffectiveConfig(request, new EffectiveType(key, definition, row.Origin, templateKey, templateVersion, row.Id));
    }

    private async Task<TypeDefinition> DefinitionOfAsync(Guid versionId, CancellationToken cancellationToken)
    {
        if (cache.TryGet(versionId, out var cached))
        {
            return cached;
        }

        var json = await db.DocumentTypeVersions.AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => v.DefinitionJson)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        return cache.Add(versionId, DefinitionSerializer.Parse(json));
    }

    private static IQueryable<Row> Select(IQueryable<DocumentTypeVersion> versions) =>
        versions.Select(v => new Row(v.Id, v.Definition!.Origin, v.Definition.BaseDefinition != null ? v.Definition.BaseDefinition.Key : null, v.Definition.BaseVersion));

    private sealed record Row(Guid Id, DefinitionOrigin Origin, string? BaseKey, int? BaseVersion);
}
