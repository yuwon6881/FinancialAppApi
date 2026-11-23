using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolioGroupsAndPushDeviceLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_investmentinstruments_allocationsleeve",
                table: "InvestmentInstruments");

            migrationBuilder.AddColumn<string>(
                name: "DeviceLabel",
                table: "PushSubscriptions",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupsJson",
                table: "InvestmentPlans",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_investmentinstruments_allocationsleeve",
                table: "InvestmentInstruments",
                sql: "\"AllocationSleeve\" IS NULL OR \"AllocationSleeve\" IN ('USEquity', 'InternationalExUS', 'Bonds') OR (length(\"AllocationSleeve\") = 32 AND ltrim(\"AllocationSleeve\", '0123456789abcdef') = '')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_investmentinstruments_allocationsleeve",
                table: "InvestmentInstruments");

            migrationBuilder.DropColumn(
                name: "DeviceLabel",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "GroupsJson",
                table: "InvestmentPlans");

            migrationBuilder.AddCheckConstraint(
                name: "ck_investmentinstruments_allocationsleeve",
                table: "InvestmentInstruments",
                sql: "\"AllocationSleeve\" IS NULL OR \"AllocationSleeve\" IN ('USEquity', 'InternationalExUS', 'Bonds')");
        }
    }
}
