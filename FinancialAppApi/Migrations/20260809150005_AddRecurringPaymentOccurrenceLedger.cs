using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringPaymentOccurrenceLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "NextDueDate",
                table: "RecurringPayments",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateOnly>(
                name: "OccurrenceTrackingStartDate",
                table: "RecurringPayments",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.Sql("""
                UPDATE "RecurringPayments"
                SET "OccurrenceTrackingStartDate" = DATE '2026-08-09';
                """);

            migrationBuilder.CreateTable(
                name: "RecurringPaymentOccurrences",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RecurringPaymentId = table.Column<string>(type: "text", nullable: false),
                    OccurrenceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ScheduledAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LedgerCategory = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PaymentMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PaidDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SettlementTransactionId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringPaymentOccurrences", x => x.Id);
                    table.CheckConstraint("ck_recurringpaymentoccurrences_status", "\"Status\" IN ('Pending', 'Paid', 'Discarded')");
                    table.ForeignKey(
                        name: "FK_RecurringPaymentOccurrences_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringPaymentOccurrences_SettlementTransactionId",
                table: "RecurringPaymentOccurrences",
                column: "SettlementTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringPaymentOccurrences_UserId_RecurringPaymentId_Occur~",
                table: "RecurringPaymentOccurrences",
                columns: new[] { "UserId", "RecurringPaymentId", "OccurrenceDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringPaymentOccurrences_UserId_Status_OccurrenceDate",
                table: "RecurringPaymentOccurrences",
                columns: new[] { "UserId", "Status", "OccurrenceDate" });

            // Only transaction-tagged history is provable. Do not regenerate old pending rows
            // from a schedule that may have been edited since those cycles occurred.
            migrationBuilder.Sql("""
                INSERT INTO "RecurringPaymentOccurrences" (
                    "Id", "UserId", "RecurringPaymentId", "OccurrenceDate", "Name",
                    "ScheduledAmount", "Category", "LedgerCategory", "PaymentMode", "Status",
                    "PaidDate", "SettlementTransactionId")
                SELECT
                    'occ-' || t."RecurringPaymentId" || '-' || to_char(t."RecurringOccurrenceDate", 'YYYYMMDD'),
                    t."UserId",
                    t."RecurringPaymentId",
                    t."RecurringOccurrenceDate",
                    t."Description",
                    CASE WHEN t."Amount" = 0 AND t."LedgerCategory" = 'Discarded'
                         THEN NULL ELSE abs(t."Amount") END,
                    t."Category",
                    CASE WHEN t."Amount" = 0 AND t."LedgerCategory" = 'Discarded'
                         THEN NULL ELSE t."LedgerCategory" END,
                    COALESCE(r."PaymentMode", 'Manual'),
                    CASE WHEN t."Amount" = 0 AND t."LedgerCategory" = 'Discarded'
                         THEN 'Discarded' ELSE 'Paid' END,
                    CASE WHEN t."Amount" = 0 AND t."LedgerCategory" = 'Discarded'
                         THEN NULL ELSE (t."Date" AT TIME ZONE 'UTC')::date END,
                    t."Id"
                FROM "Transactions" t
                LEFT JOIN "RecurringPayments" r ON r."Id" = t."RecurringPaymentId"
                WHERE t."RecurringPaymentId" IS NOT NULL
                  AND t."RecurringOccurrenceDate" IS NOT NULL
                ON CONFLICT ("UserId", "RecurringPaymentId", "OccurrenceDate") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurringPaymentOccurrences");

            migrationBuilder.DropColumn(
                name: "OccurrenceTrackingStartDate",
                table: "RecurringPayments");

            migrationBuilder.AlterColumn<string>(
                name: "NextDueDate",
                table: "RecurringPayments",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
