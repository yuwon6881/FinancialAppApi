using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations;

/// <summary>
/// Converts the nullable account columns introduced by AddLedgerAccounts into a durable account
/// ledger. Existing bucket-only rows are placed on the bucket's stable migration default; this is
/// deliberately a consolidation, not a fabricated claim about which historical bank held them.
/// </summary>
public partial class EnforceLedgerAccountTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_ledgeraccounts_kind",
            table: "LedgerAccounts");

        migrationBuilder.AddUniqueConstraint(
            name: "AK_LedgerAccounts_UserId_Id",
            table: "LedgerAccounts",
            columns: new[] { "UserId", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_Transactions_UserId_CounterAccountId",
            table: "Transactions",
            columns: new[] { "UserId", "CounterAccountId" });

        migrationBuilder.Sql("""
            UPDATE "LedgerAccounts"
            SET "IsDefault" = FALSE
            WHERE "IsArchived" = TRUE AND "IsDefault" = TRUE;

            WITH ranked_defaults AS (
                SELECT "Id",
                       ROW_NUMBER() OVER (
                           PARTITION BY "UserId", "Bucket"
                           ORDER BY "CreatedAt", "Id") AS row_number
                FROM "LedgerAccounts"
                WHERE "IsDefault" = TRUE AND "IsArchived" = FALSE
            )
            UPDATE "LedgerAccounts" account
            SET "IsDefault" = FALSE
            FROM ranked_defaults ranked
            WHERE account."Id" = ranked."Id" AND ranked.row_number > 1;

            INSERT INTO "LedgerAccounts" ("Id", "UserId", "Name", "Bucket", "Kind", "IsDefault", "IsArchived", "CreatedAt", "UpdatedAt")
            SELECT
                'acct-migrated-' || md5(u."Id" || ':' || buckets."Bucket"),
                u."Id",
                CASE
                    WHEN NOT EXISTS (
                        SELECT 1 FROM "LedgerAccounts" existing
                        WHERE existing."UserId" = u."Id" AND existing."Name" = buckets."Bucket" || ' balance'
                    ) THEN buckets."Bucket" || ' balance'
                    WHEN NOT EXISTS (
                        SELECT 1 FROM "LedgerAccounts" existing
                        WHERE existing."UserId" = u."Id" AND existing."Name" = buckets."Bucket" || ' balance (migrated)'
                    ) THEN buckets."Bucket" || ' balance (migrated)'
                    ELSE buckets."Bucket" || ' balance (migrated ' || substr(md5(u."Id" || ':' || buckets."Bucket"), 1, 8) || ')'
                END,
                buckets."Bucket",
                'Other',
                TRUE,
                FALSE,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            FROM "AppUsers" u
            CROSS JOIN (VALUES ('Essentials'), ('Growth'), ('Stability'), ('Rewards')) AS buckets("Bucket")
            WHERE NOT EXISTS (
                SELECT 1 FROM "LedgerAccounts" existing
                WHERE existing."UserId" = u."Id"
                  AND existing."Bucket" = buckets."Bucket"
                  AND existing."IsDefault" = TRUE
                  AND existing."IsArchived" = FALSE
            );
            """);

        // Ordinary bucket rows and one-sided Income split rows use AccountId. A normal transfer
        // has one source and one destination leg, so its two account columns are filled separately.
        migrationBuilder.Sql("""
            UPDATE "Transactions" t
            SET "AccountId" = a."Id"
            FROM "LedgerAccounts" a
            WHERE t."UserId" = a."UserId"
              AND t."AccountId" IS NULL
              AND a."IsDefault" = TRUE
              AND a."IsArchived" = FALSE
              AND t."LedgerCategory" = a."Bucket";

            UPDATE "Transactions" t
            SET
                "AccountId" = COALESCE(t."AccountId", source_account."Id"),
                "CounterAccountId" = COALESCE(
                    t."CounterAccountId",
                    (
                        SELECT target_account."Id"
                        FROM "LedgerAccounts" target_account
                        WHERE target_account."UserId" = t."UserId"
                          AND target_account."Bucket" = trim(split_part(split_part(t."LedgerCategory", ':', 2), '->', 2))
                          AND target_account."IsDefault" = TRUE
                          AND target_account."IsArchived" = FALSE
                        LIMIT 1
                    )
                )
            FROM "LedgerAccounts" source_account
            WHERE t."LedgerCategory" LIKE 'Transfer:%'
              AND lower(trim(split_part(split_part(t."LedgerCategory", ':', 2), '->', 1))) <> 'income'
              AND source_account."UserId" = t."UserId"
              AND source_account."Bucket" = trim(split_part(split_part(t."LedgerCategory", ':', 2), '->', 1))
              AND source_account."IsDefault" = TRUE
              AND source_account."IsArchived" = FALSE;
            """);

        // Income -> bucket has no source bucket leg, so resolve its one-sided destination
        // explicitly and keep CounterAccountId empty.
        migrationBuilder.Sql("""
            UPDATE "Transactions" t
            SET
                "AccountId" = COALESCE(t."AccountId", a."Id"),
                "CounterAccountId" = NULL
            FROM "LedgerAccounts" a
            WHERE t."LedgerCategory" LIKE 'Transfer:Income->%'
              AND t."UserId" = a."UserId"
              AND a."Bucket" = trim(split_part(split_part(t."LedgerCategory", ':', 2), '->', 2))
              AND a."IsDefault" = TRUE
              AND a."IsArchived" = FALSE;
            """);

        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM "Transactions" t
                    LEFT JOIN "LedgerAccounts" a
                      ON a."Id" = t."AccountId" AND a."UserId" = t."UserId"
                    WHERE t."LedgerCategory" IN ('Essentials', 'Growth', 'Stability', 'Rewards')
                      AND (a."Id" IS NULL OR a."Bucket" <> t."LedgerCategory")
                ) THEN
                    RAISE EXCEPTION 'Ledger account migration found an invalid ordinary bucket account reference';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM "Transactions" t
                    LEFT JOIN "LedgerAccounts" source_account
                      ON source_account."Id" = t."AccountId" AND source_account."UserId" = t."UserId"
                    LEFT JOIN "LedgerAccounts" target_account
                      ON target_account."Id" = t."CounterAccountId" AND target_account."UserId" = t."UserId"
                    WHERE t."LedgerCategory" LIKE 'Transfer:%'
                      AND (
                          source_account."Id" IS NULL
                          OR (
                              lower(trim(split_part(split_part(t."LedgerCategory", ':', 2), '->', 1))) <> 'income'
                              AND source_account."Bucket" <> trim(split_part(split_part(t."LedgerCategory", ':', 2), '->', 1))
                          )
                          OR (
                              lower(trim(split_part(split_part(t."LedgerCategory", ':', 2), '->', 1))) = 'income'
                              AND source_account."Bucket" <> trim(split_part(split_part(t."LedgerCategory", ':', 2), '->', 2))
                          )
                          OR (
                              lower(trim(split_part(split_part(t."LedgerCategory", ':', 2), '->', 1))) <> 'income'
                              AND (target_account."Id" IS NULL OR target_account."Bucket" <> trim(split_part(split_part(t."LedgerCategory", ':', 2), '->', 2)))
                          )
                      )
                ) THEN
                    RAISE EXCEPTION 'Ledger account migration found an invalid transfer account reference';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM "Transactions" t
                    LEFT JOIN "LedgerAccounts" source_account
                      ON source_account."Id" = t."AccountId" AND source_account."UserId" = t."UserId"
                    LEFT JOIN "LedgerAccounts" target_account
                      ON target_account."Id" = t."CounterAccountId" AND target_account."UserId" = t."UserId"
                    WHERE t."LedgerCategory" = 'AccountMove'
                      AND (
                          source_account."Id" IS NULL
                          OR target_account."Id" IS NULL
                          OR source_account."Bucket" <> target_account."Bucket"
                          OR t."AccountId" = t."CounterAccountId"
                      )
                ) THEN
                    RAISE EXCEPTION 'Ledger account migration found a malformed AccountMove row';
                END IF;
            END $$;
            """);

        // Rows without a bucket leg never consume account placement. Older optional clients could
        // still send FK-valid ids here, so normalize them before the strict exempt-row branch lands.
        migrationBuilder.Sql("""
            UPDATE "Transactions"
            SET "AccountId" = NULL, "CounterAccountId" = NULL
            WHERE lower("LedgerCategory") NOT IN ('essentials', 'growth', 'stability', 'rewards', 'accountmove')
              AND lower("LedgerCategory") NOT LIKE 'transfer:%';
            """);

        migrationBuilder.AddCheckConstraint(
            name: "ck_ledgeraccounts_kind",
            table: "LedgerAccounts",
            sql: "\"Kind\" IN ('Bank', 'EWallet', 'Cash', 'Card', 'Other')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_account_tracking",
            table: "Transactions",
            sql: "(lower(\"LedgerCategory\") IN ('essentials', 'growth', 'stability', 'rewards') AND \"AccountId\" IS NOT NULL AND \"CounterAccountId\" IS NULL) OR (lower(\"LedgerCategory\") = 'accountmove' AND \"AccountId\" IS NOT NULL AND \"CounterAccountId\" IS NOT NULL) OR (lower(\"LedgerCategory\") LIKE 'transfer:%' AND ((lower(\"LedgerCategory\") LIKE 'transfer:income->%' AND \"AccountId\" IS NOT NULL AND \"CounterAccountId\" IS NULL) OR (lower(\"LedgerCategory\") NOT LIKE 'transfer:income->%' AND \"AccountId\" IS NOT NULL AND \"CounterAccountId\" IS NOT NULL))) OR (lower(\"LedgerCategory\") NOT IN ('essentials', 'growth', 'stability', 'rewards', 'accountmove') AND lower(\"LedgerCategory\") NOT LIKE 'transfer:%' AND \"AccountId\" IS NULL AND \"CounterAccountId\" IS NULL)");

        migrationBuilder.AddForeignKey(
            name: "FK_Transactions_LedgerAccounts_UserId_AccountId",
            table: "Transactions",
            columns: new[] { "UserId", "AccountId" },
            principalTable: "LedgerAccounts",
            principalColumns: new[] { "UserId", "Id" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_Transactions_LedgerAccounts_UserId_CounterAccountId",
            table: "Transactions",
            columns: new[] { "UserId", "CounterAccountId" },
            principalTable: "LedgerAccounts",
            principalColumns: new[] { "UserId", "Id" },
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Transactions_LedgerAccounts_UserId_AccountId",
            table: "Transactions");
        migrationBuilder.DropForeignKey(
            name: "FK_Transactions_LedgerAccounts_UserId_CounterAccountId",
            table: "Transactions");
        migrationBuilder.DropCheckConstraint("ck_transactions_account_tracking", "Transactions");
        migrationBuilder.DropCheckConstraint("ck_ledgeraccounts_kind", "LedgerAccounts");
        migrationBuilder.Sql("UPDATE \"LedgerAccounts\" SET \"Kind\" = 'Bank' WHERE \"Kind\" = 'Other';");
        migrationBuilder.AddCheckConstraint(
            name: "ck_ledgeraccounts_kind",
            table: "LedgerAccounts",
            sql: "\"Kind\" IN ('Bank', 'EWallet', 'Cash', 'Card')");
        migrationBuilder.DropIndex("IX_Transactions_UserId_CounterAccountId", "Transactions");
        migrationBuilder.DropUniqueConstraint("AK_LedgerAccounts_UserId_Id", "LedgerAccounts");
    }
}
