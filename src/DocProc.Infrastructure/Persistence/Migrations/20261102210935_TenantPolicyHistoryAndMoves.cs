using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Closes two gaps of the tenant policy (docs/01 "Multi-tenancy", docs/11 §6.10). SQL Server does not apply a
    /// table's security predicates to its history table, so a tenant session read every tenant's history through
    /// <c>FOR SYSTEM_TIME</c> or the history table itself: <c>DocumentsHistory</c> now gets the strict filter and
    /// <c>ExtractorProfilesHistory</c> the platform-row read filter (filters only: the system writes history). And the
    /// strict tables had no after-update block, so a tenant session could move one of its rows to another tenant: each
    /// now gets one. The policy is re-created from the frozen <see cref="RowLevelSecurity.PolicyV3"/>; no schema or data
    /// changes, so it runs on a live database.
    /// </summary>
    public partial class TenantPolicyHistoryAndMoves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV3));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV2));
        }
    }
}
