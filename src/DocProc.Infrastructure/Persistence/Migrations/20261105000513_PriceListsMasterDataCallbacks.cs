using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P5 part C (docs/11 §6.7, §6.8, §11, §13.8): the revision columns of a document (<c>SupersededByDocumentId</c>,
    /// <c>RevisionOfDocumentId</c>, <c>RevisionResolution</c>), the review task's <c>Kind</c> (back-filled
    /// <c>Document</c>), the destination's callback secrets, the status poll columns of <c>DocumentExternalRefs</c>, and
    /// the master-data sources and runs under the strict predicate of the re-created policy
    /// <see cref="RowLevelSecurity.PolicyV13"/>. No data moves. The Down drops the policy first (it names the new
    /// tables) and creates <see cref="RowLevelSecurity.PolicyV12"/> after; it closes the open integration-issue tasks
    /// before <c>Kind</c> goes (<see cref="P5DowngradeSql.CloseIntegrationIssues"/>).
    /// </summary>
    public partial class PriceListsMasterDataCallbacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocumentExternalRefs_DestinationId",
                table: "DocumentExternalRefs");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ReviewTasks",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Document");

            migrationBuilder.AddColumn<string>(
                name: "CallbackSecretRef",
                table: "OutputDestinations",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousCallbackSecretRef",
                table: "OutputDestinations",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RevisionOfDocumentId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevisionResolution",
                table: "Documents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByDocumentId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextPollAt",
                table: "DocumentExternalRefs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Polls",
                table: "DocumentExternalRefs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MasterDataSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Transport = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MappingExpression = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    MatchKeys = table.Column<string>(type: "json", nullable: false),
                    ConflictPolicyJson = table.Column<string>(type: "json", nullable: true),
                    ScheduleCron = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastResultJson = table.Column<string>(type: "json", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterDataSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterDataSources_OutputDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "OutputDestinations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterDataSources_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MasterDataRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Trigger = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DryRun = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Created = table.Column<int>(type: "int", nullable: false),
                    Updated = table.Column<int>(type: "int", nullable: false),
                    Unchanged = table.Column<int>(type: "int", nullable: false),
                    Conflicts = table.Column<int>(type: "int", nullable: false),
                    Errors = table.Column<int>(type: "int", nullable: false),
                    ReportJson = table.Column<string>(type: "json", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    By = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterDataRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterDataRuns_MasterDataSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "MasterDataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MasterDataRuns_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentExternalRefs_DestinationId_ExternalId",
                table: "DocumentExternalRefs",
                columns: new[] { "DestinationId", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentExternalRefs_Status_NextPollAt",
                table: "DocumentExternalRefs",
                columns: new[] { "Status", "NextPollAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataRuns_SourceId",
                table: "MasterDataRuns",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataRuns_TenantId",
                table: "MasterDataRuns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataRuns_TenantId_SourceId_At",
                table: "MasterDataRuns",
                columns: new[] { "TenantId", "SourceId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataSources_DestinationId",
                table: "MasterDataSources",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataSources_Enabled_NextRunAt",
                table: "MasterDataSources",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataSources_TenantId",
                table: "MasterDataSources",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataSources_TenantId_Name",
                table: "MasterDataSources",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            // The schema-bound policy is re-created over the new tables.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV13));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names the new tables, so it is dropped first and the earlier policy created after.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            migrationBuilder.DropTable(
                name: "MasterDataRuns");

            migrationBuilder.DropTable(
                name: "MasterDataSources");

            migrationBuilder.DropIndex(
                name: "IX_DocumentExternalRefs_DestinationId_ExternalId",
                table: "DocumentExternalRefs");

            migrationBuilder.DropIndex(
                name: "IX_DocumentExternalRefs_Status_NextPollAt",
                table: "DocumentExternalRefs");

            // Open integration issues would be document review tasks of settled documents to the earlier build.
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(P5DowngradeSql.CloseIntegrationIssues));

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ReviewTasks");

            migrationBuilder.DropColumn(
                name: "CallbackSecretRef",
                table: "OutputDestinations");

            migrationBuilder.DropColumn(
                name: "PreviousCallbackSecretRef",
                table: "OutputDestinations");

            migrationBuilder.DropColumn(
                name: "RevisionOfDocumentId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "RevisionResolution",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "SupersededByDocumentId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "NextPollAt",
                table: "DocumentExternalRefs");

            migrationBuilder.DropColumn(
                name: "Polls",
                table: "DocumentExternalRefs");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentExternalRefs_DestinationId",
                table: "DocumentExternalRefs",
                column: "DestinationId");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV12));
        }
    }
}
