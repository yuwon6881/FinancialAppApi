using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLoans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Loans",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecurringPaymentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OpeningPrincipal = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    TrackingStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AnnualRatePercent = table.Column<decimal>(type: "numeric(7,4)", nullable: false),
                    TermPeriods = table.Column<int>(type: "integer", nullable: false),
                    InterestMethod = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "ReducingBalance")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loans", x => x.Id);
                    table.CheckConstraint("ck_loans_annualrate", "\"AnnualRatePercent\" >= 0 AND \"AnnualRatePercent\" <= 100");
                    table.CheckConstraint("ck_loans_interestmethod", "\"InterestMethod\" IN ('ReducingBalance', 'Flat')");
                    table.CheckConstraint("ck_loans_openingprincipal", "\"OpeningPrincipal\" > 0");
                    table.CheckConstraint("ck_loans_term", "\"TermPeriods\" > 0 AND \"TermPeriods\" <= 360");
                    table.ForeignKey(
                        name: "FK_Loans_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Loans_UserId_RecurringPaymentId",
                table: "Loans",
                columns: new[] { "UserId", "RecurringPaymentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Loans");
        }
    }
}
