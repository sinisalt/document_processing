namespace DocProc.Infrastructure.Persistence.Migrations;

/// <summary>
/// The data steps of the P5 Down methods (Correction (P5) fix1 of docs/11 §23): what P5 wrote into tables and columns
/// the build before P5 reads, mapped back to something that build reads. Its readers refuse a member they do not know
/// (definitions, supplier settings) and an enum value they do not know (the canonical tree's <c>source.method</c>), so
/// a downgrade without these steps leaves tenants whose price lists no longer resolve, whose supplier settings read as
/// empty, and whose documents do not load. What cannot be mapped back is listed in docs/07 (P5, "roll-forward only").
/// SQL Server only, run between the policy drop and its re-creation (the history tables are under the policy, and
/// system versioning cannot be switched off while it names them).
/// </summary>
internal static class P5DowngradeSql
{
    /// <summary>
    /// A definition without its definition flags: the top-level <c>flags</c> and every rule's <c>flag</c> are removed (the
    /// rule raises <c>RULE_FAILED</c> again, as before P5). Rules keep their order.
    /// </summary>
    private static string WithoutFlags(string column) => $"""
        CASE WHEN JSON_QUERY(CAST({column} AS nvarchar(max)), N'$.rules') IS NULL
             THEN JSON_MODIFY(CAST({column} AS nvarchar(max)), N'$.flags', NULL)
             ELSE JSON_MODIFY(
                    JSON_MODIFY(CAST({column} AS nvarchar(max)), N'$.flags', NULL),
                    N'$.rules',
                    JSON_QUERY(ISNULL(
                        (SELECT N'[' + STRING_AGG(CAST(JSON_MODIFY(r.[value], N'$.flag', NULL) AS nvarchar(max)), N',') WITHIN GROUP (ORDER BY CAST(r.[key] AS int)) + N']'
                         FROM OPENJSON(CAST({column} AS nvarchar(max)), N'$.rules') AS r),
                        N'[]')))
        END
        """;

    private static string HasFlags(string column) => $"""
        (JSON_QUERY(CAST({column} AS nvarchar(max)), N'$.flags') IS NOT NULL
         OR EXISTS (SELECT 1 FROM OPENJSON(CAST({column} AS nvarchar(max)), N'$.rules') AS r WHERE JSON_VALUE(r.[value], N'$.flag') IS NOT NULL))
        """;

    /// <summary>
    /// <paramref name="statement"/> on the history table of a temporal table, which the system alone writes: system
    /// versioning is switched off for it and on again, unless it is off already (a column drop of the same migration
    /// switched it off and switches it on at the end).
    /// </summary>
    public static string InHistory(string table, string history, string statement) => $"""
        DECLARE @versioned bit = CASE WHEN OBJECTPROPERTY(OBJECT_ID(N'dbo.{table}'), N'TableTemporalType') = 2 THEN 1 ELSE 0 END;
        IF @versioned = 1 ALTER TABLE [dbo].[{table}] SET (SYSTEM_VERSIONING = OFF);
        EXEC(N'{statement.Replace("'", "''", StringComparison.Ordinal)}');
        IF @versioned = 1 ALTER TABLE [dbo].[{table}] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[{history}]));
        """;

    /// <summary>The definition flags out of every stored definition (P5 part B).</summary>
    public static string StripDefinitionFlags(string table) =>
        $"UPDATE [{table}] SET [DefinitionJson] = {WithoutFlags("[DefinitionJson]")} WHERE {HasFlags("[DefinitionJson]")};";

