using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Drops the automatic interest accrual state. Interest is now recorded by hand from the
    /// bank's own figure, so nothing derives from a stored rate or schedule. The data loss is
    /// deliberate and bounded: only <c>InterestRemainder</c> held value, and it is by definition
    /// under one cent per account. Interest transactions already posted are untouched — they are
    /// ordinary ledger rows and stay exactly as they are.
    /// </summary>
    public partial class RemoveLedgerAccountInterest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ledgeraccounts_interestanchorday",
                table: "LedgerAccounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ledgeraccounts_interestfrequency",
                table: "LedgerAccounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ledgeraccounts_interestrate",
                table: "LedgerAccounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ledgeraccounts_interestremainder",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "InterestAnchorDay",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "InterestEnabled",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "InterestFrequency",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "InterestNextAccrualDate",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "InterestRatePercent",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "InterestRemainder",
                table: "LedgerAccounts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InterestAnchorDay",
                table: "LedgerAccounts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InterestEnabled",
                table: "LedgerAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "InterestFrequency",
                table: "LedgerAccounts",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Monthly");

            migrationBuilder.AddColumn<DateOnly>(
                name: "InterestNextAccrualDate",
                table: "LedgerAccounts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InterestRatePercent",
                table: "LedgerAccounts",
                type: "numeric(7,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "InterestRemainder",
                table: "LedgerAccounts",
                type: "numeric(12,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledgeraccounts_interestanchorday",
                table: "LedgerAccounts",
                sql: "\"InterestAnchorDay\" IS NULL OR \"InterestAnchorDay\" BETWEEN 1 AND 31");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledgeraccounts_interestfrequency",
                table: "LedgerAccounts",
                sql: "\"InterestFrequency\" IN ('Daily', 'Monthly', 'Yearly')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledgeraccounts_interestrate",
                table: "LedgerAccounts",
                sql: "\"InterestRatePercent\" >= 0 AND \"InterestRatePercent\" <= 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledgeraccounts_interestremainder",
                table: "LedgerAccounts",
                sql: "\"InterestRemainder\" >= 0");
        }
    }
}
