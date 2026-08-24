using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLongHorizonPerformanceCaches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "GrowthContributions",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);

            // The new per-cycle value cannot be reconstructed from the four cumulative totals.
            // CycleBalances contains derived cache rows only, so rebuild them from the ledger on
            // the next read rather than treating zero as real historical contribution data.
            migrationBuilder.Sql("DELETE FROM \"CycleBalances\";");

            migrationBuilder.CreateIndex(
                name: "IX_PushReminderDeliveries_SentAt",
                table: "PushReminderDeliveries",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPriceBars_MarketDate",
                table: "MarketPriceBars",
                column: "MarketDate");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryLimitAlertEvents_CreatedAt",
                table: "CategoryLimitAlertEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryLimitAlertEvaluations_CreatedAt",
                table: "CategoryLimitAlertEvaluations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryLimitAlertDeliveries_SentAt",
                table: "CategoryLimitAlertDeliveries",
                column: "SentAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushReminderDeliveries_SentAt",
                table: "PushReminderDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_MarketPriceBars_MarketDate",
                table: "MarketPriceBars");

            migrationBuilder.DropIndex(
                name: "IX_CategoryLimitAlertEvents_CreatedAt",
                table: "CategoryLimitAlertEvents");

            migrationBuilder.DropIndex(
                name: "IX_CategoryLimitAlertEvaluations_CreatedAt",
                table: "CategoryLimitAlertEvaluations");

            migrationBuilder.DropIndex(
                name: "IX_CategoryLimitAlertDeliveries_SentAt",
                table: "CategoryLimitAlertDeliveries");

            migrationBuilder.DropColumn(
                name: "GrowthContributions",
                table: "CycleBalances");
        }
    }
}
