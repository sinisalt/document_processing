using System.Security.Cryptography;
using System.Text;
using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm.Schema;

namespace DocProc.Infrastructure.Catalogue;

/// <summary>
/// The rows of the type catalogue (docs/11 §6.1) that the platform writes by itself: the platform templates and the
/// types every tenant adopts under the legacy keys (§21 step 2). One builder for both writers, the seed migration's
/// SQL and <see cref="TypeCatalogueSeeder"/>, so a fresh database and an upgraded one hold the same rows.
/// </summary>
public static class TypeCatalogueRows
{
    /// <summary>The actor recorded on rows the platform wrote.</summary>
    public const string SeedActor = "platform";

    /// <summary>The stable id of a platform template's definition row.</summary>
    public static Guid PlatformDefinitionId(string templateKey) => StableGuid($"docproc:type-template:{templateKey}");

    /// <summary>The stable id of a platform template version row.</summary>
    public static Guid PlatformVersionId(string templateKey, int version) => StableGuid($"docproc:type-template:{templateKey}@{version}");

    /// <summary>
    /// The definition a tenant gets when it adopts <paramref name="template"/> under <paramref name="key"/>: the template,
    /// fully materialised, with the tenant's key and <c>base</c> naming the template version it follows (docs/11 §5.1).
    /// </summary>
    public static TypeDefinition Adopted(TypeDefinition template, string key, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template with
        {
            Key = key,
            Version = version,
            LegacyKey = null,
            Base = new DefinitionBase { Template = template.Key, Version = template.Version, Mode = "adopt" },
        };
    }

    /// <summary>
    /// The template keys whose auto-approval a tenant opts into (docs/11 §5.8, §23 "a ConfigAdmin then switches
    /// auto-approval on"): the templates whose first version, frozen with the catalogue migration, did not auto-approve
    /// (<c>std.price-list</c>, <c>std.daily-specials</c>). A later version that auto-approves does not switch it on for a
    /// tenant by itself, neither on an upgrade nor on a new adoption.
    /// </summary>
    public static IReadOnlySet<string> AutoApproveOptIn => OptIn.Value;

    private static readonly Lazy<IReadOnlySet<string>> OptIn = new(() =>
        TypeCatalogueSeedSql.FrozenTemplates().Where(t => !t.Template.Processing.Review.AutoApprove).Select(t => t.Template.Key).ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// What a tenant gets when it adopts <paramref name="template"/> now (<c>POST /document-types {from: {mode: adopt}}</c>):
    /// <see cref="Adopted"/>, with <c>review.autoApprove</c> off for an opt-in template (<see cref="AutoApproveOptIn"/>);
    /// <c>POST /document-types/{key}:upgrade-base {autoApprove: true}</c> switches it on.
    /// </summary>
    public static TypeDefinition AdoptedNow(TypeDefinition template, string key)
    {
        var adopted = Adopted(template, key);
        return AutoApproveOptIn.Contains(template.Key) && adopted.Processing.Review.AutoApprove
            ? adopted with { Processing = adopted.Processing with { Review = adopted.Processing.Review with { AutoApprove = false } } }
            : adopted;
    }

    /// <summary>A copy (fork) of <paramref name="template"/> under <paramref name="key"/>: the same, with <c>base</c> in copy mode.</summary>
    public static TypeDefinition Copied(TypeDefinition template, string key, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template with
        {
            Key = key,
            Version = version,
            LegacyKey = null,
            Base = new DefinitionBase { Template = template.Key, Version = template.Version, Mode = "copy" },
        };
    }

    /// <summary>The platform row of a template.</summary>
    public static DocumentTypeDefinition PlatformDefinition(TypeDefinition template, DateTimeOffset at) => new()
    {
        Id = PlatformDefinitionId(template.Key),
        TenantId = null,
        Key = template.Key,
        Name = new Dictionary<string, string>(template.Name, StringComparer.Ordinal),
        Description = template.Description,
        Origin = DefinitionOrigin.Platform,
        Status = DocumentTypeStatus.Active,
        PublishedVersion = template.Version,
        CreatedAt = at,
        CreatedBy = SeedActor,
        UpdatedAt = at,
    };

    /// <summary>A published version row of the definition <paramref name="definitionId"/>, with its compiled artifacts.</summary>
    public static DocumentTypeVersion PublishedVersion(Guid id, Guid definitionId, Guid? tenantId, CompiledDefinition compiled, DateTimeOffset at, string actor, string? changeNote = null) => new()
    {
        Id = id,
        DefinitionId = definitionId,
        TenantId = tenantId,
        Version = compiled.Definition.Version,
        DefinitionJson = compiled.DefinitionJson,
        CompiledJsonSchema = compiled.JsonSchemaJson,
        CompiledLlmSchema = compiled.LlmSchema.Json,
        LlmSchemaName = compiled.LlmSchema.Name,
        LlmSchemaVersion = compiled.LlmSchema.Version,
        LlmSchemaHash = compiled.LlmSchema.Hash,
        FormSpecJson = compiled.FormSpecJson,
        DescriptorsJson = compiled.Descriptors.ToJson(),
        ContentHash = compiled.ContentHash,
        State = DocumentTypeVersionState.Published,
        CreatedAt = at,
        CreatedBy = actor,
        UpdatedAt = at,
        PublishedAt = at,
        PublishedBy = actor,
        ChangeNote = changeNote,
    };

    /// <summary>Compiles a definition the platform ships; a platform definition that does not compile is a build defect.</summary>
    public static CompiledDefinition Compile(TypeDefinition definition) =>
        DefinitionCompiler.CompileOrThrow(definition, DefinitionSchemaCompiler.Options);

    /// <summary>A version-8 GUID (RFC 9562) from the SHA-256 of <paramref name="name"/>: stable across runs and databases.</summary>
    public static Guid StableGuid(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80); // version 8 (the high nibble of the time_hi field, little-endian in Guid)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes);
    }
}
