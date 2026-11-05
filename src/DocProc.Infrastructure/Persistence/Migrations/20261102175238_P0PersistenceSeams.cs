using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The persistence half of the P0 safety seams (docs/11 §21 step 1): the ingestion actor split
    /// (<c>SourceSender</c>, <c>SubmittedBy</c>, back-filled from <c>SourceFrom</c> for uploads, docs/11 §21 step 4),
    /// the correction operation and source (<c>Op</c>, <c>SourceJson</c>; old rows keep <c>set</c> and no source), and
    /// the platform-row predicate of row-level security (docs/11 §6.10), which also puts <c>ExtractorProfiles</c> under
    /// the policy. Additive only: every new column is nullable or has a default, so it runs on a live database.
    /// </summary>
    public partial class P0PersistenceSeams : Migration
    {
        /// <summary>The flags object as EF writes it (CLR property names), every flag off. Frozen with this migration.</summary>
        public const string AllFlagsOff =
            """{"ConfigurableTypes":false,"CustomTypes":false,"Outputs":false,"SourceRouting":false,"StructuredSources":false,"SupplierLayouts":false}""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceSender",
                table: "Ingestions",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmittedBy",
                table: "Ingestions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Op",
                table: "Corrections",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "set");

            migrationBuilder.AddColumn<string>(
                name: "SourceJson",
                table: "Corrections",
                type: "json",
                nullable: true);

            // For uploads SourceFrom held the actor, not a sender (docs/11 §21 step 4); mailbox rows keep SubmittedBy
            // null, and SourceSender stays null for every historic row. Ingestions sit behind the policy, which neither
            // migration path opens, hence the platform scope.
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(
                """
                UPDATE [i] SET [i].[SubmittedBy] = LEFT([i].[SourceFrom], 200)
                FROM [Ingestions] AS [i]
                INNER JOIN [IngestionChannels] AS [c] ON [c].[Id] = [i].[ChannelId]
                WHERE [c].[Type] IN (N'ApiUpload', N'PortalUpload')
                  AND [i].[SourceFrom] IS NOT NULL
                  AND [i].[SubmittedBy] IS NULL;
                """));

            // Every tenant's settings get the feature flags object, all off (docs/11 §3). EF can only update a nested
            // owned JSON object in place: on a row without it, a change is lost or fails (see TenantSettings.Features).
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(
                $"""
                UPDATE [Tenants] SET [Settings] = JSON_MODIFY(CAST([Settings] AS nvarchar(max)), N'$.Features', JSON_QUERY(N'{AllFlagsOff}'))
                WHERE JSON_QUERY(CAST([Settings] AS nvarchar(max)), N'$.Features') IS NULL;
                """));

            // Re-created with the frozen lists of this migration: the strict tables as before, plus the platform-row tables.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PipelineProtectedTables, RowLevelSecurity.PlatformRowTablesV1));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PipelineProtectedTables));

            migrationBuilder.DropColumn(
                name: "SourceSender",
                table: "Ingestions");

            migrationBuilder.DropColumn(
                name: "SubmittedBy",
                table: "Ingestions");

            migrationBuilder.DropColumn(
                name: "Op",
                table: "Corrections");

            migrationBuilder.DropColumn(
                name: "SourceJson",
                table: "Corrections");
        }
    }
}
