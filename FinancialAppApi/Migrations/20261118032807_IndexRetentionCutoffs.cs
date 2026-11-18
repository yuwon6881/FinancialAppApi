using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class IndexRetentionCutoffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PendingTwoFactors_ExpiresAt",
                table: "PendingTwoFactors",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_MarketDataRefreshJobs_CompletedAt",
                table: "MarketDataRefreshJobs",
                column: "CompletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingTwoFactors_ExpiresAt",
                table: "PendingTwoFactors");

            migrationBuilder.DropIndex(
                name: "IX_MarketDataRefreshJobs_CompletedAt",
                table: "MarketDataRefreshJobs");
        }
    }
}
