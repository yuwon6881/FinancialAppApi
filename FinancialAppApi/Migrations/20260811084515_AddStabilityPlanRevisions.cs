using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStabilityPlanRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StabilityPlanRevisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    EffectiveAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TargetStabilityFund = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    StabilityAlloc = table.Column<decimal>(type: "numeric(8,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StabilityPlanRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StabilityPlanRevisions_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StabilityPlanRevisions_UserId_EffectiveAt_Id",
                table: "StabilityPlanRevisions",
                columns: new[] { "UserId", "EffectiveAt", "Id" });

            // The first revision is the only historical plan we can honestly reconstruct: older
            // target/allocation changes were not recorded. Freeze every legacy salary against it
            // (or against the revision effective when that salary was posted) so future setting
            // edits never reinterpret a persisted row. The child is the authoritative persisted
            // Stability share; any excess over the normal share is the old explicit reimbursement.
            migrationBuilder.Sql("""
                INSERT INTO "StabilityPlanRevisions" ("UserId", "EffectiveAt", "TargetStabilityFund", "StabilityAlloc")
                SELECT settings."UserId", TIMESTAMPTZ '1970-01-01 00:00:00+00', settings."TargetStabilityFund", settings."StabilityAlloc"
                FROM "FinancialSettings" settings
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM "StabilityPlanRevisions" existing
                    WHERE existing."UserId" = settings."UserId");

                UPDATE "Transactions" parent
                SET "StabilityRecoveryTopUpAmount" = COALESCE((
                    SELECT ROUND(GREATEST(0, child."Amount" - parent."Amount" * revision."StabilityAlloc"), 2)
                    FROM "Transactions" child
                    CROSS JOIN LATERAL (
                        SELECT plan."StabilityAlloc"
                        FROM "StabilityPlanRevisions" plan
                        WHERE plan."UserId" = parent."UserId"
                          AND plan."EffectiveAt" <= parent."PostedAt"
                        ORDER BY plan."EffectiveAt" DESC, plan."Id" DESC
                        LIMIT 1
                    ) revision
                    WHERE child."UserId" = parent."UserId"
                      AND child."Id" = parent."Id" || '-split-Stability'
                      AND child."LedgerCategory" = 'Transfer:Income->Stability'
                      AND child."Amount" > 0
                ), 0)
                WHERE parent."StabilityRecoveryTopUpAmount" IS NULL
                  AND parent."Amount" > 0
                  AND (LOWER(parent."LedgerCategory") = 'income'
                       OR LOWER(parent."LedgerCategory") LIKE 'incomesplit:%');
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StabilityPlanRevisions");
        }
    }
}
