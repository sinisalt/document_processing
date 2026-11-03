using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Outputs;

/// <summary>
/// A platform integration template (docs/11 §13.9): an output mapping every tenant can copy. Shipped as two embedded
/// files, <c>&lt;key&gt;.json</c> (the metadata and the target schema) and <c>&lt;key&gt;.jsonata</c> (the expression).
/// </summary>
public sealed record OutputTemplate(
    string Key,
    int Version,
    string Name,
    string? Description,
    string? SourceTypeKey,
    string Engine,
    string ContentType,
    string Expression,
    string? TargetSchemaJson);

/// <summary>
/// The platform output templates (<c>std.event</c>, <c>generic-ap-json</c>; <c>generic-ap-csv</c> is P6 and
/// <c>generic-article-prices-json</c> P5), their rows (<see cref="OutputMapping"/> with <c>TenantId</c> NULL) and the
/// seeding of them, as the standard document templates are seeded (docs/11 §5.1): the <c>OutputsAndLookups</c>
/// migration writes the rows from a copy frozen with it, <see cref="AddMissingAsync"/> adds what a later build ships
/// on every start.
/// </summary>
public static class OutputTemplates
{
    public const string ResourcePrefix = "DocProc.Infrastructure.OutputTemplates.";

    public const string FrozenResourcePrefix = "DocProc.Infrastructure.Seed.OutputTemplatesV1.";

    private static readonly Lazy<IReadOnlyList<OutputTemplate>> Live = new(() => Load(ResourcePrefix));

    /// <summary>The templates this build ships, in key order.</summary>
    public static IReadOnlyList<OutputTemplate> All => Live.Value;

