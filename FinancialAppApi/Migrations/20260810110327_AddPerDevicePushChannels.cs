using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPerDevicePushChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BillRemindersEnabled",
                table: "PushSubscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CategoryAlertsEnabled",
                table: "PushSubscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Carry the old single-switch state onto the two channels, preserving the invariant
            // Enabled == (BillRemindersEnabled || CategoryAlertsEnabled). Before this migration a
            // registered device received bill reminders, and it also received spending alerts iff
            // its account had turned that account-wide consent on — so an account that had opted
            // in keeps every one of its devices receiving them, and one that had not keeps none.
            // The column default of true would otherwise have handed bill reminders to rows that
            // are switched off.
            migrationBuilder.Sql("""
                UPDATE "PushSubscriptions" AS p
                SET "BillRemindersEnabled" = p."Enabled",
                    "CategoryAlertsEnabled" = p."Enabled" AND COALESCE(f."CategoryLimitAlertsEnabled", FALSE)
                FROM "FinancialSettings" AS f
                WHERE f."UserId" = p."UserId";
                """);
            // Devices belonging to an account with no FinancialSettings row are missed by the join
            // above; they were never able to receive a spending alert either way.
            migrationBuilder.Sql("""
                UPDATE "PushSubscriptions"
                SET "BillRemindersEnabled" = "Enabled",
                    "CategoryAlertsEnabled" = FALSE
                WHERE "UserId" NOT IN (SELECT "UserId" FROM "FinancialSettings");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BillRemindersEnabled",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "CategoryAlertsEnabled",
                table: "PushSubscriptions");
        }
    }
}
