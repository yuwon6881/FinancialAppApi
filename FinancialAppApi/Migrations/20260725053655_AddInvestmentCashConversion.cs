using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInvestmentCashConversion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FxRate",
                table: "InvestmentCashFlows",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ToAmount",
                table: "InvestmentCashFlows",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToCurrency",
                table: "InvestmentCashFlows",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FxRate",
                table: "InvestmentCashFlows");

            migrationBuilder.DropColumn(
                name: "ToAmount",
                table: "InvestmentCashFlows");

            migrationBuilder.DropColumn(
                name: "ToCurrency",
                table: "InvestmentCashFlows");
        }
    }
}
