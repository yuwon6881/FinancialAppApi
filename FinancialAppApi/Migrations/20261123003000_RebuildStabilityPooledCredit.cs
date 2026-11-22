using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FinancialAppApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261123003000_RebuildStabilityPooledCredit")]
public sealed class RebuildStabilityPooledCredit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Previous snapshots discarded repaid schedules. Rebuild the derived state once so
        // existing withdrawals carry the same pooled credit as newly recorded withdrawals.
        migrationBuilder.Sql("""DELETE FROM "CycleBalances";""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Older code expects snapshots to contain only outstanding obligations.
        migrationBuilder.Sql("""DELETE FROM "CycleBalances";""");
    }
}
