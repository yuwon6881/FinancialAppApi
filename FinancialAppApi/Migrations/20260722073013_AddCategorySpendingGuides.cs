using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCategorySpendingGuides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CycleLimit",
                table: "TransactionCategories",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CategorySpendingGuides",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CategoryName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EffectiveFromCycleKey = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    LimitAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategorySpendingGuides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategorySpendingGuides_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CategorySpendingGuides_UserId_CategoryName_EffectiveFromCyc~",
                table: "CategorySpendingGuides",
                columns: new[] { "UserId", "CategoryName", "EffectiveFromCycleKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CategorySpendingGuides_UserId_EffectiveFromCycleKey",
                table: "CategorySpendingGuides",
                columns: new[] { "UserId", "EffectiveFromCycleKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategorySpendingGuides");

            migrationBuilder.DropColumn(
                name: "CycleLimit",
                table: "TransactionCategories");
        }
    }
}
