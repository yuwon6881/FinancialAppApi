using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyInvestmentActivitiesAddInvestmentScanType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InvestmentTransactions_InvestmentTransactions_LinkedTransfe~",
                table: "InvestmentTransactions");

            migrationBuilder.Sql(
                """DELETE FROM "InvestmentTransactions" WHERE "Type" IN ('OpeningPosition', 'Split', 'TransferIn', 'TransferOut');""");

            migrationBuilder.DropIndex(
                name: "IX_InvestmentTransactions_LinkedTransferId",
                table: "InvestmentTransactions");

            migrationBuilder.DropColumn(
                name: "LinkedTransferId",
                table: "InvestmentTransactions");

            migrationBuilder.AddColumn<string>(
                name: "ScanType",
                table: "ReceiptScanJobs",
                type: "text",
                nullable: false,
                defaultValue: "receipt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScanType",
                table: "ReceiptScanJobs");

            migrationBuilder.AddColumn<Guid>(
                name: "LinkedTransferId",
                table: "InvestmentTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentTransactions_LinkedTransferId",
                table: "InvestmentTransactions",
                column: "LinkedTransferId");

            migrationBuilder.AddForeignKey(
                name: "FK_InvestmentTransactions_InvestmentTransactions_LinkedTransfe~",
                table: "InvestmentTransactions",
                column: "LinkedTransferId",
                principalTable: "InvestmentTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
