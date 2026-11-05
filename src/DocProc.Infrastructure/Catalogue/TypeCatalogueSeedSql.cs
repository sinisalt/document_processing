using System.Globalization;
using System.Text;
using System.Text.Json;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Infrastructure.Catalogue;

/// <summary>
/// The data statements of the <c>TypeCatalogue</c> migration (docs/11 §21 steps 2 and 3), as T-SQL: the platform
/// template rows, every existing tenant's adopted types under the legacy keys (version 1, published), and the
/// back-fill of <c>Documents.DocumentTypeVersionId</c> and <c>Corrections.DocumentTypeVersionId</c>. Each statement
/// is idempotent (it inserts only what is missing and updates only nulls) and runs through
/// <c>RowLevelSecurity.InPlatformScope</c> in the migration.
/// <para>
/// The templates come from a copy frozen with the migration (<c>Persistence/Migrations/Seed/TypeCatalogueV1</c>), not
/// from the live <see cref="StandardTemplates"/>: the SQL is generated when the migration runs, and a template edited
/// later must not change what this migration writes on a fresh database. A later template version is added by
/// <see cref="TypeCatalogueSeeder"/> (and a published version must never change in place; the template tests hold
/// the live templates of version 1 to these copies).
/// </para>
/// </summary>
public static class TypeCatalogueSeedSql
{
    public const string ResourcePrefix = "DocProc.Infrastructure.Seed.TypeCatalogueV1.";

    /// <summary>The templates frozen with the migration, in key order.</summary>
    public static IReadOnlyList<(TypeDefinition Template, string Json)> FrozenTemplates()
    {
        var assembly = typeof(TypeCatalogueSeedSql).Assembly;
        var result = new List<(TypeDefinition, string)>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            result.Add((DefinitionSerializer.Parse(json), json));
        }

