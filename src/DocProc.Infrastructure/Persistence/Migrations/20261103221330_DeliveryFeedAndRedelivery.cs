using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P3 part B (docs/11 §13.3, §13.6): <c>OutboxDeliveries.Event</c> (the event a payload was rendered for, with
    /// <c>document.updated</c> for <c>OnReapproval: Update</c>), <c>RedeliveryOfId</c> (what a redelivery sends again),
    /// <c>FeedSequence</c> with its index (a pull feed's cursor), <c>OutputDestinations.FeedSequence</c> (the feed's
    /// counter) and <c>ApiKeys.DestinationId</c> (a <c>deliveries:read</c> key restricted to one feed). No table is
    /// added, so the policy stays <c>PolicyV7</c>: plain column adds and drops pass the schema-bound policy.
    /// <para>
    /// Down maps what the build before it cannot read: the <c>deliveries:read</c> scope is a new member of
    /// <c>ApiKeyScope</c>, stored by its number in the <c>Scopes</c> json array, which that build would read as a scope
    /// without a name. It is taken out of every key, and a key left without a scope is revoked (it was a feed client's
    /// key, and that build has no feed). The other values part B writes are strings that build reads as they are (the
    /// <c>DELIVERY_FAILED</c> flag, the <c>delivery_*</c> timeline events) or members it has (the delivery statuses).
    /// </para>
    /// </summary>
    public partial class DeliveryFeedAndRedelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxDeliveries_DestinationId",
                table: "OutboxDeliveries");

            migrationBuilder.AddColumn<long>(
                name: "FeedSequence",
                table: "OutputDestinations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Event",
                table: "OutboxDeliveries",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "document.approved");

            migrationBuilder.AddColumn<long>(
                name: "FeedSequence",
                table: "OutboxDeliveries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RedeliveryOfId",
                table: "OutboxDeliveries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DestinationId",
                table: "ApiKeys",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_DestinationId_FeedSequence",
                table: "OutboxDeliveries",
                columns: new[] { "DestinationId", "FeedSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_RedeliveryOfId",
                table: "OutboxDeliveries",
                column: "RedeliveryOfId");

            migrationBuilder.AddForeignKey(
                name: "FK_OutboxDeliveries_OutboxDeliveries_RedeliveryOfId",
                table: "OutboxDeliveries",
                column: "RedeliveryOfId",
                principalTable: "OutboxDeliveries",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(StripDeliveriesReadScope));

            migrationBuilder.DropForeignKey(
                name: "FK_OutboxDeliveries_OutboxDeliveries_RedeliveryOfId",
                table: "OutboxDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_OutboxDeliveries_DestinationId_FeedSequence",
                table: "OutboxDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_OutboxDeliveries_RedeliveryOfId",
                table: "OutboxDeliveries");

            migrationBuilder.DropColumn(
                name: "FeedSequence",
                table: "OutputDestinations");

            migrationBuilder.DropColumn(
                name: "Event",
                table: "OutboxDeliveries");

            migrationBuilder.DropColumn(
                name: "FeedSequence",
                table: "OutboxDeliveries");

            migrationBuilder.DropColumn(
                name: "RedeliveryOfId",
                table: "OutboxDeliveries");

            migrationBuilder.DropColumn(
                name: "DestinationId",
                table: "ApiKeys");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_DestinationId",
                table: "OutboxDeliveries",
                column: "DestinationId");
        }

        /// <summary>
        /// The <c>deliveries:read</c> scope out of every key's json array (by its number, <c>2</c>, or its name, should a
        /// later mapping store names), the other elements kept as they are; a key left with none is revoked.
        /// </summary>
        internal const string StripDeliveriesReadScope = """
            UPDATE [ApiKeys]
            SET [Scopes] = CAST(COALESCE(
                    (SELECT N'[' + STRING_AGG(CASE WHEN j.[type] = 1 THEN N'"' + j.[value] + N'"' ELSE j.[value] END, N',') + N']'
                     FROM OPENJSON(CAST([Scopes] AS nvarchar(max))) j
                     WHERE j.[value] NOT IN (N'2', N'DeliveriesRead')),
                    N'[]') AS json),
                [RevokedAt] = CASE
                    WHEN [RevokedAt] IS NULL AND NOT EXISTS (SELECT 1 FROM OPENJSON(CAST([Scopes] AS nvarchar(max))) j WHERE j.[value] NOT IN (N'2', N'DeliveriesRead'))
                    THEN SYSDATETIMEOFFSET()
                    ELSE [RevokedAt] END
            WHERE EXISTS (SELECT 1 FROM OPENJSON(CAST([Scopes] AS nvarchar(max))) j WHERE j.[value] IN (N'2', N'DeliveriesRead'));
            """;
    }
}
