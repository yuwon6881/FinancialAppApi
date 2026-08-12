using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260812130000_HardenLoanRecurringLinks")]
public sealed class HardenLoanRecurringLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "Loans"
            SET "ScheduleStatus" = 'Complete'
            WHERE "ScheduleStatus" = 'NeedsReview';

            ALTER TABLE "Loans" DROP CONSTRAINT "ck_loans_schedulestatus";
            ALTER TABLE "Loans" ADD CONSTRAINT "ck_loans_schedulestatus"
                CHECK ("ScheduleStatus" IN ('Complete', 'Incomplete'));

            ALTER TABLE "Loans" ADD CONSTRAINT "FK_Loans_RecurringPayments_RecurringPaymentId"
                FOREIGN KEY ("RecurringPaymentId") REFERENCES "RecurringPayments" ("Id")
                ON DELETE RESTRICT NOT VALID;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "Loans" DROP CONSTRAINT "FK_Loans_RecurringPayments_RecurringPaymentId";
            ALTER TABLE "Loans" DROP CONSTRAINT "ck_loans_schedulestatus";
            ALTER TABLE "Loans" ADD CONSTRAINT "ck_loans_schedulestatus"
                CHECK ("ScheduleStatus" IN ('Complete', 'NeedsReview', 'Incomplete'));
            """);
    }
}
