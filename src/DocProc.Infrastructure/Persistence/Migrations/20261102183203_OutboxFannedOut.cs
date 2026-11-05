using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>OutboxMessages.FannedOutAt</c> (docs/11 §6.2, §13.5): when the publish stage handed the row on. Approve,
    /// reject and auto-approval now write the row in their own transaction and the publish stage sets this column, so a
    /// null marks a decision whose publish message was lost, which the publish sweeper picks up. Every existing row was
    /// written by the publish stage itself, so it was published when it was written (docs/11 §21 step 7): it gets
    /// <c>FannedOutAt = CreatedAt</c>, and the sweeper never publishes it again. Additive, so it runs on a live database.
    /// </summary>
    public partial class OutboxFannedOut : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FannedOutAt",
                table: "OutboxMessages",
                type: "datetimeoffset",
                nullable: true);

            // The outbox sits behind the tenant policy, which neither migration path opens, hence the platform scope.
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(
                """
                UPDATE [OutboxMessages] SET [FannedOutAt] = [CreatedAt] WHERE [FannedOutAt] IS NULL;
                """));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FannedOutAt",
                table: "OutboxMessages");
        }
    }
}
