using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingletonAndWishlistPurchaseUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_WishlistItemId",
                table: "Transactions");

            migrationBuilder.AddColumn<int>(
                name: "SingletonKey",
                table: "AppUsers",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Preserve every ledger row while repairing any historical duplicate links:
            // keep the transaction explicitly referenced by the wishlist item (or the
            // earliest deterministic row) and unlink the rest before adding uniqueness.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT t."Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY t."WishlistItemId"
                               ORDER BY
                                   CASE WHEN w."PurchaseTransactionId" = t."Id" THEN 0 ELSE 1 END,
                                   t."Date",
                                   t."Id") AS row_number
                    FROM "Transactions" AS t
                    LEFT JOIN "WishlistItems" AS w ON w."Id" = t."WishlistItemId"
                    WHERE t."WishlistItemId" IS NOT NULL
                )
                UPDATE "Transactions" AS t
                SET "WishlistItemId" = NULL
                FROM ranked
                WHERE ranked."Id" = t."Id" AND ranked.row_number > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_WishlistItemId",
                table: "Transactions",
                column: "WishlistItemId",
                unique: true,
                filter: "\"WishlistItemId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_SingletonKey",
                table: "AppUsers",
                column: "SingletonKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_WishlistItemId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_AppUsers_SingletonKey",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "SingletonKey",
                table: "AppUsers");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_WishlistItemId",
                table: "Transactions",
                column: "WishlistItemId");
        }
    }
}
