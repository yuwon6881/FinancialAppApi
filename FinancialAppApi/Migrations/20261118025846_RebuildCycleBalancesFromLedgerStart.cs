using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class RebuildCycleBalancesFromLedgerStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rebuild only users whose ledger can precede a snapshot made with the old start year.
            migrationBuilder.Sql("""
                DELETE FROM "CycleBalances" AS balance
                WHERE EXISTS (
                    SELECT 1 FROM "Transactions" AS ledger_entry
                    WHERE ledger_entry."UserId" = balance."UserId"
                      AND ledger_entry."Date" < TIMESTAMPTZ '2026-01-31 00:00:00+00'
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
