using System;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The type catalogue of docs/11 §6.1 (P1): <c>DocumentTypeDefinitions</c> and the temporal
    /// <c>DocumentTypeVersions</c>, both with platform rows (TenantId NULL) and therefore under the platform-row
    /// predicates of row-level security, with <c>LlmProviderConfigs</c> joining them (§6.10; the frozen
    /// <see cref="RowLevelSecurity.PolicyV4"/>). <c>Documents.DocumentTypeVersionId</c> and
    /// <c>Corrections.DocumentTypeVersionId</c> are added and back-filled (§21 step 3) after the seed (§21 step 2):
    /// the platform templates and, for every tenant, the adopted types under the legacy keys at version 1, published
    /// (<see cref="TypeCatalogueSeedSql"/>). Additive: new tables and nullable columns only, so it runs on a live database.
    /// </summary>
    public partial class TypeCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DocumentTypeVersionId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DocumentTypeVersionId",
                table: "Corrections",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentTypeDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "json", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Origin = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BaseVersion = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PublishedVersion = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTypeDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentTypeDefinitions_DocumentTypeDefinitions_BaseDefinitionId",
                        column: x => x.BaseDefinitionId,
                        principalTable: "DocumentTypeDefinitions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DocumentTypeVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    DefinitionJson = table.Column<string>(type: "json", nullable: false),
                    CompiledJsonSchema = table.Column<string>(type: "json", nullable: true),
                    CompiledLlmSchema = table.Column<string>(type: "json", nullable: true),
                    LlmSchemaName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LlmSchemaVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LlmSchemaHash = table.Column<string>(type: "nchar(16)", fixedLength: true, maxLength: 16, nullable: true),
                    FormSpecJson = table.Column<string>(type: "json", nullable: true),
                    DescriptorsJson = table.Column<string>(type: "json", nullable: true),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    State = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublishedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ChangeNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PeriodEnd = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    PeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTypeVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentTypeVersions_DocumentTypeDefinitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "DocumentTypeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "DocumentTypeVersionsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_DocumentTypeVersionId",
                table: "Documents",
                column: "DocumentTypeVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTypeDefinitions_BaseDefinitionId",
                table: "DocumentTypeDefinitions",
                column: "BaseDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTypeDefinitions_TenantId",
                table: "DocumentTypeDefinitions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTypeDefinitions_TenantId_Key",
                table: "DocumentTypeDefinitions",
                columns: new[] { "TenantId", "Key" },
                unique: true,
                filter: "[TenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTypeVersions_DefinitionId_Version",
                table: "DocumentTypeVersions",
                columns: new[] { "DefinitionId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTypeVersions_TenantId",
                table: "DocumentTypeVersions",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_DocumentTypeVersions_DocumentTypeVersionId",
                table: "Documents",
                column: "DocumentTypeVersionId",
                principalTable: "DocumentTypeVersions",
                principalColumn: "Id");

            // The seed and the back-fill run with the policy down and in platform scope (every data statement through
            // EXEC, so the idempotent script compiles); then the policy is re-created with the catalogue tables.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            foreach (var statement in TypeCatalogueSeedSql.Statements())
            {
                migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(statement));
            }

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV4));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names Documents, whose drops turn system versioning off: SQL Server refuses that
            // while the policy exists, so the earlier policy is created again after the drops (see RowLevelSecurity).
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            migrationBuilder.DropForeignKey(
                name: "FK_Documents_DocumentTypeVersions_DocumentTypeVersionId",
                table: "Documents");

            migrationBuilder.DropTable(
                name: "DocumentTypeVersions")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "DocumentTypeVersionsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.DropTable(
                name: "DocumentTypeDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_Documents_DocumentTypeVersionId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "DocumentTypeVersionId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "DocumentTypeVersionId",
                table: "Corrections");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV3));
        }
    }
}
