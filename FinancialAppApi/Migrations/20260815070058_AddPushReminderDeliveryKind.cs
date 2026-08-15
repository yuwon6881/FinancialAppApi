using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPushReminderDeliveryKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushReminderDeliveries_UserId_RecurringPaymentId_Occurrenc~1",
                table: "PushReminderDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_PushReminderDeliveries_UserId_RecurringPaymentId_Occurrence~",
                table: "PushReminderDeliveries");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "PushReminderDeliveries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Reminder");

            migrationBuilder.CreateIndex(
                name: "IX_PushReminderDeliveries_UserId_RecurringPaymentId_Occurrenc~1",
                table: "PushReminderDeliveries",
                columns: new[] { "UserId", "RecurringPaymentId", "OccurrenceDate", "ActualOffsetDays", "SubscriptionId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushReminderDeliveries_UserId_RecurringPaymentId_Occurrence~",
                table: "PushReminderDeliveries",
                columns: new[] { "UserId", "RecurringPaymentId", "OccurrenceDate", "SubscriptionId", "Kind" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_pushreminderdeliveries_kind",
                table: "PushReminderDeliveries",
                sql: "\"Kind\" IN ('Reminder', 'Shortfall')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushReminderDeliveries_UserId_RecurringPaymentId_Occurrenc~1",
                table: "PushReminderDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_PushReminderDeliveries_UserId_RecurringPaymentId_Occurrence~",
                table: "PushReminderDeliveries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pushreminderdeliveries_kind",
                table: "PushReminderDeliveries");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PushReminderDeliveries");

            migrationBuilder.CreateIndex(
                name: "IX_PushReminderDeliveries_UserId_RecurringPaymentId_Occurrenc~1",
                table: "PushReminderDeliveries",
                columns: new[] { "UserId", "RecurringPaymentId", "OccurrenceDate", "ActualOffsetDays", "SubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushReminderDeliveries_UserId_RecurringPaymentId_Occurrence~",
                table: "PushReminderDeliveries",
                columns: new[] { "UserId", "RecurringPaymentId", "OccurrenceDate", "SubscriptionId" });
        }
    }
}
