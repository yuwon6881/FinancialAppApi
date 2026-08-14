using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations;

/// <summary>
/// The contract half of the explicit-account cutover. The expand migration leaves the old
/// IsDefault column in place so the first release can backfill recurring payments safely. This
/// migration is intentionally a separate deployment boundary because production applies EF
/// migrations before starting the new Cloud Run revision.
/// </summary>
[Migration("20260814154000_HardCutoverLedgerAccountsContract")]
[DbContext(typeof(AppDbContext))]
public partial class HardCutoverLedgerAccountsContract : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM "RecurringPayments" AS rp
                    LEFT JOIN "LedgerAccounts" AS a
                      ON a."UserId" = rp."UserId" AND a."Id" = rp."AccountId"
                    WHERE rp."AccountId" IS NULL
                       OR a."Id" IS NULL
                       OR lower(a."Bucket") <> lower(rp."LedgerCategory")
                       OR (rp."Active" = TRUE AND a."IsArchived" = TRUE)
                ) THEN
                    RAISE EXCEPTION 'Hard cutover preflight failed: unresolved recurring payment account placement.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM "Transactions"
                    WHERE lower("LedgerCategory") LIKE 'incomesplit:%'
                ) THEN
                    RAISE EXCEPTION 'Hard cutover preflight failed: legacy IncomeSplit parent rows remain.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM "Transactions" AS t
                    WHERE lower(coalesce(t."AccountId", '')) LIKE 'acct-migrated-%'
                       OR lower(coalesce(t."AccountId", '')) LIKE 'acct-default-%'
                       OR lower(coalesce(t."CounterAccountId", '')) LIKE 'acct-migrated-%'
                       OR lower(coalesce(t."CounterAccountId", '')) LIKE 'acct-default-%'
                ) OR EXISTS (
                    SELECT 1
                    FROM "RecurringPayments" AS rp
                    WHERE lower(coalesce(rp."AccountId", '')) LIKE 'acct-migrated-%'
                       OR lower(coalesce(rp."AccountId", '')) LIKE 'acct-default-%'
                ) THEN
                    RAISE EXCEPTION 'Hard cutover preflight failed: system placeholder accounts are still referenced.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM "AppUsers" AS u
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "LedgerAccounts" AS a
                        WHERE a."UserId" = u."Id" AND a."Bucket" = 'Essentials' AND a."IsArchived" = FALSE
                    )
                       OR NOT EXISTS (
                        SELECT 1 FROM "LedgerAccounts" AS a
                        WHERE a."UserId" = u."Id" AND a."Bucket" = 'Growth' AND a."IsArchived" = FALSE
                    )
                       OR NOT EXISTS (
                        SELECT 1 FROM "LedgerAccounts" AS a
                        WHERE a."UserId" = u."Id" AND a."Bucket" = 'Stability' AND a."IsArchived" = FALSE
                    )
                       OR NOT EXISTS (
                        SELECT 1 FROM "LedgerAccounts" AS a
                        WHERE a."UserId" = u."Id" AND a."Bucket" = 'Rewards' AND a."IsArchived" = FALSE
                    )
                ) THEN
                    RAISE EXCEPTION 'Hard cutover preflight failed: every user needs one live account in each bucket.';
                END IF;
            END $$;

            DELETE FROM "LedgerAccounts" AS a
            WHERE (lower(a."Id") LIKE 'acct-migrated-%' OR lower(a."Id") LIKE 'acct-default-%')
              AND NOT EXISTS (
                  SELECT 1 FROM "Transactions" AS t
                  WHERE t."UserId" = a."UserId"
                    AND (t."AccountId" = a."Id" OR t."CounterAccountId" = a."Id")
              )
              AND NOT EXISTS (
                  SELECT 1 FROM "RecurringPayments" AS rp
                  WHERE rp."UserId" = a."UserId" AND rp."AccountId" = a."Id"
              );
            """);

        migrationBuilder.AlterColumn<string>(
            name: "AccountId",
            table: "RecurringPayments",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(100)",
            oldMaxLength: 100,
            oldNullable: true);

        migrationBuilder.DropIndex(
            name: "IX_LedgerAccounts_UserId_Bucket",
            table: "LedgerAccounts");

        migrationBuilder.DropColumn(
            name: "IsDefault",
            table: "LedgerAccounts");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsDefault",
            table: "LedgerAccounts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateIndex(
            name: "IX_LedgerAccounts_UserId_Bucket",
            table: "LedgerAccounts",
            columns: new[] { "UserId", "Bucket" },
            unique: true,
            filter: "\"IsDefault\"");

        migrationBuilder.Sql("""
            WITH ranked AS (
                SELECT "Id",
                       row_number() OVER (
                           PARTITION BY "UserId", "Bucket"
                           ORDER BY "IsArchived" ASC, "CreatedAt" ASC, "Id" ASC
                       ) AS position
                FROM "LedgerAccounts"
            )
            UPDATE "LedgerAccounts" AS a
            SET "IsDefault" = ranked.position = 1
            FROM ranked
            WHERE a."Id" = ranked."Id";
            """);

        migrationBuilder.AlterColumn<string>(
            name: "AccountId",
            table: "RecurringPayments",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(100)",
            oldMaxLength: 100);
    }
}
