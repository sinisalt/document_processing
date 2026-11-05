using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AcknowledgementReplayGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCallbackAt",
                table: "DocumentExternalRefs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCallbackSignature",
                table: "DocumentExternalRefs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCallbackAt",
                table: "DocumentExternalRefs");

            migrationBuilder.DropColumn(
                name: "LastCallbackSignature",
                table: "DocumentExternalRefs");
        }
    }
}
