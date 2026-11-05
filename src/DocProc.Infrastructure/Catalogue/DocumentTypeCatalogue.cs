using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm.Schema;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Catalogue;

/// <summary>How a catalogue operation ended; the API maps each to a status code.</summary>
public enum CatalogueStatus
{
    Ok,
    Created,
    NotFound,

    /// <summary>The request does not fit the current state (a key in use, a version that is not a draft, ...).</summary>
    Conflict,

    /// <summary>The definition breaks the meta-schema or the save rules; <see cref="CatalogueResult{T}.Problems"/> says where.</summary>
    Invalid,

    /// <summary>The draft changed since the caller read it (If-Match).</summary>
    PreconditionFailed,

    /// <summary>The publish checks refused the version; the report says why.</summary>
    Blocked,
}

public sealed record CatalogueResult<T>(CatalogueStatus Status, T? Value, string? Detail = null, IReadOnlyList<DefinitionProblem>? Problems = null, PublishReport? Report = null)
{
    public static CatalogueResult<T> Ok(T value) => new(CatalogueStatus.Ok, value);

    public static CatalogueResult<T> Created(T value) => new(CatalogueStatus.Created, value);

    public static CatalogueResult<T> Fail(CatalogueStatus status, string detail, IReadOnlyList<DefinitionProblem>? problems = null) => new(status, default, detail, problems);
}

/// <summary>
/// One version of a type as the catalogue lists it: everything but the compiled artifacts. <see cref="DefinitionJson"/>
/// is loaded only for the version the type's indexed fields come from (<see cref="CatalogueType.IndexedFieldsVersion"/>)
/// and, after a write, for the draft; null for every other version. A version can be large (a definition may hold up
/// to <see cref="DocumentTypeCatalogue.MaxDefinitionLength"/> characters, its compiled artifacts several times that)
/// and a type has as many versions as it was published, so a list never loads them all;
/// <see cref="DocumentTypeCatalogue.FindVersionAsync"/> loads one version in full.
/// </summary>
public sealed record CatalogueVersion(
    Guid Id,
    Guid DefinitionId,
    int Version,
    DocumentTypeVersionState State,
    int Revision,
    string? ContentHash,
    string? LlmSchemaName,
    string? LlmSchemaVersion,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    string? PublishedBy,
    string? ChangeNote,
    string? DefinitionJson = null);

/// <summary>A type with its versions (oldest first), as the catalogue operations return it.</summary>
public sealed record CatalogueType(DocumentTypeDefinition Definition, string? BaseTemplate, IReadOnlyList<CatalogueVersion> Versions)
{
    /// <summary>The version the type's indexed fields are read from: the published one, else (not published yet) the latest.</summary>
    public CatalogueVersion? IndexedFieldsVersion => IndexedFieldsVersionOf(Versions);

    internal static CatalogueVersion? IndexedFieldsVersionOf(IReadOnlyCollection<CatalogueVersion> versions) =>
        versions.Where(v => v.State == DocumentTypeVersionState.Published).MaxBy(v => v.Version) ?? versions.MaxBy(v => v.Version);
}

/// <summary>A tenant's type key with its origin and template, without its versions.</summary>
public sealed record CatalogueTypeKey(string Key, DefinitionOrigin Origin, string? BaseTemplate);

/// <summary>
/// The type catalogue of one tenant (docs/11 §5.1, §5.7, §18): adopting a platform template, copying (forking) it,
/// creating a type of the tenant's own, editing a draft version and publishing it after the publish-time checks.
/// <list type="bullet">
/// <item><description><b>Adopt</b> creates a type that follows the template, published at once (version 1).</description></item>
/// <item><description><b>Copy</b> creates a draft copy of the template under a key of the tenant's choice, or, for a type the
/// tenant has adopted under that key (<c>Invoice</c>), forks it in place: the type becomes a copy and gets the next
/// version as a draft.</description></item>
/// <item><description><b>New</b> creates a draft version 1 of a type with no template, or based on <c>std.generic</c>.</description></item>
/// <item><description>A type has at most one draft. Saving a draft checks the meta-schema and that key, version and base
/// stay what the catalogue says; everything else is checked when the draft is validated or published.</description></item>
/// <item><description><b>Publish</b> runs <see cref="PublishChecks"/> against the published version, with its indexed fields
/// as dependents; the published version becomes deprecated (documents pinned to it keep it) and the draft becomes the
/// published version with its compiled artifacts.</description></item>
/// </list>
/// The feature switches are the caller's to check (the API refuses writes without <c>configurableTypes</c>).
/// </summary>
public sealed class DocumentTypeCatalogue(DocProcDbContext db, TimeProvider clock)
{
    /// <summary>The template a new type may start from (docs/11 §5.1 "start it from std.generic").</summary>
    public const string GenericTemplate = "std.generic";

