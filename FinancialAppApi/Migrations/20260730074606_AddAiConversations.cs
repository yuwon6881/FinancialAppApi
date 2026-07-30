using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAiConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    StateJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiConversations_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiConversationTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientTurnId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AssistantReply = table.Column<string>(type: "text", nullable: false),
                    ActionsJson = table.Column<string>(type: "text", nullable: false),
                    CloseChat = table.Column<bool>(type: "boolean", nullable: false),
                    Intent = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Topic = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FacetsJson = table.Column<string>(type: "text", nullable: true),
                    KeywordsJson = table.Column<string>(type: "text", nullable: true),
                    SensitiveMode = table.Column<bool>(type: "boolean", nullable: false),
                    ConversationVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiConversationTurns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiConversationTurns_AiConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "AiConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiConversationTurns_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiConversations_UserId",
                table: "AiConversations",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiConversationTurns_ConversationId_ClientTurnId",
                table: "AiConversationTurns",
                columns: new[] { "ConversationId", "ClientTurnId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiConversationTurns_UserId_ConversationId_CreatedAt",
                table: "AiConversationTurns",
                columns: new[] { "UserId", "ConversationId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiConversationTurns");

            migrationBuilder.DropTable(
                name: "AiConversations");
        }
    }
}
