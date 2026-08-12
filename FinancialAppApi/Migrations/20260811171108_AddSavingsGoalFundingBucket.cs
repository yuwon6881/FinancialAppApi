using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSavingsGoalFundingBucket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FundingBucket",
                table: "SavingsGoals",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Rewards");

            migrationBuilder.AddCheckConstraint(
                name: "ck_savingsgoals_fundingbucket",
                table: "SavingsGoals",
                sql: "\"FundingBucket\" IN ('Essentials', 'Rewards')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_savingsgoals_fundingbucket",
                table: "SavingsGoals");

            migrationBuilder.DropColumn(
                name: "FundingBucket",
                table: "SavingsGoals");
        }
    }
}
