using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInvestmentSnapshotsAndSeriesCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvestmentPeriodSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Segment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AppCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    OpeningValue = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    ClosingValue = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    MarketValue = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    CashValue = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    NetContributions = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    WeightedContributions = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    Dividends = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    RealisedProfitLoss = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    CostBasis = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    MissingReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CarryState = table.Column<string>(type: "jsonb", nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestmentPeriodSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestmentPeriodSnapshots_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarketSeriesCoverages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SeriesKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CoveredFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketSeriesCoverages", x => x.Id);
                    table.CheckConstraint("ck_marketseriescoverages_kind", "\"Kind\" IN ('Price', 'Fx')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentPeriodSnapshots_UserId_PeriodEnd_Segment",
                table: "InvestmentPeriodSnapshots",
                columns: new[] { "UserId", "PeriodEnd", "Segment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketSeriesCoverages_Provider_Kind_SeriesKey",
                table: "MarketSeriesCoverages",
                columns: new[] { "Provider", "Kind", "SeriesKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvestmentPeriodSnapshots");

            migrationBuilder.DropTable(
                name: "MarketSeriesCoverages");
        }
    }
}
