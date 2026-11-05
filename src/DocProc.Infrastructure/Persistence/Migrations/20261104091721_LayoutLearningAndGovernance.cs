using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P4 part C (docs/11 §6.9, §8.3, §12.10): the Candidate layout version a <c>NEW_LAYOUT</c> document waits for
    /// (<c>Documents.PendingLayoutVersionId</c>), the golden documents, the sandbox and regression runs and the
    /// notifications, under the strict predicate of the re-created policy <see cref="RowLevelSecurity.PolicyV10"/>. No
    /// data moves: the new column is nullable and the tables are new (the tenant setting <c>LayoutAgreementPercent</c>
    /// lives in the settings JSON and needs no column). The Down drops the policy first (it names the new tables) and
    /// creates <see cref="RowLevelSecurity.PolicyV9"/> after.
    /// </summary>
    public partial class LayoutLearningAndGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PendingLayoutVersionId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GoldenDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TypeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplierLayoutVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpectedValuesJson = table.Column<string>(type: "json", nullable: false),
                    ExpectedOutputsJson = table.Column<string>(type: "json", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AddedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoldenDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoldenDocuments_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GoldenDocuments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Audience = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DedupeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    PayloadJson = table.Column<string>(type: "json", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RegressionRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CandidateConfigJson = table.Column<string>(type: "json", nullable: false),
                    ResultJson = table.Column<string>(type: "json", nullable: false),
                    Passed = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegressionRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegressionRuns_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_PendingLayoutVersionId",
                table: "Documents",
                columns: new[] { "TenantId", "PendingLayoutVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_GoldenDocuments_DocumentId",
                table: "GoldenDocuments",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_GoldenDocuments_TenantId",
                table: "GoldenDocuments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_GoldenDocuments_TenantId_DocumentId",
                table: "GoldenDocuments",
                columns: new[] { "TenantId", "DocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoldenDocuments_TenantId_SupplierId_TypeKey",
                table: "GoldenDocuments",
                columns: new[] { "TenantId", "SupplierId", "TypeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId",
                table: "Notifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId_Audience_ResolvedAt",
                table: "Notifications",
                columns: new[] { "TenantId", "Audience", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId_DedupeKey",
                table: "Notifications",
                columns: new[] { "TenantId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegressionRuns_TenantId",
                table: "RegressionRuns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RegressionRuns_TenantId_CreatedAt",
                table: "RegressionRuns",
                columns: new[] { "TenantId", "CreatedAt" });

            // The schema-bound policy is re-created over the new tables.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV10));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names the new tables, so it is dropped first and the earlier policy created after.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            migrationBuilder.DropTable(
                name: "GoldenDocuments");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "RegressionRuns");

            migrationBuilder.DropIndex(
                name: "IX_Documents_TenantId_PendingLayoutVersionId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "PendingLayoutVersionId",
                table: "Documents");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV9));
        }
    }
}
