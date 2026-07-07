using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinancialAppApi.Migrations
{
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FinancialSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TargetStabilityFund = table.Column<decimal>(type: "numeric", nullable: false),
                    SelectedMonth = table.Column<string>(type: "text", nullable: false),
                    SelectedYear = table.Column<int>(type: "integer", nullable: false),
                    EssentialsAlloc = table.Column<decimal>(type: "numeric", nullable: false),
                    GrowthAlloc = table.Column<decimal>(type: "numeric", nullable: false),
                    StabilityAlloc = table.Column<decimal>(type: "numeric", nullable: false),
                    RewardsAlloc = table.Column<decimal>(type: "numeric", nullable: false),
                    StabilityOverflowRedirect = table.Column<string>(type: "text", nullable: false, defaultValue: "Split: Growth 50%, Rewards 50%"),
                    CycleDay = table.Column<int>(type: "integer", nullable: false),
                    DarkMode = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    HideSensitive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Currency = table.Column<string>(type: "text", nullable: false, defaultValue: "USD"),
                    VibrationEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurringPayments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    Frequency = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    LedgerCategory = table.Column<string>(type: "text", nullable: false),
                    NextDueDate = table.Column<string>(type: "text", nullable: false),
                    DueDate = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<string>(type: "text", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    EndDate = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TransactionCategories",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    LedgerCategory = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    RecurringPaymentId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Token = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CredentialId = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Token);
                });

            migrationBuilder.CreateTable(
                name: "WebAuthnChallenges",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Purpose = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebAuthnChallenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebAuthnCredentials",
                columns: table => new
                {
                    CredentialId = table.Column<byte[]>(type: "bytea", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    SignCount = table.Column<long>(type: "bigint", nullable: false),
                    DeviceLabel = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebAuthnCredentials", x => x.CredentialId);
                });

            migrationBuilder.CreateTable(
                name: "WishlistItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false, defaultValue: "Medium"),
                    IsPurchased = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    PurchasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishlistItems", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "TransactionCategories",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { "cat-1", "Salary" },
                    { "cat-10", "Transfer" },
                    { "cat-2", "Social" },
                    { "cat-3", "Food" },
                    { "cat-4", "Hobbies" },
                    { "cat-5", "Software" },
                    { "cat-6", "Investment" },
                    { "cat-7", "Entertainment" },
                    { "cat-8", "Transport" },
                    { "cat-9", "Other" }
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AppUsers");
            migrationBuilder.DropTable(name: "FinancialSettings");
            migrationBuilder.DropTable(name: "RecurringPayments");
            migrationBuilder.DropTable(name: "TransactionCategories");
            migrationBuilder.DropTable(name: "Transactions");
            migrationBuilder.DropTable(name: "UserSessions");
            migrationBuilder.DropTable(name: "WebAuthnChallenges");
            migrationBuilder.DropTable(name: "WebAuthnCredentials");
            migrationBuilder.DropTable(name: "WishlistItems");
        }
    }
}
