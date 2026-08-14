using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class HardCutoverExpandRecurringAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountId",
                table: "RecurringPayments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Existing recurring rows are mapped from the latest valid linked ledger payment.
            // The bucket match prevents a historical transfer leg from being mistaken for the
            // account that pays this schedule. Ambiguous rows deliberately remain null for the
            // operator mapping step before the contract migration.
            migrationBuilder.Sql("""
                UPDATE "RecurringPayments" AS rp
                SET "AccountId" = latest."AccountId"
                FROM (
                    SELECT DISTINCT ON (t."UserId", t."RecurringPaymentId")
                        t."UserId", t."RecurringPaymentId", t."AccountId"
                    FROM "Transactions" AS t
                    INNER JOIN "LedgerAccounts" AS a
                        ON a."UserId" = t."UserId" AND a."Id" = t."AccountId"
                    WHERE t."RecurringPaymentId" IS NOT NULL
                      AND t."AccountId" IS NOT NULL
                      AND lower(t."LedgerCategory") IN ('essentials', 'growth', 'stability', 'rewards')
                      AND lower(t."LedgerCategory") = lower(a."Bucket")
                    ORDER BY t."UserId", t."RecurringPaymentId", t."Date" DESC, t."PostedAt" DESC, t."Id" DESC
                ) AS latest
                WHERE rp."UserId" = latest."UserId"
                  AND rp."Id" = latest."RecurringPaymentId"
                  AND rp."AccountId" IS NULL;

                UPDATE "RecurringPayments" AS rp
                SET "AccountId" = a."Id"
                FROM "LedgerAccounts" AS a
                WHERE rp."AccountId" IS NULL
                  AND a."UserId" = rp."UserId"
                  AND lower(a."Bucket") = lower(rp."LedgerCategory")
                  AND a."IsArchived" = FALSE
                  AND 1 = (
                      SELECT count(*)
                      FROM "LedgerAccounts" AS candidate
                      WHERE candidate."UserId" = rp."UserId"
                        AND lower(candidate."Bucket") = lower(rp."LedgerCategory")
                        AND candidate."IsArchived" = FALSE
                  );
            """);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringPayments_UserId_AccountId",
                table: "RecurringPayments",
                columns: new[] { "UserId", "AccountId" });

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringPayments_LedgerAccounts_UserId_AccountId",
                table: "RecurringPayments",
                columns: new[] { "UserId", "AccountId" },
                principalTable: "LedgerAccounts",
                principalColumns: new[] { "UserId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RecurringPayments_LedgerAccounts_UserId_AccountId",
                table: "RecurringPayments");

            migrationBuilder.DropIndex(
                name: "IX_RecurringPayments_UserId_AccountId",
                table: "RecurringPayments");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "RecurringPayments");
        }
    }
}