    /// <summary>
    /// The longest definition JSON a draft may hold (characters). The largest platform template is about 15 KB; a
    /// definition at the field cap with long descriptions stays well below. The API's request limit is the same.
    /// </summary>
    public const int MaxDefinitionLength = 1024 * 1024;

    /// <summary>The tenant's types with their version summaries (<see cref="CatalogueVersion"/>), ordered by key.</summary>
    public Task<IReadOnlyList<CatalogueType>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        LoadTypesAsync(tenantId, key: null, withDraftDefinition: false, cancellationToken);

    /// <summary>One of the tenant's types with its version summaries, or null.</summary>
    public async Task<CatalogueType?> FindAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
        await LoadTypesAsync(tenantId, key, withDraftDefinition: false, cancellationToken).ConfigureAwait(false) is [var type, ..] ? type : null;

    /// <summary>The tenant's type keys with their origin and template, without any version (<c>GET /document-templates</c>).</summary>
    public async Task<IReadOnlyList<CatalogueTypeKey>> ListKeysAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await KeysOf(db.DocumentTypeDefinitions.Where(d => d.TenantId == tenantId).OrderBy(d => d.Key)).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>One of the tenant's type keys with its origin and template, or null.</summary>
    public Task<CatalogueTypeKey?> FindKeyAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
        KeysOf(db.DocumentTypeDefinitions.Where(d => d.TenantId == tenantId && d.Key == key)).FirstOrDefaultAsync(cancellationToken);

