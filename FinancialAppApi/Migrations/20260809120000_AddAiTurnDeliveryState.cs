using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260809120000_AddAiTurnDeliveryState")]
public sealed class AddAiTurnDeliveryState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "ActionsDismissedAt",
            table: "AiConversationTurns",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "ActionsResolvedAt",
            table: "AiConversationTurns",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "CompletedAt",
            table: "AiConversationTurns",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Status",
            table: "AiConversationTurns",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Completed");

        migrationBuilder.Sql("""
            UPDATE "AiConversationTurns"
            SET "CompletedAt" = "CreatedAt",
                "ActionsResolvedAt" = CASE WHEN "ActionsJson" = '[]' THEN "CreatedAt" ELSE NULL END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ActionsDismissedAt", table: "AiConversationTurns");
        migrationBuilder.DropColumn(name: "ActionsResolvedAt", table: "AiConversationTurns");
        migrationBuilder.DropColumn(name: "CompletedAt", table: "AiConversationTurns");
        migrationBuilder.DropColumn(name: "Status", table: "AiConversationTurns");
    }
}
