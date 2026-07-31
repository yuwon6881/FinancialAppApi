using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class PreserveSavingsGoalDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecurrenceDayOfMonth",
                table: "SavingsGoals",
                type: "integer",
                nullable: true);

            // Existing rows have no separate anchor yet. Preserve their current recurring day so
            // the new logic is immediately stable; a previously drifted day cannot be recovered
            // because the old schema did not retain it.
            migrationBuilder.Sql("""
                UPDATE "SavingsGoals"
                SET "RecurrenceDayOfMonth" = EXTRACT(DAY FROM "TargetDate")::integer
                WHERE "IsRecurring" = TRUE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecurrenceDayOfMonth",
                table: "SavingsGoals");
        }
    }
}
