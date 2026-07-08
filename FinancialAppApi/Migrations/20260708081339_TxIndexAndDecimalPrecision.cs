using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class TxIndexAndDecimalPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_Date",
                table: "Transactions");

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "Transactions",
                type: "numeric(12,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "RecurringPayments",
                type: "numeric(12,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Date_LedgerCategory",
                table: "Transactions",
                columns: new[] { "Date", "LedgerCategory" },
                descending: new[] { true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_Date_LedgerCategory",
                table: "Transactions");

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "Transactions",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "RecurringPayments",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Date",
                table: "Transactions",
                column: "Date");
        }
    }
}
