using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260716100000_AddPasswordVerificationLockout")]
public partial class AddPasswordVerificationLockout : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PasswordVerificationFailedAttempts",
            table: "AppUsers",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "PasswordVerificationLockedUntil",
            table: "AppUsers",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PasswordVerificationFailedAttempts",
            table: "AppUsers");

        migrationBuilder.DropColumn(
            name: "PasswordVerificationLockedUntil",
            table: "AppUsers");
    }
}
