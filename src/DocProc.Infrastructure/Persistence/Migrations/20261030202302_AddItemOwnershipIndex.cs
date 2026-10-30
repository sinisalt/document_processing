using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// One owner per tenant and SHA-256 (docs/03 "Implementation"): the <c>Accepted</c> disposition for items that
    /// produced documents, back-filled for the rows that already own one, and the filtered unique index that makes
    /// the rule hold across processes.
    /// </summary>
    public partial class AddItemOwnershipIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Items that already own a document were left Candidate by earlier builds; they are the owners the index protects.
            migrationBuilder.Sql(
                """
                UPDATE [i] SET [i].[Disposition] = N'Accepted'
                FROM [IngestionItems] AS [i]
                WHERE [i].[Disposition] = N'Candidate'
                  AND EXISTS (SELECT 1 FROM [Documents] AS [d] WHERE [d].[IngestionItemId] = [i].[Id]);
                """);

            migrationBuilder.CreateIndex(
                name: "UX_IngestionItems_TenantId_Sha256_Accepted",
                table: "IngestionItems",
                columns: new[] { "TenantId", "Sha256" },
                unique: true,
                filter: "[Disposition] = N'Accepted'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_IngestionItems_TenantId_Sha256_Accepted",
                table: "IngestionItems");

            migrationBuilder.Sql("UPDATE [IngestionItems] SET [Disposition] = N'Candidate' WHERE [Disposition] = N'Accepted';");
        }
    }
}
