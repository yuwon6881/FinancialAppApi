using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class SettleSavingsGoalCompletions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SavingsGoalId",
                table: "Transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCompletionTransactionId",
                table: "SavingsGoals",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SavingsGoalCompletions",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SavingsGoalId = table.Column<int>(type: "integer", nullable: false),
                    PreviousTargetDate = table.Column<DateTime>(type: "date", nullable: false),
                    PreviousEarmarkedAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    PreviousCycleFundedKey = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    PreviousCycleFundedAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    ResultingTargetDate = table.Column<DateTime>(type: "date", nullable: false),
                    WasRecurring = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavingsGoalCompletions", x => x.TransactionId);
                    table.ForeignKey(
                        name: "FK_SavingsGoalCompletions_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_UserId_SavingsGoalId",
                table: "Transactions",
                columns: new[] { "UserId", "SavingsGoalId" });

            migrationBuilder.CreateIndex(
                name: "IX_SavingsGoalCompletions_UserId_SavingsGoalId_CreatedAt",
                table: "SavingsGoalCompletions",
                columns: new[] { "UserId", "SavingsGoalId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavingsGoalCompletions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_UserId_SavingsGoalId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "SavingsGoalId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "LastCompletionTransactionId",
                table: "SavingsGoals");
        }
    }
}
