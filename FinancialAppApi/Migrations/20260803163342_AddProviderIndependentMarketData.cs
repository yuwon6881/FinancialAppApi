using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderIndependentMarketData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketPriceBars_Provider_Symbol_Mic_MarketDate",
                table: "MarketPriceBars");

            migrationBuilder.DropIndex(
                name: "IX_InstrumentSearchCaches_NormalizedQuery",
                table: "InstrumentSearchCaches");

            migrationBuilder.AddColumn<string>(
                name: "ExternalInstrumentId",
                table: "MarketPriceBars",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderId",
                table: "MarketDataRefreshJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "Scope",
                table: "MarketDataQuotaWindows",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<string>(
                name: "ProviderId",
                table: "InstrumentSearchCaches",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "InvestmentInstrumentMarketMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    InvestmentInstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalInstrumentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplaySymbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DisplayMic = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestmentInstrumentMarketMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestmentInstrumentMarketMappings_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvestmentInstrumentMarketMappings_InvestmentInstruments_In~",
                        column: x => x.InvestmentInstrumentId,
                        principalTable: "InvestmentInstruments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "Provider", UPPER("Symbol"), UPPER(COALESCE("Mic", '')), "MarketDate"
                               ORDER BY "FetchedAt" DESC, "Id" DESC) AS row_number
                    FROM "MarketPriceBars"
                )
                DELETE FROM "MarketPriceBars"
                WHERE "Id" IN (SELECT "Id" FROM ranked WHERE row_number > 1);

                UPDATE "MarketPriceBars"
                SET "Provider" = CASE WHEN "Provider" = '' THEN 'twelvedata' ELSE LOWER("Provider") END,
                    "ExternalInstrumentId" = UPPER("Symbol") || '|' || UPPER(COALESCE("Mic", ''));
                UPDATE "FxRateBars"
                SET "Provider" = CASE WHEN "Provider" = '' THEN 'twelvedata' ELSE LOWER("Provider") END;
                UPDATE "MarketDataRefreshJobs" SET "ProviderId" = 'twelvedata';
                UPDATE "InstrumentSearchCaches" SET "ProviderId" = 'twelvedata';
                UPDATE "MarketDataQuotaWindows"
                SET "Scope" = 'twelvedata:' || "Scope"
                WHERE "Scope" NOT LIKE 'twelvedata:%';

                INSERT INTO "InvestmentInstrumentMarketMappings"
                    ("UserId", "InvestmentInstrumentId", "ProviderId", "ExternalInstrumentId",
                     "DisplaySymbol", "DisplayMic", "CreatedAt", "UpdatedAt")
                SELECT "UserId", "Id", 'twelvedata',
                       UPPER("ProviderSymbol") || '|' || UPPER(COALESCE("ProviderMic", '')),
                       UPPER("ProviderSymbol"), UPPER("ProviderMic"), "CreatedAt", "UpdatedAt"
                FROM "InvestmentInstruments"
                WHERE NOT "IsCustom" AND "ProviderSymbol" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MarketPriceBars_Provider_ExternalInstrumentId_MarketDate",
                table: "MarketPriceBars",
                columns: new[] { "Provider", "ExternalInstrumentId", "MarketDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentSearchCaches_ProviderId_NormalizedQuery",
                table: "InstrumentSearchCaches",
                columns: new[] { "ProviderId", "NormalizedQuery" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentInstrumentMarketMappings_InvestmentInstrumentId",
                table: "InvestmentInstrumentMarketMappings",
                column: "InvestmentInstrumentId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentInstrumentMarketMappings_ProviderId_ExternalInstr~",
                table: "InvestmentInstrumentMarketMappings",
                columns: new[] { "ProviderId", "ExternalInstrumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentInstrumentMarketMappings_UserId_InvestmentInstrum~",
                table: "InvestmentInstrumentMarketMappings",
                columns: new[] { "UserId", "InvestmentInstrumentId", "ProviderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "MarketDataQuotaWindows"
                SET "Scope" = SUBSTRING("Scope" FROM LENGTH('twelvedata:') + 1)
                WHERE "Scope" LIKE 'twelvedata:%';
                """);

            migrationBuilder.DropTable(
                name: "InvestmentInstrumentMarketMappings");

            migrationBuilder.DropIndex(
                name: "IX_MarketPriceBars_Provider_ExternalInstrumentId_MarketDate",
                table: "MarketPriceBars");

            migrationBuilder.DropIndex(
                name: "IX_InstrumentSearchCaches_ProviderId_NormalizedQuery",
                table: "InstrumentSearchCaches");

            migrationBuilder.DropColumn(
                name: "ExternalInstrumentId",
                table: "MarketPriceBars");

            migrationBuilder.DropColumn(
                name: "ProviderId",
                table: "MarketDataRefreshJobs");

            migrationBuilder.DropColumn(
                name: "ProviderId",
                table: "InstrumentSearchCaches");

            migrationBuilder.AlterColumn<string>(
                name: "Scope",
                table: "MarketDataQuotaWindows",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.CreateIndex(
                name: "IX_MarketPriceBars_Provider_Symbol_Mic_MarketDate",
                table: "MarketPriceBars",
                columns: new[] { "Provider", "Symbol", "Mic", "MarketDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentSearchCaches_NormalizedQuery",
                table: "InstrumentSearchCaches",
                column: "NormalizedQuery",
                unique: true);
        }
    }
}
