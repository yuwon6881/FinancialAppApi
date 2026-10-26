using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class DropRedundantRecurringPaymentUserIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecurringPayments_UserId",
                table: "RecurringPayments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RecurringPayments_UserId",
                table: "RecurringPayments",
                column: "UserId");
        }
    }
}
