using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    [Migration("20260709000000_LinkWishlistPurchasesToTransactions")]
    public partial class LinkWishlistPurchasesToTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WishlistItemId",
                table: "Transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurchaseTransactionId",
                table: "WishlistItems",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_WishlistItemId",
                table: "Transactions",
                column: "WishlistItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_PurchaseTransactionId",
                table: "WishlistItems",
                column: "PurchaseTransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_WishlistItemId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_PurchaseTransactionId",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "WishlistItemId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "PurchaseTransactionId",
                table: "WishlistItems");
        }
    }
}
