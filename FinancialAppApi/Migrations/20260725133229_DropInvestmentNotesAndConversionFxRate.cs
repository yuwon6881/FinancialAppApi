using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class DropInvestmentNotesAndConversionFxRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Notes",
                table: "InvestmentTransactions");

            migrationBuilder.DropColumn(
                name: "FxRate",
                table: "InvestmentCashFlows");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "InvestmentCashFlows");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "InvestmentTransactions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FxRate",
                table: "InvestmentCashFlows",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "InvestmentCashFlows",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }
    }
}
