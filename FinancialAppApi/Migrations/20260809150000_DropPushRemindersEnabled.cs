using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations;

// The account-level push flag is gone: enabled PushSubscription rows are the only thing that
// decides whether a reminder is dispatched, and keeping a second denormalized answer meant the
// AI assistant could report reminders as off while they were being delivered.
[DbContext(typeof(AppDbContext))]
[Migration("20260809150000_DropPushRemindersEnabled")]
public sealed class DropPushRemindersEnabled : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PushRemindersEnabled",
            table: "FinancialSettings");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "PushRemindersEnabled",
            table: "FinancialSettings",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // Restore the flag from the authoritative source rather than leaving every account
        // looking opted out: it always meant "this account has at least one enabled device".
        migrationBuilder.Sql("""
            UPDATE "FinancialSettings" AS s
            SET "PushRemindersEnabled" = TRUE
            WHERE EXISTS (
                SELECT 1 FROM "PushSubscriptions" AS p
                WHERE p."UserId" = s."UserId" AND p."Enabled"
            );
            """);
    }
}
