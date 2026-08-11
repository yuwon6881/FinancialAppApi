using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <summary>
    /// Drops every cached cycle balance so the corrected reload replay recomputes them.
    /// <para>
    /// <c>StabilityReloadLedger.Replay</c> used to track the obligation twice -- a running total
    /// beside the FIFO queue -- and seeded the queue only when the carried state also carried a
    /// date. A carried obligation with no date left the queue empty while the total stayed
    /// positive, so every later repayment debited one and not the other and the divergence was
    /// then persisted into <c>CycleBalances</c> and inherited by each following cycle. Fixing the
    /// replay cannot repair rows already written, and nothing else invalidates them: the cache is
    /// only cleared when a transaction or a Stability setting changes.
    /// </para>
    /// <para>
    /// Safe by construction -- <c>CycleBalances</c> is a derived cache, and
    /// <c>CycleBalanceService.EnsureComputedThroughAsync</c> rebuilds any missing cycle from the
    /// transaction ledger on the next dashboard read.
    /// </para>
    /// </summary>
    public partial class RebuildStabilityReloadCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"CycleBalances\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the rows are a cache, and they are rebuilt on demand.
        }
    }
}
