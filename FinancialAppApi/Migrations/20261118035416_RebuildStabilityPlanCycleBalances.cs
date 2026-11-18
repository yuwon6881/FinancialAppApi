using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class RebuildStabilityPlanCycleBalances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Replay derived history for users whose Stability plan changed after its baseline.
            migrationBuilder.Sql("""
                DELETE FROM "CycleBalances" AS balance
                WHERE EXISTS (
                    SELECT 1 FROM "StabilityPlanRevisions" AS plan
                    WHERE plan."UserId" = balance."UserId"
                      AND plan."EffectiveAt" > TIMESTAMPTZ '1970-01-01 00:00:00+00'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Derived balances are rebuilt on demand.
        }
    }
}
