using DocProc.Infrastructure.Persistence.Tenancy;
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
            // IngestionItems and Documents sit behind the row-level security policy, and neither migration path (the
            // release script run with sqlcmd, MigrationHostedService with an anonymous tenant context) sets a tenant, so
            // the statement switches the session to platform scope for itself and clears it again afterwards.
            migrationBuilder.Sql(InPlatformScope(
                """
                UPDATE [i] SET [i].[Disposition] = N'Accepted'
                FROM [IngestionItems] AS [i]
                WHERE [i].[Disposition] = N'Candidate'
                  AND EXISTS (SELECT 1 FROM [Documents] AS [d] WHERE [d].[IngestionItemId] = [i].[Id]);
                """));

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

            migrationBuilder.Sql(InPlatformScope("UPDATE [IngestionItems] SET [Disposition] = N'Candidate' WHERE [Disposition] = N'Accepted';"));
        }

        /// <summary>Runs a data statement with the row-level security predicate open (platform scope), then closes it.</summary>
        internal static string InPlatformScope(string statement) =>
            $"""
            EXEC sp_set_session_context @key = N'{TenantSessionContextInterceptor.ScopeKey}', @value = N'{TenantSessionContextInterceptor.PlatformScopeValue}', @read_only = 0;
            {statement}
            EXEC sp_set_session_context @key = N'{TenantSessionContextInterceptor.ScopeKey}', @value = NULL, @read_only = 0;
            """;
    }
}
