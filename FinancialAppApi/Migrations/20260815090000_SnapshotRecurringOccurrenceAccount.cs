using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations;

/// <summary>
/// Freezes the paying account onto the occurrence beside the Name/Amount/LedgerCategory/PaymentMode
/// snapshot it already carries. Settlement reads this rather than the schedule's current AccountId,
/// so re-pointing a bill moves only its future occurrences while one already materialised still
/// settles where it was scheduled.
/// </summary>
[Migration("20260815090000_SnapshotRecurringOccurrenceAccount")]
[DbContext(typeof(AppDbContext))]
public partial class SnapshotRecurringOccurrenceAccount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AccountId",
            table: "RecurringPaymentOccurrences",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        // A settled occurrence is backfilled from the row that actually paid it, which is the
        // honest historical answer and can differ from the schedule's account today. Pending rows
        // take the parent's, which is where they would be settled from right now. Deliberately
        // left nullable and unbackfilled beyond this: readers fall back to the parent, so a row
        // this cannot answer for degrades to the pre-snapshot behaviour rather than failing.
        migrationBuilder.Sql("""
            UPDATE "RecurringPaymentOccurrences" AS o
            SET "AccountId" = t."AccountId"
            FROM "Transactions" AS t
            WHERE t."Id" = o."SettlementTransactionId"
              AND t."UserId" = o."UserId"
              AND t."AccountId" IS NOT NULL;

            UPDATE "RecurringPaymentOccurrences" AS o
            SET "AccountId" = rp."AccountId"
            FROM "RecurringPayments" AS rp
            WHERE rp."Id" = o."RecurringPaymentId"
              AND rp."UserId" = o."UserId"
              AND o."AccountId" IS NULL
              AND o."Status" = 'Pending';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AccountId",
            table: "RecurringPaymentOccurrences");
    }
}
