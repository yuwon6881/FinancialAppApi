using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <summary>
    /// Carries the reload queue's still-owing entries across cycle boundaries.
    /// <para>
    /// The cache previously stored only the aggregate outstanding amount and the oldest outstanding
    /// date, so the next cycle replayed against a single anonymous queue entry and could not tell
    /// which carried drawdown a repayment had discharged. Every reported total was therefore summed
    /// over movements instead of obligations, and a drawdown already put back in full kept inflating
    /// the amount the dashboard asked back for as long as anything else was still owed -- a total
    /// that grew with every new drawdown and never came down.
    /// </para>
    /// <para>
    /// StabilityReloadMarkedAmount goes with it: its only reader was the recovery pace anchor, which
    /// now anchors on the oldest obligation that still owes money.
    /// </para>
    /// </summary>
    public partial class CarryStabilityReloadObligations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StabilityReloadMarkedAmount",
                table: "CycleBalances");

            migrationBuilder.AddColumn<string>(
                name: "StabilityReloadObligations",
                table: "CycleBalances",
                type: "jsonb",
                nullable: true);

            // CycleBalances is a derived cache with no authoritative data of its own, and existing
            // rows have no obligation identities to backfill. Drop them and let the next read
            // recompute from the transaction history, as every prior reload-shape change has done.
            migrationBuilder.Sql("DELETE FROM \"CycleBalances\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StabilityReloadObligations",
                table: "CycleBalances");

            migrationBuilder.AddColumn<decimal>(
                name: "StabilityReloadMarkedAmount",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql("DELETE FROM \"CycleBalances\";");
        }
    }
}
