using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The structured parameters of a flag (docs/02 §5 "Flag parameters"), so the apps can phrase it in the UI
    /// language. Nullable: flags written before keep only their English message. Adding a column to a table behind
    /// the row-level security policy needs no policy change (the predicate reads <c>TenantId</c> only).
    /// </summary>
    public partial class AddFlagParams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Params",
                table: "DocumentFlags",
                type: "json",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Params",
                table: "DocumentFlags");
        }
    }
}
