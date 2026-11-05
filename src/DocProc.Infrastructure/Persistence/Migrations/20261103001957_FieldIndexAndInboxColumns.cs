using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P1 storage of docs/11 §6.2: the inbox columns <c>Documents.ValidFrom</c>, <c>ValidUntil</c> and <c>LineCount</c>
    /// (from the roles <c>validFrom</c>, <c>validUntil</c> and the rows of <c>lineItems</c>), <c>AppliedConfigJson</c>,
    /// <c>ReviewTasks.Priority</c> (Normal for every existing task) and the table <c>DocumentFieldIndex</c> of the
    /// <c>indexed</c> fields, a strict tenant table under the re-created policy <see cref="RowLevelSecurity.PolicyV5"/>.
    /// Existing documents are not back-filled: their columns and index rows are written the next time the Extract stage
    /// or a correction rewrites their tree.
    /// </summary>
    public partial class FieldIndexAndInboxColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "ReviewTasks",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddColumn<string>(
                name: "AppliedConfigJson",
                table: "Documents",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LineCount",
                table: "Documents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ValidFrom",
                table: "Documents",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ValidUntil",
                table: "Documents",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentFieldIndex",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StringValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    NumberValue = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    DateValue = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentFieldIndex", x => new { x.DocumentId, x.FieldKey, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_DocumentFieldIndex_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentFieldIndex_TenantId",
                table: "DocumentFieldIndex",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentFieldIndex_TenantId_FieldKey_DateValue",
                table: "DocumentFieldIndex",
                columns: new[] { "TenantId", "FieldKey", "DateValue" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentFieldIndex_TenantId_FieldKey_StringValue",
                table: "DocumentFieldIndex",
                columns: new[] { "TenantId", "FieldKey", "StringValue" });

            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV5));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names Documents, whose drops turn system versioning off: SQL Server refuses that
            // while the policy exists, so the earlier policy is created again after the drops (see RowLevelSecurity).
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            migrationBuilder.DropTable(
                name: "DocumentFieldIndex");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "ReviewTasks");

            migrationBuilder.DropColumn(
                name: "AppliedConfigJson",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "LineCount",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "ValidFrom",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "ValidUntil",
                table: "Documents");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV4));
        }
    }
}
