using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using FinancialAppApi.Database;

#nullable disable

namespace FinancialAppApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260714000000_NormalizeWeeklyRecurringPayments")]
public partial class NormalizeWeeklyRecurringPayments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "RecurringPayments"
            SET "Frequency" = 'Monthly'
            WHERE LOWER("Frequency") = 'weekly';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The original weekly cadence cannot be reconstructed after normalization.
    }
}
