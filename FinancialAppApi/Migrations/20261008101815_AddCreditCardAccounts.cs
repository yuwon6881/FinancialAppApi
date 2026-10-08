using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditCardAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ledgeraccounts_kind",
                table: "LedgerAccounts");

            migrationBuilder.AddColumn<decimal>(
                name: "CreditLimit",
                table: "LedgerAccounts",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledgeraccounts_creditcard_bucket",
                table: "LedgerAccounts",
                sql: "\"Kind\" <> 'CreditCard' OR \"Bucket\" IN ('Essentials', 'Rewards')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledgeraccounts_creditlimit",
                table: "LedgerAccounts",
                sql: "\"CreditLimit\" IS NULL OR (\"Kind\" = 'CreditCard' AND \"CreditLimit\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledgeraccounts_kind",
                table: "LedgerAccounts",
                sql: "\"Kind\" IN ('Bank', 'EWallet', 'Cash', 'Card', 'CreditCard', 'Other')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ledgeraccounts_creditcard_bucket",
                table: "LedgerAccounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ledgeraccounts_creditlimit",
                table: "LedgerAccounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ledgeraccounts_kind",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "CreditLimit",
                table: "LedgerAccounts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledgeraccounts_kind",
                table: "LedgerAccounts",
                sql: "\"Kind\" IN ('Bank', 'EWallet', 'Cash', 'Card', 'Other')");
        }
    }
}
