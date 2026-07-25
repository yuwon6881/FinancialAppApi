using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class DropTradeFxRateAndManualPriceFxRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FxRate",
                table: "ManualPriceOverrides");

            migrationBuilder.DropColumn(
                name: "TradeFxRate",
                table: "InvestmentTransactions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FxRate",
                table: "ManualPriceOverrides",
                type: "numeric(28,10)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TradeFxRate",
                table: "InvestmentTransactions",
                type: "numeric(28,10)",
                nullable: true);
        }
    }
}
