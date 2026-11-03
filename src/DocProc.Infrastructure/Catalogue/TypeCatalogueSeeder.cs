using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Catalogue;

/// <summary>
/// Keeps the platform's own catalogue rows in place (docs/11 §21 step 2): a row and a published version per platform
/// template (<see cref="StandardTemplates"/>), and for every tenant the types it adopts under the legacy keys
/// (<c>Invoice</c> ... <c>Other</c>, version 1, published). The <c>TypeCatalogue</c> migration writes the same rows
/// for the tenants that exist when it runs; this seeder covers what comes later: tenants created since, the SQLite
/// development database (created from the model, without migrations) and template versions a later build ships.
/// Every method only adds what is missing, so it can run on every start. The caller saves.
/// </summary>
public static class TypeCatalogueSeeder
{
    /// <summary>Adds the platform template rows and versions that are missing; returns how many rows it added.</summary>
    public static async Task<int> AddMissingPlatformTemplatesAsync(DocProcDbContext db, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var existing = await db.DocumentTypeDefinitions.IgnoreQueryFilters()
            .Where(d => d.TenantId == null)
            .Include(d => d.Versions)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var tracked = db.ChangeTracker.Entries<DocumentTypeDefinition>().Where(e => e.Entity.TenantId == null).Select(e => e.Entity);
        var byKey = existing.Concat(tracked).GroupBy(d => d.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var added = 0;
        foreach (var template in StandardTemplates.All)
        {
            if (!byKey.TryGetValue(template.Key, out var definition))
            {
                definition = TypeCatalogueRows.PlatformDefinition(template, now);
                db.DocumentTypeDefinitions.Add(definition);
                byKey[template.Key] = definition;
                added++;
            }

            if (definition.Versions.All(v => v.Version != template.Version))
            {
                var version = TypeCatalogueRows.PublishedVersion(
                    TypeCatalogueRows.PlatformVersionId(template.Key, template.Version), definition.Id, null, TypeCatalogueRows.Compile(template), now, TypeCatalogueRows.SeedActor);
                definition.Versions.Add(version);
                if (db.Entry(definition).State != EntityState.Added)
                {
                    db.DocumentTypeVersions.Add(version);
                }

                added++;
            }

            if ((definition.PublishedVersion ?? 0) < template.Version)
            {
                definition.PublishedVersion = template.Version;
                definition.Name = new Dictionary<string, string>(template.Name, StringComparer.Ordinal);
                definition.Description = template.Description;
                definition.UpdatedAt = now;
            }
        }

        return added;
    }

    /// <summary>
    /// Adds, for <paramref name="tenantId"/>, the adopted types of the legacy keys it does not have yet (and the
    /// platform rows they refer to). Returns how many type rows it added.
    /// </summary>
    public static async Task<int> AddMissingAdoptedTypesAsync(DocProcDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await AddMissingPlatformTemplatesAsync(db, now, cancellationToken).ConfigureAwait(false);
        var keys = await db.DocumentTypeDefinitions.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId)
            .Select(d => d.Key)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var have = keys.Concat(db.ChangeTracker.Entries<DocumentTypeDefinition>().Where(e => e.Entity.TenantId == tenantId).Select(e => e.Entity.Key)).ToHashSet(StringComparer.Ordinal);

        var added = 0;
        foreach (var template in StandardTemplates.All.Where(t => t.LegacyKey is not null && !have.Contains(t.LegacyKey)))
        {
            var key = template.LegacyKey!;
            var definition = new DocumentTypeDefinition
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Key = key,
                Name = new Dictionary<string, string>(template.Name, StringComparer.Ordinal),
                Description = template.Description,
                Origin = DefinitionOrigin.Adopted,
                BaseDefinitionId = TypeCatalogueRows.PlatformDefinitionId(template.Key),
                BaseVersion = template.Version,
                Status = DocumentTypeStatus.Active,
                PublishedVersion = 1,
                CreatedAt = now,
                CreatedBy = TypeCatalogueRows.SeedActor,
                UpdatedAt = now,
            };
            definition.Versions.Add(TypeCatalogueRows.PublishedVersion(
                Guid.CreateVersion7(), definition.Id, tenantId, TypeCatalogueRows.Compile(TypeCatalogueRows.Adopted(template, key)), now, TypeCatalogueRows.SeedActor,
                $"Adopted {template.Key} v{template.Version}"));
            db.DocumentTypeDefinitions.Add(definition);
            added++;
        }

        return added;
    }

    /// <summary>
    /// The platform rows and every tenant's adopted types, then the type versions of documents without one (the
    /// back-fill of docs/11 §21 step 3 for documents the migration did not see). Needs a platform-scope context.
    /// </summary>
    public static async Task<int> EnsureAllAsync(DocProcDbContext db, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var added = await AddMissingPlatformTemplatesAsync(db, now, cancellationToken).ConfigureAwait(false);
        // The platform output templates (docs/11 §13.9) are seeded the same way.
        added += await Outputs.OutputTemplates.AddMissingAsync(db, now, cancellationToken).ConfigureAwait(false);
        var tenants = await db.Tenants.IgnoreQueryFilters().Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var seeded = new List<Guid>();
        foreach (var tenant in tenants)
        {
            var adopted = await AddMissingAdoptedTypesAsync(db, tenant, now, cancellationToken).ConfigureAwait(false);
            if (adopted > 0)
            {
                seeded.Add(tenant);
            }

            added += adopted;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await BackfillTypeVersionsAsync(db, seeded, cancellationToken).ConfigureAwait(false);
        return added;
    }

    /// <summary>
    /// Pins every document without a type version to its tenant's adopted version 1 of its legacy type, and, for the
    /// tenants in <paramref name="catalogueJustAdded"/> (whose adopted types this start added, so every correction they
    /// have predates the catalogue), copies the document's version onto its corrections (docs/11 §21 step 3). Unknown
    /// documents and types without an adopted row stay null. A correction made since keeps the version it was made
    /// under, also none (a document of a copied type while <c>configurableTypes</c> was off): the back-fill never
    /// attributes it to a version the document was pinned to later. The SQL Server migration does the same once, in
    /// one statement each; this is the path of the SQLite development database and of documents written while an
    /// older build ran.
    /// </summary>
    public static async Task BackfillTypeVersionsAsync(DocProcDbContext db, IReadOnlyCollection<Guid> catalogueJustAdded, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogueJustAdded);
        ArgumentNullException.ThrowIfNull(db);
        var adopted = await db.DocumentTypeVersions.IgnoreQueryFilters()
            .Where(v => v.Version == 1 && v.TenantId != null && v.Definition!.Origin == DefinitionOrigin.Adopted)
            .Select(v => new { TenantId = v.TenantId!.Value, v.Definition!.Key, v.Id })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in adopted)
        {
            var key = Domain.Documents.DocumentTypeKey.FromStored(row.Key);
            await db.Documents.IgnoreQueryFilters()
                .Where(d => d.TenantId == row.TenantId && d.Type == key && d.DocumentTypeVersionId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.DocumentTypeVersionId, row.Id), cancellationToken).ConfigureAwait(false);
        }

        if (catalogueJustAdded.Count == 0)
        {
            return;
        }

        var tenants = catalogueJustAdded.ToList();
        await db.Corrections.IgnoreQueryFilters()
            .Where(c => tenants.Contains(c.TenantId) && c.DocumentTypeVersionId == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.DocumentTypeVersionId, c => db.Documents.IgnoreQueryFilters().Where(d => d.Id == c.DocumentId).Select(d => d.DocumentTypeVersionId).FirstOrDefault()),
                cancellationToken).ConfigureAwait(false);
    }
}
