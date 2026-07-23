using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInvestmentCashFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvestmentCashFlows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestmentCashFlows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestmentCashFlows_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvestmentCashFlows_InvestmentAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "InvestmentAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentCashFlows_AccountId",
                table: "InvestmentCashFlows",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentCashFlows_UserId_AccountId_Date",
                table: "InvestmentCashFlows",
                columns: new[] { "UserId", "AccountId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvestmentCashFlows");
        }
    }
}
