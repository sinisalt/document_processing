using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConcurrencyTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ReviewTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Ingestions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Documents",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "ReviewTasks");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Ingestions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Documents");
        }
    }
}
