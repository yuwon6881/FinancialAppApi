using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260709090000_AddWebAuthnPerformanceIndexes")]
    public partial class AddWebAuthnPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_CredentialId",
                table: "UserSessions",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ExpiresAt",
                table: "UserSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_Username_DeviceId",
                table: "UserSessions",
                columns: new[] { "Username", "DeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_WebAuthnChallenges_ExpiresAt",
                table: "WebAuthnChallenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_WebAuthnCredentials_Username",
                table: "WebAuthnCredentials",
                column: "Username");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSessions_CredentialId",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_ExpiresAt",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_Username_DeviceId",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_WebAuthnChallenges_ExpiresAt",
                table: "WebAuthnChallenges");

            migrationBuilder.DropIndex(
                name: "IX_WebAuthnCredentials_Username",
                table: "WebAuthnCredentials");
        }
    }
}