        return result;
    }

    /// <summary>The statements, in order: platform rows, adopted rows per tenant, then the two back-fills.</summary>
    public static IReadOnlyList<string> Statements()
    {
        var statements = new List<string>();
        var templates = FrozenTemplates();
        foreach (var (template, _) in templates)
        {
            var compiled = TypeCatalogueRows.Compile(template);
            var definitionId = TypeCatalogueRows.PlatformDefinitionId(template.Key);
            statements.Add($"""
                INSERT INTO [DocumentTypeDefinitions] ([Id], [TenantId], [Key], [Name], [Description], [Origin], [BaseDefinitionId], [BaseVersion], [Status], [PublishedVersion], [CreatedAt], [CreatedBy], [UpdatedAt])
                SELECT {Guid(definitionId)}, NULL, {Text(template.Key)}, {Text(Name(template))}, {Text(template.Description)}, N'Platform', NULL, NULL, N'{nameof(DocumentTypeStatus.Active)}', {template.Version.ToString(CultureInfo.InvariantCulture)}, SYSUTCDATETIME(), {Text(TypeCatalogueRows.SeedActor)}, SYSUTCDATETIME()
                WHERE NOT EXISTS (SELECT 1 FROM [DocumentTypeDefinitions] WHERE [TenantId] IS NULL AND [Key] = {Text(template.Key)});
                """);
            statements.Add(InsertVersion(
                $"SELECT {Guid(TypeCatalogueRows.PlatformVersionId(template.Key, template.Version))}, [d].[Id], NULL",
                compiled,
                $"FROM [DocumentTypeDefinitions] AS [d] WHERE [d].[TenantId] IS NULL AND [d].[Key] = {Text(template.Key)}",
                changeNote: null));
        }

        foreach (var (template, _) in templates.Where(t => t.Template.LegacyKey is not null))
        {
            var key = template.LegacyKey!;
            statements.Add($"""
                INSERT INTO [DocumentTypeDefinitions] ([Id], [TenantId], [Key], [Name], [Description], [Origin], [BaseDefinitionId], [BaseVersion], [Status], [PublishedVersion], [CreatedAt], [CreatedBy], [UpdatedAt])
                SELECT NEWID(), [t].[Id], {Text(key)}, {Text(Name(template))}, {Text(template.Description)}, N'Adopted', {Guid(TypeCatalogueRows.PlatformDefinitionId(template.Key))}, {template.Version.ToString(CultureInfo.InvariantCulture)}, N'{nameof(DocumentTypeStatus.Active)}', 1, SYSUTCDATETIME(), {Text(TypeCatalogueRows.SeedActor)}, SYSUTCDATETIME()
                FROM [Tenants] AS [t]
                WHERE NOT EXISTS (SELECT 1 FROM [DocumentTypeDefinitions] AS [x] WHERE [x].[TenantId] = [t].[Id] AND [x].[Key] = {Text(key)});
                """);
            statements.Add(InsertVersion(
                "SELECT NEWID(), [d].[Id], [d].[TenantId]",
                TypeCatalogueRows.Compile(TypeCatalogueRows.Adopted(template, key)),
                $"FROM [DocumentTypeDefinitions] AS [d] WHERE [d].[TenantId] IS NOT NULL AND [d].[Key] = {Text(key)} AND [d].[Origin] = N'Adopted'",
                changeNote: $"Adopted {template.Key} v{template.Version}"));
        }

        // docs/11 §21 step 3: documents pin the adopted v1 of their type; Unknown (and any type without a row) stays NULL.
        statements.Add("""
            UPDATE [doc] SET [doc].[DocumentTypeVersionId] = [v].[Id]
            FROM [Documents] AS [doc]
            INNER JOIN [DocumentTypeDefinitions] AS [d] ON [d].[TenantId] = [doc].[TenantId] AND [d].[Key] = [doc].[Type] AND [d].[Origin] = N'Adopted'
            INNER JOIN [DocumentTypeVersions] AS [v] ON [v].[DefinitionId] = [d].[Id] AND [v].[Version] = 1
            WHERE [doc].[DocumentTypeVersionId] IS NULL;
            """);
        statements.Add("""
            UPDATE [c] SET [c].[DocumentTypeVersionId] = [doc].[DocumentTypeVersionId]
            FROM [Corrections] AS [c]
            INNER JOIN [Documents] AS [doc] ON [doc].[Id] = [c].[DocumentId]
            WHERE [c].[DocumentTypeVersionId] IS NULL AND [doc].[DocumentTypeVersionId] IS NOT NULL;
            """);
        return statements;
    }

    private static string InsertVersion(string select, CompiledDefinition compiled, string from, string? changeNote) => $"""
        INSERT INTO [DocumentTypeVersions] ([Id], [DefinitionId], [TenantId], [Version], [DefinitionJson], [CompiledJsonSchema], [CompiledLlmSchema], [LlmSchemaName], [LlmSchemaVersion], [LlmSchemaHash], [FormSpecJson], [DescriptorsJson], [ContentHash], [State], [Revision], [CreatedAt], [CreatedBy], [UpdatedAt], [PublishedAt], [PublishedBy], [ChangeNote])
        {select}, {compiled.Definition.Version.ToString(CultureInfo.InvariantCulture)}, {Text(compiled.DefinitionJson)}, {Text(compiled.JsonSchemaJson)}, {Text(compiled.LlmSchema.Json)}, {Text(compiled.LlmSchema.Name)}, {Text(compiled.LlmSchema.Version)}, {Text(compiled.LlmSchema.Hash)}, {Text(compiled.FormSpecJson)}, {Text(compiled.Descriptors.ToJson())}, {Text(compiled.ContentHash)}, N'{nameof(DocumentTypeVersionState.Published)}', 0, SYSUTCDATETIME(), {Text(TypeCatalogueRows.SeedActor)}, SYSUTCDATETIME(), SYSUTCDATETIME(), {Text(TypeCatalogueRows.SeedActor)}, {Text(changeNote)}
        {from}
          AND NOT EXISTS (SELECT 1 FROM [DocumentTypeVersions] AS [e] WHERE [e].[DefinitionId] = [d].[Id] AND [e].[Version] = {compiled.Definition.Version.ToString(CultureInfo.InvariantCulture)});
        """;

    private static string Name(TypeDefinition template) => JsonSerializer.Serialize(template.Name.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), NameJson);

    private static readonly JsonSerializerOptions NameJson = new(JsonSerializerDefaults.Web);

    private static string Guid(Guid value) => $"'{value:D}'";

    private static string Text(string? value)
    {
        if (value is null)
        {
            return "NULL";
        }

        var builder = new StringBuilder("N'");
        builder.Append(value.Replace("'", "''", StringComparison.Ordinal));
        builder.Append('\'');
        return builder.ToString();
    }
}
