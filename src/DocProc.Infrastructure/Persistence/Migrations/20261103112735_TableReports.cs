using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P2 table tool storage of docs/11 §6.2: the table <c>DocumentTableReports</c>, the report of a repeating group the
    /// review table tool wrote (docs/11 §12.4), one per document and group path, linked to the Correction that wrote the
    /// rows. A strict tenant table under the re-created policy <see cref="RowLevelSecurity.PolicyV6"/>. Nothing to back-fill.
    /// </summary>
    public partial class TableReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentTableReports",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StepId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrectionId = table.Column<long>(type: "bigint", nullable: true),
                    ProducedAtVersion = table.Column<int>(type: "int", nullable: false),
                    ReportJson = table.Column<string>(type: "json", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTableReports", x => new { x.DocumentId, x.Path });
                    table.ForeignKey(
                        name: "FK_DocumentTableReports_Corrections_CorrectionId",
                        column: x => x.CorrectionId,
                        principalTable: "Corrections",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DocumentTableReports_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTableReports_CorrectionId",
                table: "DocumentTableReports",
                column: "CorrectionId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTableReports_TenantId",
                table: "DocumentTableReports",
                column: "TenantId");

            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV6));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names the table, so it is dropped first and the earlier policy created after.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            migrationBuilder.DropTable(
                name: "DocumentTableReports");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV5));
        }
    }
}
