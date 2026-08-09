using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryLimitPushAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CategoryLimitAlertsEnabled",
                table: "FinancialSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CategoryLimitAlertEvaluations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PreviousCategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PreviousLedgerCategory = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PreviousDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PreviousAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    CurrentCategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CurrentLedgerCategory = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CurrentDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CurrentAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryLimitAlertEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryLimitAlertEvaluations_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CategoryLimitAlertEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CycleKey = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Body = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Tag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CategoryName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryLimitAlertEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryLimitAlertEvents_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CategoryLimitAlertDeliveries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    EventId = table.Column<string>(type: "text", nullable: false),
                    SubscriptionId = table.Column<string>(type: "text", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryLimitAlertDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryLimitAlertDeliveries_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategoryLimitAlertDeliveries_CategoryLimitAlertEvents_Event~",
                        column: x => x.EventId,
                        principalTable: "CategoryLimitAlertEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CategoryLimitAlertMilestones",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    EventId = table.Column<string>(type: "text", nullable: false),
                    CycleKey = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    CategoryName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Milestone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryLimitAlertMilestones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryLimitAlertMilestones_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategoryLimitAlertMilestones_CategoryLimitAlertEvents_Event~",
                        column: x => x.EventId,
                        principalTable: "CategoryLimitAlertEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryLimitAlertDeliveries_EventId",
                table: "CategoryLimitAlertDeliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryLimitAlertDeliveries_UserId_EventId_SubscriptionId",
                table: "CategoryLimitAlertDeliveries",
                columns: new[] { "UserId", "EventId", "SubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CategoryLimitAlertEvaluations_UserId_CreatedAt",
                table: "CategoryLimitAlertEvaluations",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryLimitAlertEvents_UserId_CompletedAt_ExpiresAt",
                table: "CategoryLimitAlertEvents",
                columns: new[] { "UserId", "CompletedAt", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryLimitAlertMilestones_EventId",
                table: "CategoryLimitAlertMilestones",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryLimitAlertMilestones_UserId_CycleKey_CategoryName_M~",
                table: "CategoryLimitAlertMilestones",
                columns: new[] { "UserId", "CycleKey", "CategoryName", "Milestone" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategoryLimitAlertDeliveries");

            migrationBuilder.DropTable(
                name: "CategoryLimitAlertEvaluations");

            migrationBuilder.DropTable(
                name: "CategoryLimitAlertMilestones");

            migrationBuilder.DropTable(
                name: "CategoryLimitAlertEvents");

            migrationBuilder.DropColumn(
                name: "CategoryLimitAlertsEnabled",
                table: "FinancialSettings");
        }
    }
}