    /// <summary>One version of one of the tenant's types in full, with its definition and compiled artifacts, or null.</summary>
    public Task<DocumentTypeVersion?> FindVersionAsync(Guid tenantId, string key, int version, CancellationToken cancellationToken) =>
        db.DocumentTypeVersions.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.Definition!.TenantId == tenantId && v.Definition.Key == key && v.Version == version)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<CatalogueResult<CatalogueType>> AdoptAsync(Guid tenantId, string templateKey, string? key, string actor, CancellationToken cancellationToken)
    {
        if (StandardTemplates.Find(templateKey) is not { } template)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.NotFound, $"There is no platform template '{templateKey}'.");
        }

        key ??= DefaultKey(template);
        if ((KeyProblem(key) ?? InvoiceKeyProblem(template, key)) is { } problem)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Invalid, problem, [new DefinitionProblem("key", problem)]);
        }

        if (await FindKeyAsync(tenantId, key, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Conflict, $"The tenant already has a type '{key}' ({existing.Origin}).");
        }

        var now = clock.GetUtcNow();
        await TypeCatalogueSeeder.AddMissingPlatformTemplatesAsync(db, now, cancellationToken).ConfigureAwait(false);
        var compiled = TypeCatalogueRows.Compile(TypeCatalogueRows.AdoptedNow(template, key));
        var definition = NewDefinition(tenantId, key, DefinitionOrigin.Adopted, template, now, actor);
        definition.PublishedVersion = 1;
        definition.Versions.Add(TypeCatalogueRows.PublishedVersion(Guid.CreateVersion7(), definition.Id, tenantId, compiled, now, actor, $"Adopted {template.Key} v{template.Version}"));
        db.DocumentTypeDefinitions.Add(definition);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CatalogueResult<CatalogueType>.Created(await ReloadAsync(tenantId, key, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// <c>POST /document-types/{key}:upgrade-base</c> (docs/11 §5.8, §18): moves an adopted type onto the newest version of
    /// its template (when it is not there yet) and sets <c>review.autoApprove</c> to <paramref name="autoApprove"/> (null
    /// keeps what the published version has). Auto-approval is switched on only where the template has it. Nothing
    /// changes, and the type comes back as it is, when both already hold.
    /// </summary>
    public async Task<CatalogueResult<CatalogueType>> UpgradeBaseAsync(Guid tenantId, string key, bool? autoApprove, string actor, CancellationToken cancellationToken)
    {
        var definition = await db.DocumentTypeDefinitions
            .Include(d => d.Versions)
            .Include(d => d.BaseDefinition)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Key == key, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.NotFound, $"The tenant has no type '{key}'.");
        }

        if (definition.Origin != DefinitionOrigin.Adopted || definition.BaseDefinition?.Key is not { } templateKey || StandardTemplates.Find(templateKey) is not { } template)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Conflict, $"Type '{key}' is {definition.Origin}; only an adopted type follows its template (a copy is upgraded by a three-way merge, P6).");
        }

        var current = definition.Versions.FirstOrDefault(v => v.Version == definition.PublishedVersion) ?? definition.Versions.MaxBy(v => v.Version);
        var currentAuto = current is not null && DefinitionSerializer.Parse(current.DefinitionJson).Processing.Review.AutoApprove;
        var target = autoApprove ?? currentAuto;
        if (target && !template.Processing.Review.AutoApprove)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Invalid, $"{template.Key} v{template.Version} does not auto-approve; its documents are always reviewed.", [new DefinitionProblem("autoApprove", "the template has review.autoApprove false.")]);
        }

        if ((definition.BaseVersion ?? 0) >= template.Version && target == currentAuto)
        {
            return CatalogueResult<CatalogueType>.Ok(await ReloadAsync(tenantId, key, cancellationToken).ConfigureAwait(false));
        }

        var now = clock.GetUtcNow();
        await TypeCatalogueSeeder.AddMissingPlatformTemplatesAsync(db, now, cancellationToken).ConfigureAwait(false);
        var note = (definition.BaseVersion ?? 0) < template.Version ? $"Upgraded to {template.Key} v{template.Version}" : $"Follows {template.Key} v{template.Version}";
        note += target == currentAuto ? string.Empty : target ? "; auto-approval switched on" : "; auto-approval switched off";
        TypeCatalogueSeeder.MoveAdopted(db, definition, template, target, now, actor, note);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CatalogueResult<CatalogueType>.Ok(await ReloadAsync(tenantId, key, cancellationToken).ConfigureAwait(false));
    }

    public async Task<CatalogueResult<CatalogueType>> CopyAsync(Guid tenantId, string templateKey, string? key, string actor, CancellationToken cancellationToken)
    {
        if (StandardTemplates.Find(templateKey) is not { } template)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.NotFound, $"There is no platform template '{templateKey}'.");
        }

        key ??= DefaultKey(template);
        if ((KeyProblem(key) ?? InvoiceKeyProblem(template, key)) is { } problem)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Invalid, problem, [new DefinitionProblem("key", problem)]);
        }

        var now = clock.GetUtcNow();
        var existing = await LoadDefinitionAsync(tenantId, key, tracked: true, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Fork in place (docs/11 §5.1: "the type keeps its key, so Invoice can be forked in place"): only a type
            // that follows this very template can become its copy.
            if (existing.Origin != DefinitionOrigin.Adopted || existing.BaseDefinition?.Key != template.Key)
            {
                return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Conflict, $"The tenant already has a type '{key}' ({existing.Origin}{(existing.BaseDefinition is { } b ? $" of {b.Key}" : string.Empty)}); only an adopted {template.Key} can be copied in place.");
            }

            var versions = await SummariesAsync(existing.Id, cancellationToken).ConfigureAwait(false);
            if (versions.Any(v => v.State == DocumentTypeVersionState.Draft))
            {
                return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Conflict, $"Type '{key}' already has a draft.");
            }

            var next = versions.Max(v => v.Version) + 1;
            existing.Origin = DefinitionOrigin.Copied;
            existing.BaseVersion = template.Version;
            existing.UpdatedAt = now;
            var fork = Draft(existing, TypeCatalogueRows.Copied(template, key, next), now, actor, $"Copied from {template.Key} v{template.Version}");
            db.DocumentTypeVersions.Add(fork);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return CatalogueResult<CatalogueType>.Ok(await ReloadAsync(tenantId, key, cancellationToken).ConfigureAwait(false));
        }

        await TypeCatalogueSeeder.AddMissingPlatformTemplatesAsync(db, now, cancellationToken).ConfigureAwait(false);
        var definition = NewDefinition(tenantId, key, DefinitionOrigin.Copied, template, now, actor);
        definition.Versions.Add(Draft(definition, TypeCatalogueRows.Copied(template, key), now, actor, $"Copied from {template.Key} v{template.Version}"));
        db.DocumentTypeDefinitions.Add(definition);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CatalogueResult<CatalogueType>.Created(await ReloadAsync(tenantId, key, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A type of the tenant's own from <paramref name="definitionJson"/>, as draft version 1.</summary>
    public async Task<CatalogueResult<CatalogueType>> CreateAsync(Guid tenantId, string definitionJson, string actor, CancellationToken cancellationToken)
    {
        var parsed = ParseDraft(definitionJson);
        if (parsed.Problems is { Count: > 0 } problems)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Invalid, "The definition is not valid.", problems);
        }

        var definition = parsed.Definition!;
        if ((KeyProblem(definition.Key) ?? InvoiceKeyProblem(definition, definition.Key)) is { } keyProblem)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Invalid, keyProblem, [new DefinitionProblem("key", keyProblem)]);
        }

        if (definition.Version != 1)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Invalid, "A new type starts at version 1.", [new DefinitionProblem("version", "must be 1 for a new type.")]);
        }

        if (definition.Base is { } baseRef && (baseRef.Template != GenericTemplate || baseRef.Mode != "copy" || StandardTemplates.Find(GenericTemplate)?.Version != baseRef.Version))
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Invalid, $"A new type has no base, or starts from {GenericTemplate} in copy mode; copy another template instead.", [new DefinitionProblem("base", "only std.generic (copy) is allowed for a new type.")]);
        }

        if (await FindKeyAsync(tenantId, definition.Key, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Conflict, $"The tenant already has a type '{definition.Key}' ({existing.Origin}).");
        }

        var now = clock.GetUtcNow();
        if (definition.Base is not null)
        {
            await TypeCatalogueSeeder.AddMissingPlatformTemplatesAsync(db, now, cancellationToken).ConfigureAwait(false);
        }

        var row = NewDefinition(tenantId, definition.Key, DefinitionOrigin.Custom, definition.Base is null ? null : StandardTemplates.Find(GenericTemplate), now, actor);
        row.Name = new Dictionary<string, string>(definition.Name, StringComparer.Ordinal);
        row.Description = definition.Description;
        row.Versions.Add(Draft(row, definition, now, actor, changeNote: null));
        db.DocumentTypeDefinitions.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CatalogueResult<CatalogueType>.Created(await ReloadAsync(tenantId, definition.Key, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The next version of a copied or custom type as a draft, starting from the published version.</summary>
    public async Task<CatalogueResult<DocumentTypeVersion>> CreateDraftAsync(Guid tenantId, string key, string actor, CancellationToken cancellationToken)
    {
        var definition = await LoadDefinitionAsync(tenantId, key, tracked: true, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return CatalogueResult<DocumentTypeVersion>.Fail(CatalogueStatus.NotFound, $"The tenant has no type '{key}'.");
        }

        if (definition.Origin == DefinitionOrigin.Adopted)
        {
            return CatalogueResult<DocumentTypeVersion>.Fail(CatalogueStatus.Conflict, $"Type '{key}' follows {definition.BaseDefinition?.Key}; copy it first (POST /document-types with from: {{ template, mode: \"copy\" }}) to edit it.");
        }

        var versions = await SummariesAsync(definition.Id, cancellationToken).ConfigureAwait(false);
        if (versions.FirstOrDefault(v => v.State == DocumentTypeVersionState.Draft) is { } open)
        {
            return CatalogueResult<DocumentTypeVersion>.Fail(CatalogueStatus.Conflict, $"Type '{key}' already has a draft, version {open.Version}.");
        }

        var published = versions.SingleOrDefault(v => v.Version == definition.PublishedVersion);
        var source = published ?? versions.OrderByDescending(v => v.Version).First();
        var next = versions.Max(v => v.Version) + 1;
        var sourceJson = await DefinitionJsonOfAsync(source.Id, cancellationToken).ConfigureAwait(false);
        var draft = Draft(definition, DefinitionSerializer.Parse(sourceJson) with { Version = next }, clock.GetUtcNow(), actor, changeNote: null);
        db.DocumentTypeVersions.Add(draft);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CatalogueResult<DocumentTypeVersion>.Created(draft);
    }

    /// <summary>Replaces the draft <paramref name="version"/> with <paramref name="definitionJson"/>; <paramref name="ifMatch"/> is the revision the caller read.</summary>
    public async Task<CatalogueResult<DocumentTypeVersion>> SaveDraftAsync(Guid tenantId, string key, int version, string definitionJson, int ifMatch, string actor, CancellationToken cancellationToken)
    {
        var definition = await LoadDefinitionAsync(tenantId, key, tracked: true, cancellationToken).ConfigureAwait(false);
        var draft = definition is null ? null : await LoadVersionAsync(definition.Id, version, cancellationToken).ConfigureAwait(false);
        if (definition is null || draft is null)
        {
            return CatalogueResult<DocumentTypeVersion>.Fail(CatalogueStatus.NotFound, $"Type '{key}' has no version {version}.");
        }

        if (draft.State != DocumentTypeVersionState.Draft)
        {
            return CatalogueResult<DocumentTypeVersion>.Fail(CatalogueStatus.Conflict, $"Version {version} of '{key}' is {draft.State} and cannot change; create a new draft version.");
        }

        if (draft.Revision != ifMatch)
        {
            return CatalogueResult<DocumentTypeVersion>.Fail(CatalogueStatus.PreconditionFailed, $"The draft is at revision {draft.Revision}, not {ifMatch}; reload it and apply the change again.");
        }

        var parsed = ParseDraft(definitionJson);
        if (parsed.Problems is { Count: > 0 } problems)
        {
            return CatalogueResult<DocumentTypeVersion>.Fail(CatalogueStatus.Invalid, "The definition is not valid.", problems);
        }

        var candidate = parsed.Definition!;
        var mismatches = new List<DefinitionProblem>();
        if (candidate.Key != definition.Key)
        {
            mismatches.Add(new("key", $"is '{candidate.Key}', but this is type '{definition.Key}'; a type's key does not change."));
        }

        if (candidate.Version != version)
        {
            mismatches.Add(new("version", $"is {candidate.Version}, but this is draft version {version}."));
        }

        var stored = DefinitionSerializer.Parse(draft.DefinitionJson).Base;
        if (candidate.Base != stored)
        {
            mismatches.Add(new("base", $"must stay {(stored is null ? "absent" : $"{stored.Template} v{stored.Version} ({stored.Mode})")}; a template upgrade is its own action."));
        }

        if (InvoiceKeyProblem(candidate, candidate.Key) is { } invoiceProblem)
        {
            mismatches.Add(new("llm.adapter", invoiceProblem));
        }

        if (mismatches.Count > 0)
        {
            return CatalogueResult<DocumentTypeVersion>.Fail(CatalogueStatus.Invalid, "The definition does not fit this draft.", mismatches);
        }

        draft.DefinitionJson = DefinitionSerializer.Write(candidate);
        draft.Revision++;
        draft.UpdatedAt = clock.GetUtcNow();
        definition.UpdatedAt = draft.UpdatedAt;
        _ = actor;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CatalogueResult<DocumentTypeVersion>.Ok(draft);
    }

    /// <summary>The publish checks of <paramref name="version"/> without publishing (<c>:validate</c>).</summary>
    public async Task<CatalogueResult<PublishReport>> ValidateAsync(Guid tenantId, string key, int version, CancellationToken cancellationToken)
    {
        var definition = await LoadDefinitionAsync(tenantId, key, tracked: false, cancellationToken).ConfigureAwait(false);
        var candidate = definition is null ? null : await db.DocumentTypeVersions.AsNoTracking()
            .Where(v => v.DefinitionId == definition.Id && v.Version == version)
            .Select(v => new { v.Id, v.DefinitionJson })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (definition is null || candidate is null)
        {
            return CatalogueResult<PublishReport>.Fail(CatalogueStatus.NotFound, $"Type '{key}' has no version {version}.");
        }

        var earlier = await EarlierKeysAsync(definition.Id, candidate.Id, cancellationToken).ConfigureAwait(false);
        return CatalogueResult<PublishReport>.Ok(await CheckAsync(definition, candidate.Id, candidate.DefinitionJson, earlier, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Publishes the draft <paramref name="version"/> when the publish checks pass.</summary>
    public async Task<CatalogueResult<CatalogueType>> PublishAsync(Guid tenantId, string key, int version, int? ifMatch, string? changeNote, string actor, CancellationToken cancellationToken)
    {
        var definition = await LoadDefinitionAsync(tenantId, key, tracked: true, cancellationToken).ConfigureAwait(false);
        var draft = definition is null ? null : await LoadVersionAsync(definition.Id, version, cancellationToken).ConfigureAwait(false);
        if (definition is null || draft is null)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.NotFound, $"Type '{key}' has no version {version}.");
        }

        if (draft.State != DocumentTypeVersionState.Draft)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.Conflict, $"Version {version} of '{key}' is {draft.State}; only a draft is published.");
        }

        if (ifMatch is { } expected && draft.Revision != expected)
        {
            return CatalogueResult<CatalogueType>.Fail(CatalogueStatus.PreconditionFailed, $"The draft is at revision {draft.Revision}, not {expected}; reload it and publish again.");
        }

        // Documents stored under an earlier version hold a renamed field's value under its old key; the version's
        // descriptors name those keys, so completing such a tree for this version carries the value over (docs/11 §5.7).
        // Only the keys of the earlier versions are kept, read one version at a time.
        var earlier = await EarlierKeysAsync(definition.Id, draft.Id, cancellationToken).ConfigureAwait(false);
        var report = await CheckAsync(definition, draft.Id, draft.DefinitionJson, earlier, cancellationToken).ConfigureAwait(false);
        if (!report.CanPublish)
        {
            return new CatalogueResult<CatalogueType>(CatalogueStatus.Blocked, null, $"Version {version} of '{key}' cannot be published.", report.Problems, report);
        }

        var compiled = report.Compiled!;
        var descriptors = compiled.Descriptors.WithFormerKeys(earlier);
        var now = clock.GetUtcNow();
        var replaced = await db.DocumentTypeVersions
            .Where(v => v.DefinitionId == definition.Id && v.Id != draft.Id && v.State == DocumentTypeVersionState.Published)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var previous in replaced)
        {
            previous.State = DocumentTypeVersionState.Deprecated;
            previous.UpdatedAt = now;
        }

        draft.DefinitionJson = compiled.DefinitionJson;
        draft.CompiledJsonSchema = compiled.JsonSchemaJson;
        draft.CompiledLlmSchema = compiled.LlmSchema.Json;
        draft.LlmSchemaName = compiled.LlmSchema.Name;
        draft.LlmSchemaVersion = compiled.LlmSchema.Version;
        draft.LlmSchemaHash = compiled.LlmSchema.Hash;
        draft.FormSpecJson = compiled.FormSpecJson;
        draft.DescriptorsJson = descriptors.ToJson();
        draft.ContentHash = compiled.ContentHash;
        draft.State = DocumentTypeVersionState.Published;
        draft.PublishedAt = now;
        draft.PublishedBy = actor;
        draft.ChangeNote = changeNote ?? draft.ChangeNote;
        draft.UpdatedAt = now;
        definition.PublishedVersion = version;
        definition.Name = new Dictionary<string, string>(compiled.Definition.Name, StringComparer.Ordinal);
        definition.Description = compiled.Definition.Description;
        definition.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CatalogueResult<CatalogueType>(CatalogueStatus.Ok, await ReloadAsync(tenantId, key, cancellationToken).ConfigureAwait(false), Report: report);
    }

    private async Task<PublishReport> CheckAsync(DocumentTypeDefinition definition, Guid candidateId, string candidateJson, IReadOnlyList<FieldKeySlot> earlier, CancellationToken cancellationToken)
    {
        var publishedVersion = definition.PublishedVersion;
        var publishedJson = publishedVersion is null ? null : await db.DocumentTypeVersions.AsNoTracking()
            .Where(v => v.DefinitionId == definition.Id && v.Version == publishedVersion && v.Id != candidateId)
            .Select(v => v.DefinitionJson)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var publishedDefinition = publishedJson is null ? null : DefinitionSerializer.Parse(publishedJson);
        var report = DefinitionSchemaCompiler.RunPublishChecks(new PublishCheckInput
        {
            DefinitionJson = candidateJson,
            Published = publishedDefinition,
            EarlierKeys = earlier,
            // The dependents P1 knows: the published version's indexed fields, whose index rows documents carry.
            // Supplier layouts (P4), output mappings and bindings (P3) join here as they arrive.
            Dependents = publishedDefinition is null ? [] : [.. PublishChecks.IndexedKeys(publishedDefinition)],
        });
        if (report.Definition is { } parsed && parsed.Key != definition.Key)
        {
            return report with { Problems = [.. report.Problems, new DefinitionProblem("key", $"is '{parsed.Key}', but this is type '{definition.Key}'.")], Compiled = null };
        }

        return report;
    }

    /// <summary>
    /// A draft's definition: within <see cref="MaxDefinitionLength"/> (checked first, so an oversized body costs no
    /// meta-schema run), valid against the meta-schema, readable, and within the compiler's size caps (docs/11 §5.7:
    /// fields, depth, enum codes), so a draft that could never publish is not stored.
    /// </summary>
    private static (TypeDefinition? Definition, IReadOnlyList<DefinitionProblem>? Problems) ParseDraft(string json)
    {
        if (json.Length > MaxDefinitionLength)
        {
            return (null, [new DefinitionProblem("(root)", $"The definition is {json.Length} characters long; at most {MaxDefinitionLength} are allowed.")]);
        }

        var problems = DefinitionMetaSchema.Validate(json);
        if (problems.Count > 0)
        {
            return (null, problems);
        }

        TypeDefinition definition;
        try
        {
            definition = DefinitionSerializer.Parse(json);
        }
        catch (DefinitionFormatException e)
        {
            return (null, [new DefinitionProblem("(root)", e.Message)]);
        }

        var caps = DefinitionCompiler.CheckSizeCaps(definition);
        return caps.Count > 0 ? (null, caps) : (definition, null);
    }

    /// <summary>
    /// Why <paramref name="key"/> cannot be the key of an invoice-compatible type (one from <c>std.invoice</c> or
    /// <c>std.credit-note</c>, or with the <c>invoice_extraction@1</c> adapter), or null. Until a document can get any
    /// key (P2 set-type, P5 the tenant classifier), the invoice extraction, the coded invoice rules and the correction
    /// of the invoice record are chosen by the key Invoice or CreditNote; under another key such a type would get
    /// none of them (docs/07, P1 "left for later").
    /// </summary>
    public static string? InvoiceKeyProblem(TypeDefinition definition, string key)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var template = definition.Base?.Template ?? (definition.Key.StartsWith("std.", StringComparison.Ordinal) ? definition.Key : null);
        var invoiceCompatible = template is "std.invoice" or "std.credit-note" || DefinitionCompiler.AdapterOf(definition) == DefinitionCompiler.InvoiceAdapter;
        return invoiceCompatible && !DocumentTypeKey.FromStored(key).IsInvoiceFamily
            ? $"An invoice or credit note type keeps the key Invoice or CreditNote for now; under '{key}' the invoice extraction and rules would not run."
            : null;
    }

    /// <summary>Why <paramref name="key"/> cannot be a tenant's type key, or null.</summary>
    public static string? KeyProblem(string key)
    {
        if (!DocumentTypeKey.IsValid(key))
        {
            return $"'{key}' is not a valid type key (a letter, then letters, digits, '_', '.' or '-', at most {DocumentTypeKey.MaxLength}).";
        }

        if (string.Equals(key, WellKnownTypeKeys.Unknown, StringComparison.OrdinalIgnoreCase))
        {
            return "'Unknown' is reserved for documents whose type is not decided.";
        }

        if (key.StartsWith("std.", StringComparison.OrdinalIgnoreCase))
        {
            return "Keys starting with 'std.' are the platform templates'.";
        }

        return null;
    }

    /// <summary>The key a template is adopted or copied under when the caller names none: its legacy key, or its id without <c>std.</c>.</summary>
    public static string DefaultKey(TypeDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.LegacyKey ?? (template.Key.StartsWith("std.", StringComparison.Ordinal) ? template.Key[4..] : template.Key);
    }

    private static DocumentTypeDefinition NewDefinition(Guid tenantId, string key, DefinitionOrigin origin, TypeDefinition? template, DateTimeOffset now, string actor) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Key = key,
        Name = template is null ? [] : new Dictionary<string, string>(template.Name, StringComparer.Ordinal),
        Description = template?.Description,
        Origin = origin,
        BaseDefinitionId = template is null ? null : TypeCatalogueRows.PlatformDefinitionId(template.Key),
        BaseVersion = template?.Version,
        Status = DocumentTypeStatus.Active,
        CreatedAt = now,
        CreatedBy = actor,
        UpdatedAt = now,
    };

    private static DocumentTypeVersion Draft(DocumentTypeDefinition definition, TypeDefinition content, DateTimeOffset now, string actor, string? changeNote) => new()
    {
        Id = Guid.CreateVersion7(),
        DefinitionId = definition.Id,
        TenantId = definition.TenantId,
        Version = content.Version,
        DefinitionJson = DefinitionSerializer.Write(content),
        State = DocumentTypeVersionState.Draft,
        CreatedAt = now,
        CreatedBy = actor,
        UpdatedAt = now,
        ChangeNote = changeNote,
    };

    /// <summary>The type row with its template row, without its versions (a write loads the versions it changes itself).</summary>
    private Task<DocumentTypeDefinition?> LoadDefinitionAsync(Guid tenantId, string key, bool tracked, CancellationToken cancellationToken)
    {
        var query = db.DocumentTypeDefinitions.Where(d => d.TenantId == tenantId && d.Key == key).Include(d => d.BaseDefinition);
        return (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>One version of a type, tracked, in full: the draft a write changes.</summary>
    private Task<DocumentTypeVersion?> LoadVersionAsync(Guid definitionId, int version, CancellationToken cancellationToken) =>
        db.DocumentTypeVersions.Where(v => v.DefinitionId == definitionId && v.Version == version).FirstOrDefaultAsync(cancellationToken);

    private static IQueryable<CatalogueTypeKey> KeysOf(IQueryable<DocumentTypeDefinition> definitions) =>
        definitions.AsNoTracking().Select(d => new CatalogueTypeKey(d.Key, d.Origin, d.BaseDefinition != null ? d.BaseDefinition.Key : null));

    private static IQueryable<CatalogueVersion> Summaries(IQueryable<DocumentTypeVersion> versions) =>
        versions.AsNoTracking().Select(v => new CatalogueVersion(
            v.Id, v.DefinitionId, v.Version, v.State, v.Revision, v.ContentHash, v.LlmSchemaName, v.LlmSchemaVersion,
            v.CreatedAt, v.CreatedBy, v.UpdatedAt, v.PublishedAt, v.PublishedBy, v.ChangeNote, null));

    private async Task<List<CatalogueVersion>> SummariesAsync(Guid definitionId, CancellationToken cancellationToken) =>
        await Summaries(db.DocumentTypeVersions.Where(v => v.DefinitionId == definitionId)).ToListAsync(cancellationToken).ConfigureAwait(false);

    private Task<string> DefinitionJsonOfAsync(Guid versionId, CancellationToken cancellationToken) =>
        db.DocumentTypeVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.DefinitionJson).SingleAsync(cancellationToken);

    /// <summary>
    /// The key slots (<see cref="DescriptorIndex.KeySlotsOf"/>) of every published or deprecated version of a type but
    /// <paramref name="exceptId"/>: read one definition at a time and dropped once its slots are taken, so a type with
    /// many large versions costs one definition in memory, not all of them.
    /// </summary>
    private async Task<IReadOnlyList<FieldKeySlot>> EarlierKeysAsync(Guid definitionId, Guid exceptId, CancellationToken cancellationToken)
    {
        var ids = await db.DocumentTypeVersions.AsNoTracking()
            .Where(v => v.DefinitionId == definitionId && v.Id != exceptId && (v.State == DocumentTypeVersionState.Published || v.State == DocumentTypeVersionState.Deprecated))
            .OrderBy(v => v.Version)
            .Select(v => v.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var slots = new List<FieldKeySlot>();
        foreach (var id in ids)
        {
            slots.AddRange(DescriptorIndex.KeySlotsOf(DefinitionSerializer.Parse(await DefinitionJsonOfAsync(id, cancellationToken).ConfigureAwait(false))));
        }

        return slots;
    }

    /// <summary>
    /// The tenant's types (one, with <paramref name="key"/>) with their version summaries; the definition JSON only of
    /// each type's <see cref="CatalogueType.IndexedFieldsVersion"/> and, with <paramref name="withDraftDefinition"/>, of
    /// its draft. The compiled artifacts are never read here.
    /// </summary>
    private async Task<IReadOnlyList<CatalogueType>> LoadTypesAsync(Guid tenantId, string? key, bool withDraftDefinition, CancellationToken cancellationToken)
    {
        var definitionQuery = db.DocumentTypeDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId);
        var versionQuery = db.DocumentTypeVersions.Where(v => v.TenantId == tenantId && v.Definition!.TenantId == tenantId);
        if (key is not null)
        {
            definitionQuery = definitionQuery.Where(d => d.Key == key);
            versionQuery = versionQuery.Where(v => v.Definition!.Key == key);
        }

        var definitions = await definitionQuery.Include(d => d.BaseDefinition).OrderBy(d => d.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (definitions.Count == 0)
        {
            return [];
        }

        var byDefinition = (await Summaries(versionQuery).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(v => v.DefinitionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(v => v.Version).ToList());
        var wanted = new List<Guid>();
        foreach (var versions in byDefinition.Values)
        {
            if (CatalogueType.IndexedFieldsVersionOf(versions) is { } indexed)
            {
                wanted.Add(indexed.Id);
            }

            if (withDraftDefinition && versions.FirstOrDefault(v => v.State == DocumentTypeVersionState.Draft) is { } draft && draft.Id != CatalogueType.IndexedFieldsVersionOf(versions)?.Id)
            {
                wanted.Add(draft.Id);
            }
        }

        var json = wanted.Count == 0
            ? []
            : await db.DocumentTypeVersions.AsNoTracking().Where(v => wanted.Contains(v.Id)).Select(v => new { v.Id, v.DefinitionJson })
                .ToDictionaryAsync(v => v.Id, v => v.DefinitionJson, cancellationToken).ConfigureAwait(false);
        return
        [
            .. definitions.Select(d => new CatalogueType(
                d,
                d.BaseDefinition?.Key,
                [.. byDefinition.GetValueOrDefault(d.Id, []).Select(v => json.TryGetValue(v.Id, out var definitionJson) ? v with { DefinitionJson = definitionJson } : v)])),
        ];
    }

    private async Task<CatalogueType> ReloadAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
        (await LoadTypesAsync(tenantId, key, withDraftDefinition: true, cancellationToken).ConfigureAwait(false)).Single();
}
