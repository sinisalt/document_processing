using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P5 fix 2 (docs/11 §13.8): a reference keeps the signatures of every signed callback applied in the second of its
    /// last one (<c>LastCallbackSignatures</c>, space-separated), not only the last signature, so a captured callback of
    /// that second cannot be replayed. The column the fix-1 migration added is renamed and widened; the signature it
    /// holds is the first of the list. Down keeps the last signature of each list.
    /// </summary>
    public partial class CallbackSignaturesPerSecond : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LastCallbackSignature",
                table: "DocumentExternalRefs",
                newName: "LastCallbackSignatures");

            migrationBuilder.AlterColumn<string>(
                name: "LastCallbackSignatures",
                table: "DocumentExternalRefs",
                type: "nvarchar(1040)",
                maxLength: 1040,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The last signature applied is the last of the list (the earlier build compares that one).
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope("""
                UPDATE [DocumentExternalRefs]
                SET [LastCallbackSignatures] = LEFT(RIGHT([LastCallbackSignatures], CHARINDEX(N' ', REVERSE([LastCallbackSignatures])) - 1), 64)
                WHERE CHARINDEX(N' ', [LastCallbackSignatures]) > 0;
                UPDATE [DocumentExternalRefs]
                SET [LastCallbackSignatures] = LEFT([LastCallbackSignatures], 64)
                WHERE LEN([LastCallbackSignatures]) > 64;
                """));

            migrationBuilder.AlterColumn<string>(
                name: "LastCallbackSignatures",
                table: "DocumentExternalRefs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(1040)",
                oldMaxLength: 1040,
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "LastCallbackSignatures",
                table: "DocumentExternalRefs",
                newName: "LastCallbackSignature");
        }
    }
}
