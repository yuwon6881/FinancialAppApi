using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStabilityReloadIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StabilityReloadIntent",
                table: "Transactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unanswered");

            migrationBuilder.AddColumn<decimal>(
                name: "StabilityReloadMarkedAmount",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StabilityReloadOldestDate",
                table: "CycleBalances",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StabilityReloadOutstanding",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.DropColumn(
                name: "StabilityPeakBalance",
                table: "CycleBalances");

            migrationBuilder.DropColumn(
                name: "StabilityWithdrawnAmount",
                table: "CycleBalances");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transactions_stabilityreloadintent",
                table: "Transactions",
                sql: "\"StabilityReloadIntent\" IN ('Unanswered', 'Required', 'NotRequired')");

            // Existing withdrawals predate the opt-in answer. They must remain visible as
            // obligations; only a new explicit "Spent for good" answer opts a drawdown out.
            migrationBuilder.Sql("""
                UPDATE "Transactions"
                SET "StabilityReloadIntent" = 'Required'
                WHERE (LOWER("LedgerCategory") = 'stability' AND "Amount" < 0)
                   OR LOWER("LedgerCategory") LIKE 'transfer:stability->%';
                """);

            // The old cache has no replay state. Rebuilding it from the transaction ledger is
            // safer than carrying a high-water result into the new obligation model.
            migrationBuilder.Sql("DELETE FROM \"CycleBalances\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_transactions_stabilityreloadintent",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "StabilityReloadIntent",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "StabilityReloadMarkedAmount",
                table: "CycleBalances");

            migrationBuilder.DropColumn(
                name: "StabilityReloadOldestDate",
                table: "CycleBalances");

            migrationBuilder.DropColumn(
                name: "StabilityReloadOutstanding",
                table: "CycleBalances");

            migrationBuilder.AddColumn<decimal>(
                name: "StabilityPeakBalance",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StabilityWithdrawnAmount",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);
        }
    }
}
