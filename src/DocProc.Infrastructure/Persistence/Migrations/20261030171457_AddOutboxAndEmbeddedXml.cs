using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxAndEmbeddedXml : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmbeddedXmlBlob_Container",
                table: "Documents",
                type: "nvarchar(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddedXmlBlob_ContentType",
                table: "Documents",
                type: "nvarchar(127)",
                maxLength: 127,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddedXmlBlob_Key",
                table: "Documents",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddedXmlBlob_Sha256",
                table: "Documents",
                type: "nchar(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EmbeddedXmlBlob_SizeBytes",
                table: "Documents",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "json", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_DeliveredAt_CreatedAt",
                table: "OutboxMessages",
                columns: new[] { "DeliveredAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_DocumentId_EventType",
                table: "OutboxMessages",
                columns: new[] { "DocumentId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantId",
                table: "OutboxMessages",
                column: "TenantId");

            // The security policy is re-created over the full table list so the outbox is tenant-filtered too.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.ProtectedTables));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "EmbeddedXmlBlob_Container",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "EmbeddedXmlBlob_ContentType",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "EmbeddedXmlBlob_Key",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "EmbeddedXmlBlob_Sha256",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "EmbeddedXmlBlob_SizeBytes",
                table: "Documents");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql());
        }
    }
}
