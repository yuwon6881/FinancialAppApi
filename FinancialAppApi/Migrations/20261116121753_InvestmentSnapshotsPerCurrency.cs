using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class InvestmentSnapshotsPerCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The snapshots are a derived cache. Their figures were converted into the app currency at
            // historical rates and now hold each currency natively (constant-currency reporting),
            // so they are cleared and rebuilt from activity on the next read.
            migrationBuilder.Sql("DELETE FROM \"InvestmentPeriodSnapshots\";");

            migrationBuilder.DropIndex(
                name: "IX_InvestmentPeriodSnapshots_UserId_PeriodEnd_Segment",
                table: "InvestmentPeriodSnapshots");

            migrationBuilder.RenameColumn(
                name: "AppCurrency",
                table: "InvestmentPeriodSnapshots",
                newName: "Currency");

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentPeriodSnapshots_UserId_PeriodEnd_Segment_Currency",
                table: "InvestmentPeriodSnapshots",
                columns: new[] { "UserId", "PeriodEnd", "Segment", "Currency" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Per-currency rows cannot satisfy the older one-row-per-segment index,
            // so they are cleared and rebuilt from activity on the next read.
            migrationBuilder.Sql("DELETE FROM \"InvestmentPeriodSnapshots\";");

            migrationBuilder.DropIndex(
                name: "IX_InvestmentPeriodSnapshots_UserId_PeriodEnd_Segment_Currency",
                table: "InvestmentPeriodSnapshots");

            migrationBuilder.RenameColumn(
                name: "Currency",
                table: "InvestmentPeriodSnapshots",
                newName: "AppCurrency");

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentPeriodSnapshots_UserId_PeriodEnd_Segment",
                table: "InvestmentPeriodSnapshots",
                columns: new[] { "UserId", "PeriodEnd", "Segment" },
                unique: true);
        }
    }
}
