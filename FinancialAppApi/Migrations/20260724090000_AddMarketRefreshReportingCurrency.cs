using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using FinancialAppApi.Database;

#nullable disable

namespace FinancialAppApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260724090000_AddMarketRefreshReportingCurrency")]
public partial class AddMarketRefreshReportingCurrency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ReportingCurrency",
            table: "MarketDataRefreshJobs",
            type: "character varying(3)",
            maxLength: 3,
            nullable: false,
            defaultValue: "USD");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ReportingCurrency",
            table: "MarketDataRefreshJobs");
    }
}
