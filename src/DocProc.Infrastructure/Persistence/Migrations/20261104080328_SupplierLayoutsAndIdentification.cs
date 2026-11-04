using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P4 part B (docs/11 §6.6): the supplier layouts and their versions (identification rule, field mapping, lifecycle,
    /// review warm-up and counters) under the strict predicate of the re-created policy
    /// <see cref="RowLevelSecurity.PolicyV9"/>, and the layout version a document was identified by
    /// (<c>Documents.SupplierLayoutVersionId</c>). No data moves: the new column is nullable and the tables are new. The
    /// Down maps the type sources <c>Identification</c> and <c>Proposed</c> back to <c>Rule</c>, which the build before
    /// reads.
    /// </summary>
    public partial class SupplierLayoutsAndIdentification : Migration
    {
        // Frozen: the stored enum names of P4 part B that the Down maps back (renaming the members later must not change them).
        private const string LayoutTypeSources = "N'Identification', N'Proposed'";
        private const string EarlierTypeSource = "Rule";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupplierLayoutVersionId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SupplierLayouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentTypeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LayoutKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReceivingEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ItemKinds = table.Column<string>(type: "json", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ActiveVersion = table.Column<int>(type: "int", nullable: true),
                    ShadowVersion = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedFromDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastMatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierLayouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierLayouts_ReceivingEntities_ReceivingEntityId",
                        column: x => x.ReceivingEntityId,
                        principalTable: "ReceivingEntities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SupplierLayouts_SupplierGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "SupplierGroups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SupplierLayouts_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupplierLayouts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SupplierLayoutVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LayoutId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    BuiltAgainstTypeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdentificationJson = table.Column<string>(type: "json", nullable: false),
                    IdentificationHash = table.Column<string>(type: "nchar(8)", fixedLength: true, maxLength: 8, nullable: false),
                    MappingJson = table.Column<string>(type: "json", nullable: false),
                    CoverageJson = table.Column<string>(type: "json", nullable: true),
                    SourceDocumentIdsJson = table.Column<string>(type: "json", nullable: true),
                    ReplayJson = table.Column<string>(type: "json", nullable: true),
                    SimilarityProfileJson = table.Column<string>(type: "json", nullable: true),
                    ReviewWarmup = table.Column<int>(type: "int", nullable: false),
                    WarmupSeen = table.Column<int>(type: "int", nullable: false),
                    Identified = table.Column<int>(type: "int", nullable: false),
                    Applied = table.Column<int>(type: "int", nullable: false),
                    Agreed = table.Column<int>(type: "int", nullable: false),
                    Corrected = table.Column<int>(type: "int", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierLayoutVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierLayoutVersions_SupplierLayouts_LayoutId",
                        column: x => x.LayoutId,
                        principalTable: "SupplierLayouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupplierLayoutVersions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_SupplierLayoutVersionId",
                table: "Documents",
                column: "SupplierLayoutVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLayoutVersions_LayoutId_Version",
                table: "SupplierLayoutVersions",
                columns: new[] { "LayoutId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLayoutVersions_TenantId",
                table: "SupplierLayoutVersions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLayouts_GroupId",
                table: "SupplierLayouts",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLayouts_ReceivingEntityId",
                table: "SupplierLayouts",
                column: "ReceivingEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLayouts_SupplierId",
                table: "SupplierLayouts",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLayouts_TenantId",
                table: "SupplierLayouts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLayouts_TenantId_Scope_OwnerId_DocumentTypeKey_LayoutKey",
                table: "SupplierLayouts",
                columns: new[] { "TenantId", "Scope", "OwnerId", "DocumentTypeKey", "LayoutKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_SupplierLayoutVersions_SupplierLayoutVersionId",
                table: "Documents",
                column: "SupplierLayoutVersionId",
                principalTable: "SupplierLayoutVersions",
                principalColumn: "Id");

            // The schema-bound policy is re-created over the new tables.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV9));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names the new tables, so it is dropped first and the earlier policy created after.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            // The build before reads neither the Identification nor the Proposed type source: both become a rule's, in
            // the table and in its history, which only the system writes, so system versioning is off for that statement.
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(
                $"UPDATE [Documents] SET [TypeSource] = N'{EarlierTypeSource}' WHERE [TypeSource] IN ({LayoutTypeSources});"));
            migrationBuilder.Sql("ALTER TABLE [Documents] SET (SYSTEM_VERSIONING = OFF);");
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(
                $"UPDATE [DocumentsHistory] SET [TypeSource] = N'{EarlierTypeSource}' WHERE [TypeSource] IN ({LayoutTypeSources});"));
            migrationBuilder.Sql("ALTER TABLE [Documents] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[DocumentsHistory]));");

            migrationBuilder.DropForeignKey(
                name: "FK_Documents_SupplierLayoutVersions_SupplierLayoutVersionId",
                table: "Documents");

            migrationBuilder.DropTable(
                name: "SupplierLayoutVersions");

            migrationBuilder.DropTable(
                name: "SupplierLayouts");

            migrationBuilder.DropIndex(
                name: "IX_Documents_SupplierLayoutVersionId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "SupplierLayoutVersionId",
                table: "Documents");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV8));
        }
    }
}
