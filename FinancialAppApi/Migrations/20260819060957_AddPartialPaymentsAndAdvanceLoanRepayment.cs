
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPartialPaymentsAndAdvanceLoanRepayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_UserId_RecurringPaymentId_RecurringOccurrenceD~",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_RecurringPaymentOccurrences_SettlementTransactionId",
                table: "RecurringPaymentOccurrences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_recurringpaymentoccurrences_status",
                table: "RecurringPaymentOccurrences");

            migrationBuilder.DropColumn(
                name: "SettlementTransactionId",
                table: "RecurringPaymentOccurrences");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "RecurringPaymentOccurrences",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.CreateTable(
                name: "LoanRepaymentActions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoanId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecurringPaymentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LenderQuoteAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReplayRevision = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PriorPaymentActive = table.Column<bool>(type: "boolean", nullable: false),
                    PriorPaymentEndDate = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TransactionIds = table.Column<string>(type: "text", nullable: false),
                    TargetOccurrenceDates = table.Column<string>(type: "text", nullable: false),
                    UndoFingerprints = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanRepaymentActions", x => x.Id);
                    table.CheckConstraint("ck_loanrepaymentactions_kind", "\"Kind\" IN ('AdvanceCycles', 'FullSettlement')");
                    table.ForeignKey(
                        name: "FK_LoanRepaymentActions_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_UserId_RecurringPaymentId_RecurringOccurrenceD~",
                table: "Transactions",
                columns: new[] { "UserId", "RecurringPaymentId", "RecurringOccurrenceDate" },
                filter: "\"RecurringPaymentId\" IS NOT NULL AND \"RecurringOccurrenceDate\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_recurringpaymentoccurrences_status",
                table: "RecurringPaymentOccurrences",
                sql: "\"Status\" IN ('Pending', 'PartiallyPaid', 'Paid', 'Discarded', 'SettledByLoanPayoff')");

            migrationBuilder.CreateIndex(
                name: "IX_LoanRepaymentActions_UserId_LoanId",
                table: "LoanRepaymentActions",
                columns: new[] { "UserId", "LoanId" },
                unique: true,
                filter: "\"Kind\" = 'FullSettlement'");

            migrationBuilder.CreateIndex(
                name: "IX_LoanRepaymentActions_UserId_LoanId_CreatedAt",
                table: "LoanRepaymentActions",
                columns: new[] { "UserId", "LoanId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanRepaymentActions_UserId_RecurringPaymentId",
                table: "LoanRepaymentActions",
                columns: new[] { "UserId", "RecurringPaymentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoanRepaymentActions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_UserId_RecurringPaymentId_RecurringOccurrenceD~",
                table: "Transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_recurringpaymentoccurrences_status",
                table: "RecurringPaymentOccurrences");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "RecurringPaymentOccurrences",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30);

            migrationBuilder.AddColumn<string>(
                name: "SettlementTransactionId",
                table: "RecurringPaymentOccurrences",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_UserId_RecurringPaymentId_RecurringOccurrenceD~",
                table: "Transactions",
                columns: new[] { "UserId", "RecurringPaymentId", "RecurringOccurrenceDate" },
                unique: true,
                filter: "\"RecurringPaymentId\" IS NOT NULL AND \"RecurringOccurrenceDate\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringPaymentOccurrences_SettlementTransactionId",
                table: "RecurringPaymentOccurrences",
                column: "SettlementTransactionId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_recurringpaymentoccurrences_status",
                table: "RecurringPaymentOccurrences",
                sql: "\"Status\" IN ('Pending', 'Paid', 'Discarded')");
        }
    }
}
