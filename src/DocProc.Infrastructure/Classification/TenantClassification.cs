using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Classification;

/// <summary>
/// What a tenant's documents are classified against (docs/11 §9.4): the keyword rules of its published types and the
/// types the LLM classifier may answer, with a hash of the type set (the cache key of the rule table).
/// </summary>
public sealed record TenantClassificationSet(KeywordRuleTable Keywords, IReadOnlyList<ClassificationCandidate> Candidates, string Hash)
{
    /// <summary>The candidates restricted to <paramref name="allowed"/> (a supplier's allowed types), or all of them.</summary>
    public IReadOnlyList<ClassificationCandidate> CandidatesFor(IReadOnlyCollection<string>? allowed) =>
        allowed is { Count: > 0 } ? [.. Candidates.Where(c => allowed.Contains(c.Key, StringComparer.Ordinal))] : Candidates;
}

/// <summary>
/// The tenant keyword set and the LLM classifier's candidates (docs/11 §9.4, P5), with <c>configurableTypes</c> and
/// <c>customTypes</c> on: built from the tenant's Active types with a published version (adopted, copied and its own),
/// each through <see cref="IEffectiveConfigResolver"/>. A standard type whose <c>classification</c> block is its
/// template's keeps the built-in rules exactly; a fork that changed the block, a price list and a tenant's own type get
/// rules from their blocks. Tables are cached process-wide by the hash of the type set (keys and published versions).
/// Without the switches, or for a tenant with no catalogue rows, the answer is null and the classifiers run as before
/// (the built-in table, the fixed classification schema).
/// </summary>
public sealed class TenantClassification(DocProcDbContext db, IEffectiveConfigResolver resolver, ITenantFeatures features)
{
    private const int MaxCachedTables = 256;

    private static readonly ConcurrentDictionary<string, KeywordRuleTable> Tables = new(StringComparer.Ordinal);

    public async Task<TenantClassificationSet?> ResolveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await features.IsEnabledAsync(tenantId, TenantFeature.ConfigurableTypes, cancellationToken).ConfigureAwait(false)
            || !await features.IsEnabledAsync(tenantId, TenantFeature.CustomTypes, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var keys = await db.DocumentTypeDefinitions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.Status == DocumentTypeStatus.Active && d.PublishedVersion != null)
            .OrderBy(d => d.Key)
            .Select(d => d.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return null;
        }

        var sources = new List<KeywordTypeSource>();
        var candidates = new List<ClassificationCandidate>();
        var identity = new StringBuilder();
        foreach (var key in keys)
        {
            var type = DocumentTypeKey.FromStored(key);
            if (type.IsUnknown)
            {
                continue;
            }

            var config = await resolver.ResolveAsync(new EffectiveConfigRequest(tenantId, type), cancellationToken).ConfigureAwait(false);
            if (config.Type is not { } effective)
            {
                continue;
            }

            var standard = UsesStandardRules(type, effective);
            sources.Add(new KeywordTypeSource(type, effective.Definition.Classification, standard));
            candidates.Add(new ClassificationCandidate(key, DescriptionOf(effective.Definition)));
            identity.Append(key).Append('@').Append(effective.VersionId?.ToString() ?? effective.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(standard ? "s" : "d").Append('\n');
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))[..16];
        if (!Tables.TryGetValue(hash, out var table))
        {
            table = KeywordRuleTable.FromTypes(sources);
            if (Tables.Count >= MaxCachedTables)
            {
                Tables.Clear();
            }

            Tables[hash] = table;
        }

        return new TenantClassificationSet(table, candidates, hash);
    }

    /// <summary>
    /// Whether a type keeps the built-in keyword rules: a legacy type (the built-in table has its rules) whose
    /// classification block equals its template's, which the built-in rules are the seed of.
    /// </summary>
    internal static bool UsesStandardRules(DocumentTypeKey type, EffectiveType effective)
    {
        if (!type.IsLegacy || StandardTemplates.Find(effective.TemplateKey) is not { } template)
        {
            return false;
        }

        return string.Equals(
            JsonSerializer.Serialize(effective.Definition.Classification, DefinitionSerializer.Options),
            JsonSerializer.Serialize(template.Classification, DefinitionSerializer.Options),
            StringComparison.Ordinal);
    }

    /// <summary>What the LLM classifier is told about a type: its classification description, else its description, else its name.</summary>
    internal static string DescriptionOf(TypeDefinition definition)
    {
        var name = definition.Name.TryGetValue("en", out var english) ? english : definition.Name.Values.FirstOrDefault() ?? definition.Key;
        var description = definition.Classification?.Description ?? definition.Description;
        return string.IsNullOrWhiteSpace(description) ? name : $"{name}: {description}";
    }
}
