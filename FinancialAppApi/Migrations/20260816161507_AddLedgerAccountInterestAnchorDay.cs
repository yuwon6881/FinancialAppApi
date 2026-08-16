using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerAccountInterestAnchorDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InterestAnchorDay",
                table: "LedgerAccounts",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE \"LedgerAccounts\" SET \"InterestAnchorDay\" = EXTRACT(DAY FROM \"InterestNextAccrualDate\")::integer WHERE \"InterestAnchorDay\" IS NULL AND \"InterestNextAccrualDate\" IS NOT NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledgeraccounts_interestanchorday",
                table: "LedgerAccounts",
                sql: "\"InterestAnchorDay\" IS NULL OR \"InterestAnchorDay\" BETWEEN 1 AND 31");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ledgeraccounts_interestanchorday",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "InterestAnchorDay",
                table: "LedgerAccounts");
        }
    }
}