    /// <summary>
    /// The adopted types P5 moved onto a newer template version (the start-up upgrade of docs/11 §5.8, P5 part C, or a
    /// <c>:upgrade-base</c>) go back to the version they had before it: the newest earlier version whose <c>base</c> is a
    /// template version the earlier build ships (1) is Published again, the versions after it are Deprecated, the
    /// documents pinned to those follow the restored version, and the type names that version and its base again. A type
    /// with no such earlier version (adopted under P5) is left as it is (docs/07, roll-forward only). The platform rows
    /// of the templates P5 gave a second version publish their first version again.
    /// </summary>
    public const string RestoreAdoptedTypes =
        """
        DECLARE @restore TABLE ([DefinitionId] uniqueidentifier PRIMARY KEY, [Previous] int NOT NULL, [PreviousId] uniqueidentifier NOT NULL, [PreviousBase] int NOT NULL);
        INSERT INTO @restore ([DefinitionId], [Previous], [PreviousId], [PreviousBase])
        SELECT d.[Id], p.[Version], p.[Id], p.[Base]
        FROM [DocumentTypeDefinitions] AS d
        CROSS APPLY (
            SELECT TOP (1) v.[Version], v.[Id], ISNULL(TRY_CAST(JSON_VALUE(CAST(v.[DefinitionJson] AS nvarchar(max)), N'$.base.version') AS int), 1) AS [Base]
            FROM [DocumentTypeVersions] AS v
            WHERE v.[DefinitionId] = d.[Id] AND v.[Version] < d.[PublishedVersion] AND v.[State] IN (N'Published', N'Deprecated')
              AND ISNULL(TRY_CAST(JSON_VALUE(CAST(v.[DefinitionJson] AS nvarchar(max)), N'$.base.version') AS int), 1) < 2
            ORDER BY v.[Version] DESC) AS p
        WHERE d.[TenantId] IS NOT NULL AND d.[Origin] = N'Adopted' AND d.[BaseVersion] >= 2;

        UPDATE doc SET [DocumentTypeVersionId] = r.[PreviousId]
        FROM [Documents] AS doc
        JOIN [DocumentTypeVersions] AS v ON v.[Id] = doc.[DocumentTypeVersionId]
        JOIN @restore AS r ON r.[DefinitionId] = v.[DefinitionId]
        WHERE v.[Version] > r.[Previous];

        UPDATE v SET [State] = N'Deprecated'
        FROM [DocumentTypeVersions] AS v
        JOIN @restore AS r ON r.[DefinitionId] = v.[DefinitionId]
        WHERE v.[Version] > r.[Previous] AND v.[State] = N'Published';

        UPDATE v SET [State] = N'Published'
        FROM [DocumentTypeVersions] AS v
        JOIN @restore AS r ON r.[PreviousId] = v.[Id];

        UPDATE d SET [PublishedVersion] = r.[Previous], [BaseVersion] = r.[PreviousBase]
        FROM [DocumentTypeDefinitions] AS d
        JOIN @restore AS r ON r.[DefinitionId] = d.[Id];

        UPDATE v SET [State] = N'Deprecated'
        FROM [DocumentTypeVersions] AS v
        JOIN [DocumentTypeDefinitions] AS d ON d.[Id] = v.[DefinitionId]
        WHERE d.[TenantId] IS NULL AND v.[Version] >= 2 AND v.[State] = N'Published'
          AND EXISTS (SELECT 1 FROM [DocumentTypeVersions] AS first WHERE first.[DefinitionId] = d.[Id] AND first.[Version] = 1);

        UPDATE v SET [State] = N'Published'
        FROM [DocumentTypeVersions] AS v
        JOIN [DocumentTypeDefinitions] AS d ON d.[Id] = v.[DefinitionId]
        WHERE d.[TenantId] IS NULL AND d.[PublishedVersion] >= 2 AND v.[Version] = 1;

        UPDATE d SET [PublishedVersion] = 1
        FROM [DocumentTypeDefinitions] AS d
        WHERE d.[TenantId] IS NULL AND d.[PublishedVersion] >= 2
          AND EXISTS (SELECT 1 FROM [DocumentTypeVersions] AS first WHERE first.[DefinitionId] = d.[Id] AND first.[Version] = 1);
        """;

    /// <summary>The pipeline overrides out of the supplier and group settings (P5 part A); the other settings stay.</summary>
    public const string RemovePipelineOverrides =
        """
        UPDATE [Suppliers] SET [SettingsJson] = JSON_MODIFY(CAST([SettingsJson] AS nvarchar(max)), N'$.pipelineOverrides', NULL)
        WHERE JSON_QUERY(CAST([SettingsJson] AS nvarchar(max)), N'$.pipelineOverrides') IS NOT NULL;
        UPDATE [SupplierGroups] SET [SettingsJson] = JSON_MODIFY(CAST([SettingsJson] AS nvarchar(max)), N'$.pipelineOverrides', NULL)
        WHERE JSON_QUERY(CAST([SettingsJson] AS nvarchar(max)), N'$.pipelineOverrides') IS NOT NULL;
        """;

    /// <summary>The source method an <c>http-lookup</c> wrote (P5 part A), as stored, and what the earlier build reads instead.</summary>
    public const string LookupMethod = "\"method\":\"Lookup\"";

    public const string ProfileMethod = "\"method\":\"Profile\"";

    /// <summary>
    /// The values an <c>http-lookup</c> wrote keep their value and become <c>Profile</c> values (the earlier build's
    /// "read by configuration"), in a document's canonical tree.
    /// </summary>
    public static string MapLookupSources(string table) =>
        $"UPDATE [{table}] SET [CanonicalJson] = REPLACE(CAST([CanonicalJson] AS nvarchar(max)), N'{LookupMethod}', N'{ProfileMethod}') " +
        $"WHERE CHARINDEX(N'{LookupMethod}', CAST([CanonicalJson] AS nvarchar(max))) > 0;";

    /// <summary>
    /// Open integration issues (P5 part C) are closed as skipped before <c>ReviewTasks.Kind</c> goes: the earlier build
    /// would take them for document review tasks of settled documents. The <c>EXTERNAL_REJECTED</c> flags stay.
    /// </summary>
    public const string CloseIntegrationIssues =
        """
        UPDATE [ReviewTasks] SET [Status] = N'Skipped', [CompletedAt] = SYSDATETIMEOFFSET()
        WHERE [Kind] = N'IntegrationIssue' AND [Status] IN (N'Open', N'InProgress');
        """;
}
