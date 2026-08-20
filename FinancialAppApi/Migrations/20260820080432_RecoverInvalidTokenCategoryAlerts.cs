using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class RecoverInvalidTokenCategoryAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The old invalid-token path left a delivery claim and completed the event even though
            // FCM proved that no device received it. Reopen only still-live events whose every
            // claim has the exact old-path signature: disabled/empty token, with no UpdatedAt bump.
            migrationBuilder.Sql(
                """
                WITH recovered AS (
                    UPDATE "CategoryLimitAlertEvents" AS event
                    SET "AwaitingDeviceRecovery" = TRUE,
                        "CompletedAt" = NULL
                    WHERE event."CompletedAt" IS NOT NULL
                      AND event."ExpiresAt" > NOW()
                      AND EXISTS (
                          SELECT 1
                          FROM "CategoryLimitAlertDeliveries" AS delivery
                          INNER JOIN "PushSubscriptions" AS subscription
                              ON subscription."Id" = delivery."SubscriptionId"
                             AND subscription."UserId" = delivery."UserId"
                          WHERE delivery."EventId" = event."Id"
                            AND delivery."UserId" = event."UserId"
                            AND NOT subscription."Enabled"
                            AND subscription."FcmToken" = ''
                            AND subscription."UpdatedAt" <= event."CreatedAt"
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM "CategoryLimitAlertDeliveries" AS delivery
                          INNER JOIN "PushSubscriptions" AS subscription
                              ON subscription."Id" = delivery."SubscriptionId"
                             AND subscription."UserId" = delivery."UserId"
                          WHERE delivery."EventId" = event."Id"
                            AND delivery."UserId" = event."UserId"
                            AND (
                                subscription."Enabled"
                                OR subscription."FcmToken" <> ''
                                OR subscription."UpdatedAt" > event."CreatedAt"
                            )
                      )
                    RETURNING event."Id", event."UserId"
                )
                DELETE FROM "CategoryLimitAlertDeliveries" AS delivery
                USING recovered
                WHERE delivery."EventId" = recovered."Id"
                  AND delivery."UserId" = recovered."UserId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only recovery is intentionally irreversible: a successfully retried alert must
            // never be put back into the false "completed without delivery" state.
        }
    }
}
