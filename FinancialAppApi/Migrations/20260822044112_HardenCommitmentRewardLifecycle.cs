using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class HardenCommitmentRewardLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                table: "SavingsGoalCompletions",
                type: "timestamp with time zone",
                nullable: true);

            // Normalize legacy focus state before enforcing the invariant. Purchased rewards can
            // never remain focused, and every user with an open reward gets exactly the newest one
            // (CreatedAt, then Id) as the sole focus.
            migrationBuilder.Sql("""
                UPDATE "WishlistItems"
                SET "IsActive" = FALSE
                WHERE "IsPurchased" = TRUE AND "IsActive" = TRUE;

                WITH ranked AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "UserId"
                               ORDER BY "CreatedAt" DESC, "Id" DESC) AS rank
                    FROM "WishlistItems"
                    WHERE "IsPurchased" = FALSE
                )
                UPDATE "WishlistItems" AS item
                SET "IsActive" = (ranked.rank = 1)
                FROM ranked
                WHERE item."Id" = ranked."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_UserId_FocusedOpen",
                table: "WishlistItems",
                column: "UserId",
                unique: true,
                filter: "\"IsActive\" = TRUE AND \"IsPurchased\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_UserId_FocusedOpen",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "ReversedAt",
                table: "SavingsGoalCompletions");
        }
    }
}
