using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P5 part A (docs/11 §10, §16): the hash of the pipeline a document ran (<c>Documents.PipelineHash</c>), the pipeline
    /// snapshots by hash, the results of <c>http-lookup</c> calls and the tenants' LLM budgets, under the strict predicate
    /// of the re-created policy <see cref="RowLevelSecurity.PolicyV11"/>. No data moves: the column is nullable and the
    /// tables are new (the <c>configurableSteps</c> switch lives in the settings JSON and reads as off where it is not
    /// stored, and is back-filled off in every tenant's flags). The Down drops the policy first (it names the new tables) and creates <see cref="RowLevelSecurity.PolicyV10"/> after.
    /// </summary>
    public partial class ConfigurableStepsAndBudgets : Migration
    {
        /// <summary>
        /// Every tenant's feature flags get <c>ConfigurableSteps</c>, off (docs/11 §3): EF updates the owned JSON object
        /// in place, so the stored flags carry every member the model has. Frozen with this migration.
        /// </summary>
        public const string BackfillFlagSql =
            """
            UPDATE [Tenants] SET [Settings] = JSON_MODIFY(CAST([Settings] AS nvarchar(max)), N'$.Features.ConfigurableSteps', CAST(0 AS bit))
            WHERE JSON_QUERY(CAST([Settings] AS nvarchar(max)), N'$.Features') IS NOT NULL
              AND JSON_VALUE(CAST([Settings] AS nvarchar(max)), N'$.Features.ConfigurableSteps') IS NULL;
            """;

        /// <summary>The Down of <see cref="BackfillFlagSql"/>: the flag goes from every tenant's settings.</summary>
        public const string RemoveFlagSql =
            """
            UPDATE [Tenants] SET [Settings] = JSON_MODIFY(CAST([Settings] AS nvarchar(max)), N'$.Features.ConfigurableSteps', NULL)
            WHERE JSON_VALUE(CAST([Settings] AS nvarchar(max)), N'$.Features.ConfigurableSteps') IS NOT NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PipelineHash",
                table: "Documents",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LlmBudgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LimitUsd = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmBudgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlmBudgets_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LookupResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    AssignmentsJson = table.Column<string>(type: "json", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookupResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LookupResults_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LookupResults_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PipelineSnapshots",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StepsJson = table.Column<string>(type: "json", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineSnapshots", x => new { x.TenantId, x.Hash });
                    table.ForeignKey(
                        name: "FK_PipelineSnapshots_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LlmBudgets_TenantId",
                table: "LlmBudgets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmBudgets_TenantId_Period",
                table: "LlmBudgets",
                columns: new[] { "TenantId", "Period" });

            migrationBuilder.CreateIndex(
                name: "IX_LookupResults_DocumentId",
                table: "LookupResults",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_LookupResults_TenantId",
                table: "LookupResults",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LookupResults_TenantId_DocumentId_StepId_RequestHash",
                table: "LookupResults",
                columns: new[] { "TenantId", "DocumentId", "StepId", "RequestHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSnapshots_TenantId",
                table: "PipelineSnapshots",
                column: "TenantId");

            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(BackfillFlagSql));

            // The schema-bound policy is re-created over the new tables.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV11));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names the new tables, so it is dropped first and the earlier policy created after.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            migrationBuilder.DropTable(
                name: "LlmBudgets");

            migrationBuilder.DropTable(
                name: "LookupResults");

            migrationBuilder.DropTable(
                name: "PipelineSnapshots");

            migrationBuilder.DropColumn(
                name: "PipelineHash",
                table: "Documents");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV10));
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(RemoveFlagSql));
        }
    }
}
