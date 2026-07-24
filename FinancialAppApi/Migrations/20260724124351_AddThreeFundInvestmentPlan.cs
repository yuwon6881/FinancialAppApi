using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddThreeFundInvestmentPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllocationSleeve",
                table: "InvestmentInstruments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InvestmentPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    UsEquityTarget = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    InternationalExUsTarget = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    BondsTarget = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    WatchDrift = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    AlertDrift = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestmentPlans", x => x.Id);
                    table.CheckConstraint("ck_investmentplans_drift_bands", "\"WatchDrift\" > 0 AND \"AlertDrift\" > \"WatchDrift\" AND \"AlertDrift\" <= 100");
                    table.CheckConstraint("ck_investmentplans_targets_positive", "\"UsEquityTarget\" > 0 AND \"InternationalExUsTarget\" > 0 AND \"BondsTarget\" > 0");
                    table.CheckConstraint("ck_investmentplans_targets_total", "\"UsEquityTarget\" + \"InternationalExUsTarget\" + \"BondsTarget\" = 100");
                    table.ForeignKey(
                        name: "FK_InvestmentPlans_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_investmentinstruments_allocationsleeve",
                table: "InvestmentInstruments",
                sql: "\"AllocationSleeve\" IS NULL OR \"AllocationSleeve\" IN ('USEquity', 'InternationalExUS', 'Bonds')");

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentPlans_UserId",
                table: "InvestmentPlans",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvestmentPlans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_investmentinstruments_allocationsleeve",
                table: "InvestmentInstruments");

            migrationBuilder.DropColumn(
                name: "AllocationSleeve",
                table: "InvestmentInstruments");
        }
    }
}
