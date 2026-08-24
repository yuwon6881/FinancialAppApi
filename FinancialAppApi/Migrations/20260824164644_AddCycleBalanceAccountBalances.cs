using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCycleBalanceAccountBalances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountBalances",
                table: "CycleBalances",
                type: "jsonb",
                nullable: true);

            // CycleBalances is a pure cache -- CycleBalanceService.InvalidateAllAsync already
            // deletes it wholesale whenever CycleDay changes, and the next read recomputes on the
            // fly. Clearing it here means every row that exists afterwards carries the new column,
            // so no read has to fall back to a full-history rescan to recover it.
            migrationBuilder.Sql("DELETE FROM \"CycleBalances\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountBalances",
                table: "CycleBalances");
        }
    }
}
