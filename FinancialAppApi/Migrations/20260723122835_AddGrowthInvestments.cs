using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddGrowthInvestments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FxRateBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BaseCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    QuoteCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    MarketDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxRateBars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstrumentSearchCaches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NormalizedQuery = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ResultsJson = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentSearchCaches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvestmentAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BaseCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestmentAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestmentAccounts_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvestmentInstruments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Exchange = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Mic = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Country = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ProviderSymbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProviderMic = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    IsCustom = table.Column<bool>(type: "boolean", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestmentInstruments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestmentInstruments_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarketDataQuotaWindows",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Used = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketDataQuotaWindows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketDataRefreshJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    UpdatedItems = table.Column<int>(type: "integer", nullable: false),
                    TotalItems = table.Column<int>(type: "integer", nullable: false),
                    PendingItemsJson = table.Column<string>(type: "text", nullable: false),
                    Warning = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketDataRefreshJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketDataRefreshJobs_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarketPriceBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Mic = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    MarketDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Close = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketPriceBars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvestmentTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TradeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Units = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    CashAmount = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    Fees = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    Taxes = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    TradeFxRate = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LinkedTransferId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestmentTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestmentTransactions_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvestmentTransactions_InvestmentAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "InvestmentAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvestmentTransactions_InvestmentInstruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalTable: "InvestmentInstruments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvestmentTransactions_InvestmentTransactions_LinkedTransfe~",
                        column: x => x.LinkedTransferId,
                        principalTable: "InvestmentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ManualPriceOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    FxRate = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualPriceOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualPriceOverrides_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ManualPriceOverrides_InvestmentInstruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalTable: "InvestmentInstruments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FxRateBars_Provider_BaseCurrency_QuoteCurrency_MarketDate",
                table: "FxRateBars",
                columns: new[] { "Provider", "BaseCurrency", "QuoteCurrency", "MarketDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentSearchCaches_ExpiresAt",
                table: "InstrumentSearchCaches",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentSearchCaches_NormalizedQuery",
                table: "InstrumentSearchCaches",
                column: "NormalizedQuery",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentAccounts_UserId_Name",
                table: "InvestmentAccounts",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentInstruments_UserId_Symbol_ProviderMic",
                table: "InvestmentInstruments",
                columns: new[] { "UserId", "Symbol", "ProviderMic" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentTransactions_AccountId",
                table: "InvestmentTransactions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentTransactions_InstrumentId",
                table: "InvestmentTransactions",
                column: "InstrumentId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentTransactions_LinkedTransferId",
                table: "InvestmentTransactions",
                column: "LinkedTransferId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentTransactions_UserId_AccountId_InstrumentId_TradeD~",
                table: "InvestmentTransactions",
                columns: new[] { "UserId", "AccountId", "InstrumentId", "TradeDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ManualPriceOverrides_InstrumentId",
                table: "ManualPriceOverrides",
                column: "InstrumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualPriceOverrides_UserId_InstrumentId_MarketDate",
                table: "ManualPriceOverrides",
                columns: new[] { "UserId", "InstrumentId", "MarketDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketDataQuotaWindows_Scope_WindowStart",
                table: "MarketDataQuotaWindows",
                columns: new[] { "Scope", "WindowStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketDataRefreshJobs_UserId_Status_UpdatedAt",
                table: "MarketDataRefreshJobs",
                columns: new[] { "UserId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketPriceBars_Provider_Symbol_Mic_MarketDate",
                table: "MarketPriceBars",
                columns: new[] { "Provider", "Symbol", "Mic", "MarketDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FxRateBars");

            migrationBuilder.DropTable(
                name: "InstrumentSearchCaches");

            migrationBuilder.DropTable(
                name: "InvestmentTransactions");

            migrationBuilder.DropTable(
                name: "ManualPriceOverrides");

            migrationBuilder.DropTable(
                name: "MarketDataQuotaWindows");

            migrationBuilder.DropTable(
                name: "MarketDataRefreshJobs");

            migrationBuilder.DropTable(
                name: "MarketPriceBars");

            migrationBuilder.DropTable(
                name: "InvestmentAccounts");

            migrationBuilder.DropTable(
                name: "InvestmentInstruments");
        }
    }
}
