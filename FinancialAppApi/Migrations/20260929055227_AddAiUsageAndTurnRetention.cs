using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageAndTurnRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ToolTraceJson",
                table: "AiConversationTurns",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiUsageDays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CachedTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ReasoningTokens = table.Column<long>(type: "bigint", nullable: false),
                    Calls = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiUsageDays_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiConversationTurns_CreatedAt",
                table: "AiConversationTurns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageDays_Date",
                table: "AiUsageDays",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageDays_UserId_Date",
                table: "AiUsageDays",
                columns: new[] { "UserId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsageDays");

            migrationBuilder.DropIndex(
                name: "IX_AiConversationTurns_CreatedAt",
                table: "AiConversationTurns");

            migrationBuilder.DropColumn(
                name: "ToolTraceJson",
                table: "AiConversationTurns");
        }
    }
}