    public static OutputTemplate? Find(string key) => All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));

    /// <summary>The templates as the migration froze them.</summary>
    public static IReadOnlyList<OutputTemplate> Frozen() => Load(FrozenResourcePrefix);

    /// <summary>The stable id of a template's mapping row.</summary>
    public static Guid PlatformMappingId(string key) => TypeCatalogueRows.StableGuid($"docproc:output-template:{key}");

    /// <summary>The stable id of a template version row.</summary>
    public static Guid PlatformVersionId(string key, int version) => TypeCatalogueRows.StableGuid($"docproc:output-template:{key}@{version}");

    /// <summary>Adds the template rows and versions that are missing, and moves a row to a newer shipped version; returns how many rows it added. The caller saves.</summary>
    public static async Task<int> AddMissingAsync(DocProcDbContext db, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var existing = await db.OutputMappings.IgnoreQueryFilters().Where(m => m.TenantId == null).Include(m => m.Versions)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byKey = existing.ToDictionary(m => m.Key, StringComparer.Ordinal);
        var added = 0;
        foreach (var template in All)
        {
            if (!byKey.TryGetValue(template.Key, out var mapping))
            {
                mapping = new OutputMapping
                {
                    Id = PlatformMappingId(template.Key),
                    TenantId = null,
                    Key = template.Key,
                    Name = template.Name,
                    Description = template.Description,
                    SourceTypeKey = template.SourceTypeKey,
                    Origin = OutputMappingOrigin.Platform,
                    Status = OutputMappingStatus.Active,
                    PublishedVersion = template.Version,
                    CreatedAt = now,
                    CreatedBy = TypeCatalogueRows.SeedActor,
                    UpdatedAt = now,
                };
                db.OutputMappings.Add(mapping);
                byKey[template.Key] = mapping;
                added++;
            }

            if (mapping.Versions.All(v => v.Version != template.Version))
            {
                var version = VersionRow(template, mapping.Id, now);
                mapping.Versions.Add(version);
                if (db.Entry(mapping).State != EntityState.Added)
                {
                    db.OutputMappingVersions.Add(version);
                }

                added++;
            }

            if ((mapping.PublishedVersion ?? 0) < template.Version)
            {
                foreach (var older in mapping.Versions.Where(v => v.State == OutputMappingVersionState.Published && v.Version < template.Version))
                {
                    older.State = OutputMappingVersionState.Deprecated;
                }

                mapping.PublishedVersion = template.Version;
                mapping.Name = template.Name;
                mapping.Description = template.Description;
                mapping.UpdatedAt = now;
            }
        }

        return added;
    }

    /// <summary>The published version row of a template.</summary>
    public static OutputMappingVersion VersionRow(OutputTemplate template, Guid mappingId, DateTimeOffset now) => new()
    {
        Id = PlatformVersionId(template.Key, template.Version),
        MappingId = mappingId,
        TenantId = null,
        Version = template.Version,
        Engine = template.Engine,
        Expression = template.Expression,
        ContentType = template.ContentType,
        TargetSchemaJson = template.TargetSchemaJson,
        ReferencedPathsJson = "[]",
        SampleDocumentIdsJson = "[]",
        State = OutputMappingVersionState.Published,
        CreatedAt = now,
        CreatedBy = TypeCatalogueRows.SeedActor,
        UpdatedAt = now,
        PublishedAt = now,
        PublishedBy = TypeCatalogueRows.SeedActor,
    };

    /// <summary>The <c>OutputsAndLookups</c> migration's data statements: the frozen templates' rows, each only when missing.</summary>
    public static IReadOnlyList<string> SeedStatements()
    {
        var statements = new List<string>();
        foreach (var template in Frozen())
        {
            var version = template.Version.ToString(CultureInfo.InvariantCulture);
            statements.Add($"""
                INSERT INTO [OutputMappings] ([Id], [TenantId], [Key], [Name], [Description], [SourceTypeKey], [Origin], [BaseMappingId], [BaseVersion], [Status], [PublishedVersion], [CreatedAt], [CreatedBy], [UpdatedAt])
                SELECT '{PlatformMappingId(template.Key):D}', NULL, {Text(template.Key)}, {Text(template.Name)}, {Text(template.Description)}, {Text(template.SourceTypeKey)}, N'{nameof(OutputMappingOrigin.Platform)}', NULL, NULL, N'{nameof(OutputMappingStatus.Active)}', {version}, SYSUTCDATETIME(), {Text(TypeCatalogueRows.SeedActor)}, SYSUTCDATETIME()
                WHERE NOT EXISTS (SELECT 1 FROM [OutputMappings] WHERE [TenantId] IS NULL AND [Key] = {Text(template.Key)});
                """);
            statements.Add($"""
                INSERT INTO [OutputMappingVersions] ([Id], [MappingId], [TenantId], [Version], [Engine], [Expression], [ContentType], [FileNameTemplate], [TargetSchemaJson], [ReferencedPathsJson], [SampleDocumentIdsJson], [State], [Revision], [ChangeNote], [CreatedAt], [CreatedBy], [UpdatedAt], [PublishedAt], [PublishedBy])
                SELECT '{PlatformVersionId(template.Key, template.Version):D}', [m].[Id], NULL, {version}, {Text(template.Engine)}, {Text(template.Expression)}, {Text(template.ContentType)}, NULL, {Text(template.TargetSchemaJson)}, N'[]', N'[]', N'{nameof(OutputMappingVersionState.Published)}', 0, NULL, SYSUTCDATETIME(), {Text(TypeCatalogueRows.SeedActor)}, SYSUTCDATETIME(), SYSUTCDATETIME(), {Text(TypeCatalogueRows.SeedActor)}
                FROM [OutputMappings] AS [m]
                WHERE [m].[TenantId] IS NULL AND [m].[Key] = {Text(template.Key)}
                  AND NOT EXISTS (SELECT 1 FROM [OutputMappingVersions] AS [e] WHERE [e].[MappingId] = [m].[Id] AND [e].[Version] = {version});
                """);
        }

        return statements;
    }

    private static List<OutputTemplate> Load(string prefix)
    {
        var assembly = typeof(OutputTemplates).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var result = new List<OutputTemplate>();
        foreach (var name in names.Where(n => n.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var meta = JsonNode.Parse(Read(name))!.AsObject();
            var key = meta["key"]!.GetValue<string>();
            var expressionName = name[..^".json".Length] + ".jsonata";
            if (!names.Contains(expressionName))
            {
                throw new InvalidOperationException($"The output template '{key}' has no expression ({expressionName}).");
            }

            result.Add(new OutputTemplate(
                key,
                meta["version"]!.GetValue<int>(),
                meta["name"]!.GetValue<string>(),
                meta["description"]?.GetValue<string>(),
                meta["sourceTypeKey"]?.GetValue<string>(),
                meta["engine"]!.GetValue<string>(),
                meta["contentType"]!.GetValue<string>(),
                Read(expressionName).TrimEnd(),
                meta["targetSchema"] is JsonObject schema ? schema.ToJsonString() : null));
        }

        return result;

        string Read(string resource)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    private static string Text(string? value) => value is null ? "NULL" : "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
