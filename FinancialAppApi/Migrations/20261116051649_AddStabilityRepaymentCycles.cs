using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStabilityRepaymentCycles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StabilityRepaymentCycles",
                table: "Transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_transactions_stabilityrepaymentcycles",
                table: "Transactions",
                sql: "\"StabilityRepaymentCycles\" BETWEEN 1 AND 24");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_transactions_stabilityrepaymentcycles",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "StabilityRepaymentCycles",
                table: "Transactions");
        }
    }
}
