using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLoanScheduleSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScheduleDueDay",
                table: "Loans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleFrequency",
                table: "Loans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ScheduleStartDate",
                table: "Loans",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleStatus",
                table: "Loans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Incomplete");

            // Existing loans need a one-time review because the former implementation read
            // cadence from the live bill. Preserve the current bill's valid cadence as the
            // starting snapshot, but make missing links or legacy untagged history explicit.
            migrationBuilder.Sql("""
                UPDATE "Loans" AS loan
                SET "ScheduleFrequency" = CASE
                        WHEN payment."Frequency" ILIKE 'monthly' THEN 'Monthly'
                        WHEN payment."Frequency" ILIKE 'annually' THEN 'Annually'
                        ELSE NULL
                    END,
                    "ScheduleDueDay" = CASE
                        WHEN payment."DueDate" BETWEEN 1 AND 31 THEN payment."DueDate"
                        ELSE NULL
                    END,
                    "ScheduleStartDate" = CASE
                        WHEN payment."StartDate" ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
                            AND to_char(to_date(payment."StartDate", 'YYYY-MM-DD'), 'YYYY-MM-DD') = payment."StartDate"
                        THEN to_date(payment."StartDate", 'YYYY-MM-DD')
                        ELSE NULL
                    END
                FROM "RecurringPayments" AS payment
                WHERE payment."Id" = loan."RecurringPaymentId"
                    AND payment."UserId" = loan."UserId";

                UPDATE "Loans" AS loan
                SET "ScheduleStatus" = CASE
                    WHEN loan."ScheduleFrequency" IS NULL
                        OR loan."ScheduleDueDay" IS NULL
                        OR loan."ScheduleStartDate" IS NULL
                        OR EXISTS (
                            SELECT 1
                            FROM "Transactions" AS transaction
                            WHERE transaction."UserId" = loan."UserId"
                                AND transaction."RecurringPaymentId" = loan."RecurringPaymentId"
                                AND transaction."RecurringOccurrenceDate" IS NULL
                        )
                    THEN 'Incomplete'
                    ELSE 'NeedsReview'
                END;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_loans_scheduledueday",
                table: "Loans",
                sql: "\"ScheduleDueDay\" IS NULL OR \"ScheduleDueDay\" BETWEEN 1 AND 31");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loans_schedulestatus",
                table: "Loans",
                sql: "\"ScheduleStatus\" IN ('Complete', 'NeedsReview', 'Incomplete')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_loans_scheduledueday",
                table: "Loans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loans_schedulestatus",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "ScheduleDueDay",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "ScheduleFrequency",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "ScheduleStartDate",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "ScheduleStatus",
                table: "Loans");
        }
    }
}
