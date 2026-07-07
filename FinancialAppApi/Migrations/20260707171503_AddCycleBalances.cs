using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCycleBalances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CycleBalances",
                columns: table => new
                {
                    Year = table.Column<int>(type: "integer", nullable: false),
                    MonthIndex = table.Column<int>(type: "integer", nullable: false),
                    EssentialsBalance = table.Column<decimal>(type: "numeric", nullable: false),
                    GrowthBalance = table.Column<decimal>(type: "numeric", nullable: false),
                    StabilityBalance = table.Column<decimal>(type: "numeric", nullable: false),
                    RewardsBalance = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CycleBalances", x => new { x.Year, x.MonthIndex });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CycleBalances");
        }
    }
}
