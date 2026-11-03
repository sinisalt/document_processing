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
/// without them. A version never changes once published, so an entry never goes stale. The cache is shared by every
/// tenant of the process and bounded by clearing it: at <see cref="MaxEntries"/> entries, or when the stored JSON of its
/// entries (the definition and the artifacts, in characters) would pass the size budget, since one version may hold
/// several megabytes.
/// </summary>
public sealed class PublishedDefinitionCache
{
    public const int MaxEntries = 2048;

    /// <summary>The default size budget: the characters of stored JSON the cached versions may hold together (about 128 MB as strings).</summary>
    public const long DefaultMaxCharacters = 64L * 1024 * 1024;

    private readonly ConcurrentDictionary<Guid, Entry> _definitions = new();
    private readonly long _maxCharacters;
    private long _characters;

    public PublishedDefinitionCache()
        : this(DefaultMaxCharacters)
    {
    }

    public PublishedDefinitionCache(long maxCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        _maxCharacters = maxCharacters;
    }

    /// <summary>How many versions are cached.</summary>
    public int Count => _definitions.Count;

    /// <summary>The characters of stored JSON the cached versions were read from.</summary>
    public long Characters => Interlocked.Read(ref _characters);

    public bool TryGet(Guid versionId, [NotNullWhen(true)] out Entry? entry) => _definitions.TryGetValue(versionId, out entry);

    /// <summary>
    /// Caches <paramref name="definition"/> of the published version <paramref name="versionId"/>;
    /// <paramref name="characters"/> is the length of the stored JSON it was read from (its weight in the size budget).
    /// Returns the cached entry (the one already there if another request added the version first), which keeps its
    /// definition and artifacts together even after the cache is cleared.
    /// </summary>
    public Entry Add(Guid versionId, TypeDefinition definition, StoredArtifacts? stored = null, long characters = 0)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_definitions.TryGetValue(versionId, out var cached))
        {
            return cached;
        }

        if (_definitions.Count >= MaxEntries || Interlocked.Read(ref _characters) + characters > _maxCharacters)
        {
            _definitions.Clear();
            Interlocked.Exchange(ref _characters, 0);
        }

        var entry = new Entry(definition, stored);
        var added = _definitions.GetOrAdd(versionId, entry);
        if (ReferenceEquals(added, entry))
        {
            Interlocked.Add(ref _characters, Math.Max(0, characters));
        }

        return added;
    }

    /// <summary>The characters of stored JSON of one version row, its weight in the size budget.</summary>
    public static long SizeOf(string definitionJson, StoredArtifacts? stored)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);
        return definitionJson.Length
               + (stored is null
                   ? 0L
                   : (long)(stored.JsonSchema?.Length ?? 0) + (stored.LlmSchema?.Length ?? 0) + (stored.FormSpec?.Length ?? 0) + (stored.Descriptors?.Length ?? 0));
    }

    /// <summary>
    /// One cached version: its parsed definition and its artifacts (docs/11 §5.5), the stored ones, read once on first
    /// use; for a row without them, compiled with this deployment's adapter schemas. A caller holds on to the entry, not
    /// to the version id: the artifacts stay those of the row it was read from even if the cache is cleared before they
    /// are first read (compiling the definition again would lose what only the stored artifacts know, such as the former
    /// keys of a renamed field).
    /// </summary>
    public sealed class Entry
    {
        private readonly Lazy<CompiledDefinition?> _compiled;

        internal Entry(TypeDefinition definition, StoredArtifacts? stored)
        {
            Definition = definition;
            _compiled = new Lazy<CompiledDefinition?>(() =>
                (stored is null ? null : DefinitionCompiler.FromStored(definition, stored))
                ?? StandardTemplateConfigResolver.CompileOrNull(definition, DefinitionSchemaCompiler.Options));
        }

        public TypeDefinition Definition { get; }

        /// <summary>The artifacts of the version, built on first read.</summary>
        public CompiledDefinition? Compiled => _compiled.Value;
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

        var entry = await EntryOfAsync(row.Id, cancellationToken).ConfigureAwait(false);
        var definition = entry.Definition;
        var templateKey = definition.Base?.Template ?? row.BaseKey ?? definition.Key;
        var templateVersion = definition.Base?.Version ?? row.BaseVersion ?? definition.Version;
        // The artifacts come from the entry read now, not from a later lookup by id: a clear of the shared cache in
        // between must not swap the stored artifacts for a fresh compile without the version's history.
        return new EffectiveConfig(request, new EffectiveType(key, definition, row.Origin, templateKey, templateVersion, row.Id, () => entry.Compiled));
    }

    private async Task<PublishedDefinitionCache.Entry> EntryOfAsync(Guid versionId, CancellationToken cancellationToken)
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
        return cache.Add(versionId, DefinitionSerializer.Parse(row.DefinitionJson), stored, PublishedDefinitionCache.SizeOf(row.DefinitionJson, stored));
    }

    private static IQueryable<Row> Select(IQueryable<DocumentTypeVersion> versions) =>
        versions.Select(v => new Row(v.Id, v.Definition!.Origin, v.Definition.BaseDefinition != null ? v.Definition.BaseDefinition.Key : null, v.Definition.BaseVersion));

    private sealed record Row(Guid Id, DefinitionOrigin Origin, string? BaseKey, int? BaseVersion);
}
