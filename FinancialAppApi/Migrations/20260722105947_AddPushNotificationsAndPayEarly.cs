using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPushNotificationsAndPayEarly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "RecurringOccurrenceDate",
                table: "Transactions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushReminderEnabled",
                table: "RecurringPayments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PushReminderLeadDays",
                table: "RecurringPayments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "PushReminderMode",
                table: "RecurringPayments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Once");

            migrationBuilder.AddColumn<bool>(
                name: "PushRemindersEnabled",
                table: "FinancialSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PushReminderDeliveries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RecurringPaymentId = table.Column<string>(type: "text", nullable: false),
                    OccurrenceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ActualOffsetDays = table.Column<int>(type: "integer", nullable: false),
                    SubscriptionId = table.Column<string>(type: "text", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushReminderDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushReminderDeliveries_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FcmToken = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushSubscriptions_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_UserId_RecurringPaymentId_RecurringOccurrenceD~",
                table: "Transactions",
                columns: new[] { "UserId", "RecurringPaymentId", "RecurringOccurrenceDate" },
                unique: true,
                filter: "\"RecurringPaymentId\" IS NOT NULL AND \"RecurringOccurrenceDate\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_recurringpayments_pushreminderleaddays",
                table: "RecurringPayments",
                sql: "\"PushReminderLeadDays\" IN (1, 2, 3, 7)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_recurringpayments_pushremindermode",
                table: "RecurringPayments",
                sql: "\"PushReminderMode\" IN ('Once', 'Daily')");

            migrationBuilder.CreateIndex(
                name: "IX_PushReminderDeliveries_UserId_RecurringPaymentId_Occurrenc~1",
                table: "PushReminderDeliveries",
                columns: new[] { "UserId", "RecurringPaymentId", "OccurrenceDate", "ActualOffsetDays", "SubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushReminderDeliveries_UserId_RecurringPaymentId_Occurrence~",
                table: "PushReminderDeliveries",
                columns: new[] { "UserId", "RecurringPaymentId", "OccurrenceDate", "SubscriptionId" });

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_UserId_DeviceId",
                table: "PushSubscriptions",
                columns: new[] { "UserId", "DeviceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushReminderDeliveries");

            migrationBuilder.DropTable(
                name: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_UserId_RecurringPaymentId_RecurringOccurrenceD~",
                table: "Transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_recurringpayments_pushreminderleaddays",
                table: "RecurringPayments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_recurringpayments_pushremindermode",
                table: "RecurringPayments");

            migrationBuilder.DropColumn(
                name: "RecurringOccurrenceDate",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "PushReminderEnabled",
                table: "RecurringPayments");

            migrationBuilder.DropColumn(
                name: "PushReminderLeadDays",
                table: "RecurringPayments");

            migrationBuilder.DropColumn(
                name: "PushReminderMode",
                table: "RecurringPayments");

            migrationBuilder.DropColumn(
                name: "PushRemindersEnabled",
                table: "FinancialSettings");
        }
    }
}
