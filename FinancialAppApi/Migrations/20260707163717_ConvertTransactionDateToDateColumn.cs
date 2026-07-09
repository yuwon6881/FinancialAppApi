using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class ConvertTransactionDateToDateColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Postgres has no implicit/assignment cast from text to date, so the plain
            // ALTER COLUMN ... TYPE that EF would otherwise generate fails against a
            // populated table. An explicit USING cast is required; it in turn requires
            // every existing row's Date to already be a valid, unambiguous date literal
            // (e.g. "2026-07-08") -- fix any row that isn't before running this migration.
            migrationBuilder.Sql(
                "ALTER TABLE \"Transactions\" ALTER COLUMN \"Date\" TYPE date USING \"Date\"::date;");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Date",
                table: "Transactions",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_Date",
                table: "Transactions");

            migrationBuilder.Sql(
                "ALTER TABLE \"Transactions\" ALTER COLUMN \"Date\" TYPE text USING \"Date\"::text;");
        }
    }
}
