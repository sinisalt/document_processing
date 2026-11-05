using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P2 review actions of docs/11 §12.1 and §12.7: <c>Documents.SupplierMatchMethod</c> (docs/11 §6.2; <c>Human</c>
    /// for a supplier a reviewer assigned, which the Match stage then keeps; the method the Match stage found otherwise)
    /// and <c>Documents.JoinedIntoDocumentId</c>, the document a part was joined into (the part is then
    /// <c>Cancelled</c>). Both nullable; existing documents are not back-filled. No new table, so the row-level security
    /// policy stays <see cref="RowLevelSecurity.PolicyV5"/>.
    /// <para>
    /// Down maps the status P2 added back to one the build before it reads: a part joined into another document is
    /// <c>Cancelled</c>, which that build's <c>DocumentStatus</c> does not have, and enums are stored as strings, so
    /// reading such a row (a document, a version of it in <c>DocumentsHistory</c>, a timeline event) would fail. Those
    /// rows become <c>Rejected</c>, a settled status that is neither exported nor matched; the document a part was
    /// joined into stays in the details of its <c>joined</c> event (<c>into</c>) once <c>JoinedIntoDocumentId</c> is gone.
    /// </para>
    /// </summary>
    public partial class ReviewActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JoinedIntoDocumentId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierMatchMethod",
                table: "Documents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names Documents, whose drops turn system versioning off: SQL Server refuses that
            // while the policy exists, so the policy is dropped first and created again after the drops.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            // Cancelled rows back to a status the earlier build reads, in the table, its history and the timeline. The
            // update of Documents adds the Cancelled versions to the history, so the history goes last; it is written
            // by the system only, so system versioning is off for that one statement.
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(
                $"UPDATE [Documents] SET [Status] = N'{UncancelledStatus}' WHERE [Status] = N'{CancelledStatus}';"));
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(
                $"UPDATE [DocumentEvents] SET [ToStatus] = N'{UncancelledStatus}' WHERE [ToStatus] = N'{CancelledStatus}'; " +
                $"UPDATE [DocumentEvents] SET [FromStatus] = N'{UncancelledStatus}' WHERE [FromStatus] = N'{CancelledStatus}';"));
            migrationBuilder.Sql("ALTER TABLE [Documents] SET (SYSTEM_VERSIONING = OFF);");
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(
                $"UPDATE [DocumentsHistory] SET [Status] = N'{UncancelledStatus}' WHERE [Status] = N'{CancelledStatus}';"));
            migrationBuilder.Sql("ALTER TABLE [Documents] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[DocumentsHistory]));");

            migrationBuilder.DropColumn(
                name: "JoinedIntoDocumentId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "SupplierMatchMethod",
                table: "Documents");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV5));
        }

        /// <summary>The status P2 added (<c>DocumentStatus.Cancelled</c>), as stored.</summary>
        private const string CancelledStatus = "Cancelled";

        /// <summary>What a Down makes of it: a settled status of the earlier build.</summary>
        private const string UncancelledStatus = "Rejected";
    }
}
