using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCardStatementDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StatementDay",
                table: "LedgerAccounts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledgeraccounts_statementday",
                table: "LedgerAccounts",
                sql: "\"StatementDay\" IS NULL OR (\"Kind\" = 'CreditCard' AND \"StatementDay\" BETWEEN 1 AND 31)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ledgeraccounts_statementday",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "StatementDay",
                table: "LedgerAccounts");
        }
    }
}
