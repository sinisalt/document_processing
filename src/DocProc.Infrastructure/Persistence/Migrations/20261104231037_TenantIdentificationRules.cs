using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P5 part B (docs/11 §6.6, §8.5): the tenant fallback identification rules (<c>TypeIdentificationRules</c>) and their
    /// versions, and the version a document was typed by (<c>Documents.IdentificationRuleVersionId</c>), under the strict
    /// predicate of the re-created policy <see cref="RowLevelSecurity.PolicyV12"/>. No data moves: the column is nullable
    /// and the tables are new. The Down drops the policy first (it names the new tables) and creates
    /// <see cref="RowLevelSecurity.PolicyV11"/> after.
    /// </summary>
    public partial class TenantIdentificationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IdentificationRuleVersionId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TypeIdentificationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentTypeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ActiveVersion = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedFromDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Matched = table.Column<int>(type: "int", nullable: false),
                    Overridden = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TypeIdentificationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TypeIdentificationRules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TypeIdentificationRuleVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RuleJson = table.Column<string>(type: "json", nullable: false),
                    RuleHash = table.Column<string>(type: "nchar(8)", fixedLength: true, maxLength: 8, nullable: false),
                    ExcludedSupplierIdsJson = table.Column<string>(type: "json", nullable: true),
                    TestJson = table.Column<string>(type: "json", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TypeIdentificationRuleVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TypeIdentificationRuleVersions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TypeIdentificationRuleVersions_TypeIdentificationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "TypeIdentificationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_IdentificationRuleVersionId",
                table: "Documents",
                column: "IdentificationRuleVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TypeIdentificationRuleVersions_RuleId_Version",
                table: "TypeIdentificationRuleVersions",
                columns: new[] { "RuleId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeIdentificationRuleVersions_TenantId",
                table: "TypeIdentificationRuleVersions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TypeIdentificationRuleVersions_TenantId_State",
                table: "TypeIdentificationRuleVersions",
                columns: new[] { "TenantId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_TypeIdentificationRules_TenantId",
                table: "TypeIdentificationRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TypeIdentificationRules_TenantId_DocumentTypeKey",
                table: "TypeIdentificationRules",
                columns: new[] { "TenantId", "DocumentTypeKey" });

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_TypeIdentificationRuleVersions_IdentificationRuleVersionId",
                table: "Documents",
                column: "IdentificationRuleVersionId",
                principalTable: "TypeIdentificationRuleVersions",
                principalColumn: "Id");

            // The schema-bound policy is re-created over the new tables.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV12));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names the new tables, so it is dropped first and the earlier policy created after.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            migrationBuilder.DropForeignKey(
                name: "FK_Documents_TypeIdentificationRuleVersions_IdentificationRuleVersionId",
                table: "Documents");

            migrationBuilder.DropTable(
                name: "TypeIdentificationRuleVersions");

            migrationBuilder.DropTable(
                name: "TypeIdentificationRules");

            migrationBuilder.DropIndex(
                name: "IX_Documents_IdentificationRuleVersionId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "IdentificationRuleVersionId",
                table: "Documents");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV11));
        }
    }
}
