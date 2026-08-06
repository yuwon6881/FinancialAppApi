using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringPaymentPaymentMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentMode",
                table: "RecurringPayments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddCheckConstraint(
                name: "ck_recurringpayments_paymentmode",
                table: "RecurringPayments",
                sql: "\"PaymentMode\" IN ('AutoDeduct', 'Manual')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_recurringpayments_paymentmode",
                table: "RecurringPayments");

            migrationBuilder.DropColumn(
                name: "PaymentMode",
                table: "RecurringPayments");
        }
    }
}
