using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class CorrectStabilityReimbursement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "StabilityRecoveryTopUpAmount",
                table: "Transactions",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StabilityPeakBalance",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);

            // These rows are a rebuildable cache. Old rows have no intra-cycle peak/withdrawal
            // history, so retaining them would permanently understate recovery history.
            migrationBuilder.Sql("DELETE FROM \"CycleBalances\";");

            migrationBuilder.AddColumn<decimal>(
                name: "StabilityWithdrawnAmount",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StabilityRecoveryTopUpAmount",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "StabilityPeakBalance",
                table: "CycleBalances");

            migrationBuilder.DropColumn(
                name: "StabilityWithdrawnAmount",
                table: "CycleBalances");
        }
    }
}
