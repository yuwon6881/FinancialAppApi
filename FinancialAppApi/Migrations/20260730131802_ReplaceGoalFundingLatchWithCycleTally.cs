using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceGoalFundingLatchWithCycleTally : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LastFundedCycleKey",
                table: "SavingsGoals",
                newName: "CycleFundedKey");

            migrationBuilder.AddColumn<decimal>(
                name: "CycleFundedAmount",
                table: "SavingsGoals",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CycleFundedAmount",
                table: "SavingsGoals");

            migrationBuilder.RenameColumn(
                name: "CycleFundedKey",
                table: "SavingsGoals",
                newName: "LastFundedCycleKey");
        }
    }
}
