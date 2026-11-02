using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Corrections.OldValueJson and NewValueJson become nvarchar(max): a field value is usually a bare JSON string,
    /// number or null, which SQL Server's json type rejects (error 13609), so every such correction failed. SQL Server
    /// does not convert json to nvarchar in place ("Implicit conversion from data type json to nvarchar(max) is not
    /// allowed"), so each column is copied into a new one with CONVERT, dropped and the new one renamed.
    /// </summary>
    public partial class CorrectionValuesAsText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Swap(migrationBuilder, "nvarchar(max)");
        }

        /// <summary>
        /// Back to json. Fails while a row holds a value json does not accept, which every bare string, number or null
        /// is; delete those corrections first.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Swap(migrationBuilder, "json");
        }

        private static void Swap(MigrationBuilder migrationBuilder, string type)
        {
            foreach (var column in new[] { "OldValueJson", "NewValueJson" })
            {
                var temporary = column + "_New";
                migrationBuilder.Sql($"ALTER TABLE [Corrections] ADD [{temporary}] {type} NULL;");
                // A batch of its own: the new column does not exist when the batch that adds it is compiled.
                migrationBuilder.Sql($"UPDATE [Corrections] SET [{temporary}] = CONVERT({type}, [{column}]);");
                migrationBuilder.Sql($"ALTER TABLE [Corrections] DROP COLUMN [{column}];");
                migrationBuilder.Sql($"EXEC sp_rename N'[Corrections].[{temporary}]', N'{column}', N'COLUMN';");
            }
        }
    }
}
