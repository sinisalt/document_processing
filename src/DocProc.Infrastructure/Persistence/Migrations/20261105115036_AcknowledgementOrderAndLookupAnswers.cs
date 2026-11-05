using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P5 fix 3. <c>DocumentExternalRefs.AcknowledgementVersion</c> orders the acknowledgements of one reference (docs/11
    /// §13.8): the writer of a callback or a status answer moves it in the transaction of its writes, so two that ran at
    /// the same time cannot both pass the order guard. <c>LookupResults.ResponseJson</c> keeps an http-lookup's answer
    /// (docs/11 §10.2), which the step's current response mapping maps on every run; rows stored before have none and
    /// send their request once more. Down drops both: the earlier build orders nothing and reuses the stored assignments.
    /// </summary>
    public partial class AcknowledgementOrderAndLookupAnswers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResponseJson",
                table: "LookupResults",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AcknowledgementVersion",
                table: "DocumentExternalRefs",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResponseJson",
                table: "LookupResults");

            migrationBuilder.DropColumn(
                name: "AcknowledgementVersion",
                table: "DocumentExternalRefs");
        }
    }
}
