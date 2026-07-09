using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class BoundRemainingMoneyColumnPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "TargetStabilityFund",
                table: "FinancialSettings",
                type: "numeric(12,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "Price",
                table: "WishlistItems",
                type: "numeric(12,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "EssentialsBalance",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "GrowthBalance",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "RewardsBalance",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "StabilityBalance",
                table: "CycleBalances",
                type: "numeric(12,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "TargetStabilityFund",
                table: "FinancialSettings",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "Price",
                table: "WishlistItems",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "EssentialsBalance",
                table: "CycleBalances",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "GrowthBalance",
                table: "CycleBalances",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "RewardsBalance",
                table: "CycleBalances",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "StabilityBalance",
                table: "CycleBalances",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)");
        }
    }
}
