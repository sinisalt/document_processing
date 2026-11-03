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

            migrationBuilder.DropColumn(
                name: "JoinedIntoDocumentId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "SupplierMatchMethod",
                table: "Documents");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV5));
        }
    }
}
