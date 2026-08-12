using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLoanInterestMethodsAndRateBasis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RateBasis",
                table: "Loans",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Yearly");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loans_interestmethod",
                table: "Loans");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loans_interestmethod",
                table: "Loans",
                sql: "\"InterestMethod\" IN ('ReducingBalance', 'Flat', 'ReducingBalanceDaily', 'InterestOnly')");

            // Down is safe only when no loan row uses ReducingBalanceDaily or InterestOnly.
            migrationBuilder.AddCheckConstraint(
                name: "ck_loans_ratebasis",
                table: "Loans",
                sql: "\"RateBasis\" IN ('Yearly', 'Monthly')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_loans_interestmethod",
                table: "Loans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loans_ratebasis",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "RateBasis",
                table: "Loans");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loans_interestmethod",
                table: "Loans",
                sql: "\"InterestMethod\" IN ('ReducingBalance', 'Flat')");
        }
    }
}
