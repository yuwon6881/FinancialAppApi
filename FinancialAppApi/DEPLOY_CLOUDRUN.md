# Deploying to Google Cloud Run (free tier)

This API is a plain containerized ASP.NET Core app, so Cloud Run runs it as-is —
there is no serverless rewrite. Run every command below from this directory
(`FinancialAppApi/FinancialAppApi/`, the one containing the `Dockerfile`).

## Why it's free at this scale
Cloud Run's perpetual free tier covers ~2M requests, 360k GB-seconds of memory
and 180k vCPU-seconds per month. A single-user app that scales to zero when idle
sits comfortably inside it. The trade-off vs. an always-on server: the first
request after an idle period cold-starts the container (~1–3 s for .NET).

## One-time setup
```bash
# 1. Install the gcloud CLI and log in (run these in your own terminal).
gcloud auth login
gcloud config set project YOUR_PROJECT_ID

# 2. Enable the services Cloud Run source deploys need.
gcloud services enable run.googleapis.com cloudbuild.googleapis.com
```

## Store the database connection string as a secret
The database is **Neon** serverless Postgres. Cloud Run maps the env var
`ConnectionStrings__DefaultConnection` (double underscore) onto the app's
`ConnectionStrings:DefaultConnection` config.

```bash
printf '%s' 'Host=ep-REPLACE.REGION.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=REPLACE;SSL Mode=Require' \
  | gcloud secrets create financialapp-db --data-file=-
```

Store the **direct** endpoint, not the `-pooler` host. At `--max-instances=2` and
`Database:MaxPoolSize=8` the service tops out at 16 connections, comfortably
inside a direct endpoint's budget, and a direct connection is what keeps the
session-scoped advisory locks in `PostgresAdvisoryLock` honest — a transaction
pooler may serve consecutive statements from different backends. `AddPersistence`
strips a `-pooler` label under `--migrate` regardless, so schema maintenance can
never silently run through PgBouncer.

Neon's own `postgresql://…` URI is accepted verbatim, including the libpq-spelled
`channel_binding=require` it ships with. Parameters Npgsql has no equivalent for
are dropped rather than failing startup — see `ConnectionStringNormalizationTests`.

Neon free-tier compute auto-suspends after roughly five minutes idle, so with
`--min-instances=0` a cold request pays both that resume and the Cloud Run cold
start. `Database:Timeout` is therefore set to 15 s in `appsettings.json`,
overriding the 5 s code default that suited an always-on pooler.

## Configure private receipt-image storage

OCR images are held briefly in a private Google Cloud Storage bucket instead of in
Postgres, and are deleted as soon as a scan reaches a terminal state. There is no
storage secret to manage: `GcsReceiptImageStore` authenticates with Application
Default Credentials, so on Cloud Run the runtime service account is the only
identity that can reach the bucket.

This must be a **different bucket from the document vault**. It carries a
one-day lifecycle rule as a safety net for scans abandoned mid-flight; the vault
holds user-retained documents that such a rule would destroy.

```bash
gcloud storage buckets create gs://financialapp-receipts-PROJECT_NUMBER --location=asia-southeast1 --uniform-bucket-level-access --public-access-prevention
```

Grant the Cloud Run runtime service account object admin on that bucket alone —
it needs delete, not just write, because cleanup is part of the scan lifecycle:

```bash
gcloud storage buckets add-iam-policy-binding gs://financialapp-receipts-PROJECT_NUMBER --member=serviceAccount:RUNTIME_SERVICE_ACCOUNT --role=roles/storage.objectAdmin
```

```bash
printf '%s' '{"rule":[{"action":{"type":"Delete"},"condition":{"age":1}}]}' > receipt-lifecycle.json && gcloud storage buckets update gs://financialapp-receipts-PROJECT_NUMBER --lifecycle-file=receipt-lifecycle.json
```

The bucket name is not secret: set it as the Cloud Build substitution
`_RECEIPT_SCAN_BUCKET`, or as the Cloud Run environment variable
`ReceiptScanStorage__Bucket`. Uploads keep the 10 MiB limit and image-only MIME
allowlist, and use `ifGenerationMatch=0` so one job id owns one object path.
Lifecycle deletions are free and objects this short-lived cost a fraction of a
cent per month, but note the GCS always-free allowance is US-regions-only — an
`asia-southeast1` bucket bills from the first byte.

The object-storage migrations add `StorageObjectPath` and remove the old
`ReceiptScanJobs.ImageData` database blob. Any legacy queued scan that still has
only an embedded database image is marked failed during migration so the client
can submit it again instead of leaving an unprocessable job stuck in the queue.

## Deploy
```bash
gcloud run deploy financialapp-api \
  --source . \
  --region asia-southeast1 \
  --allow-unauthenticated \
  --port 8080 \
  --min-instances 0 \
  --max-instances 2 \
  --memory 512Mi \
  --set-env-vars "ReceiptScanStorage__Bucket=financialapp-receipts-PROJECT_NUMBER" \
  --set-secrets "ConnectionStrings__DefaultConnection=financialapp-db:latest,MarketData__Providers__TwelveData__ApiKey=financialapp-twelvedata-api-key:latest,OpenAiApiKey=financialapp-openai-api-key:latest"
```

- `--source .` builds the image from the `Dockerfile` via Cloud Build and deploys it.
- `--min-instances 0` = scale to zero = pay nothing while idle (accepts cold starts).
  Set it to `1` for an always-warm instance (leaves the free tier, ~a few $/mo).
- `--allow-unauthenticated` is required — this is a public API guarded by its own
  bearer-token auth, not Google IAM.

Run the checked-in Cloud Build pipeline from the repository root. Manual submissions
do not receive `COMMIT_SHA` from a repository trigger, so pass the current commit
explicitly along with the non-secret bucket:

```bash
commit_sha="$(git rev-parse HEAD)"
gcloud builds submit . \
  --config cloudbuild.yaml \
  --substitutions "_RECEIPT_SCAN_BUCKET=financialapp-receipts-PROJECT_NUMBER,COMMIT_SHA=${commit_sha}"
```

## Configure OpenAI

The AI assistant, category/note suggestions, vault cleanup suggestions and receipt
OCR all use the OpenAI Responses API. The default model is `gpt-5.4-mini`; override
`OpenAiModel` or a feature-specific `OpenAiModels__*` environment variable only
after validating that workload. Store the API key only in Secret Manager:

```bash
printf '%s' 'sk-REPLACE_ME' \
  | gcloud secrets create financialapp-openai-api-key --data-file=-

gcloud secrets add-iam-policy-binding financialapp-openai-api-key \
  --member="serviceAccount:YOUR_CLOUD_RUN_RUNTIME_SA@YOUR_PROJECT_ID.iam.gserviceaccount.com" \
  --role="roles/secretmanager.secretAccessor"
```

The checked-in deployment maps this secret to
`OpenAiApiKey=financialapp-openai-api-key:latest`. After deploying and verifying an
AI request, remove the obsolete `AiApiKey` Cloud Run mapping before deleting its
old Secret Manager secret.

## Configure Growth Investments market data

Twelve Data is used only after an explicit symbol search or **Update prices**
action. Store the shared provider key in Secret Manager and grant the Cloud Run
runtime identity access; never put the value in `appsettings.json`, a URL, or
Cloud Build substitutions.

```bash
printf '%s' 'YOUR_TWELVE_DATA_KEY' \
  | gcloud secrets create financialapp-twelvedata-api-key --data-file=-

gcloud secrets add-iam-policy-binding financialapp-twelvedata-api-key \
  --member="serviceAccount:YOUR_CLOUD_RUN_RUNTIME_SA@YOUR_PROJECT_ID.iam.gserviceaccount.com" \
  --role="roles/secretmanager.secretAccessor"
```

The deployment maps the secret to
`MarketData__Providers__TwelveData__ApiKey=financialapp-twelvedata-api-key:latest`.
Non-secret defaults live under `MarketData` in `appsettings.json`: provider base
URL, 15-minute freshness, six refresh calls per minute (leaving two discovery
calls available), a 750-call daily ceiling, and feature enablement. With no key,
manual accounts, instruments, transactions, and prices continue to work.

## Retiring Supabase

Supabase previously provided both the Postgres database and the receipt-image
bucket. The deploy config removes the retired keys explicitly
(`--remove-env-vars=SupabaseStorage__ProjectUrl`,
`--remove-secrets=…,SupabaseStorage__ApiKey`), because `--update-env-vars` and
`--update-secrets` only add or overwrite: without that, a stale URL and API key
stay bound to the revision long after the code stops reading them.

Tear the account down only after the new stack is proven, in this order:

1. Confirm the Neon cutover: the app serves reads and writes, and
   `dotnet ef database update` against Neon reports nothing pending.
2. Confirm a receipt can be uploaded, retrieved, and cleaned up on GCS.
3. Destroy the `financialapp-supabase-api-key` secret versions, then the
   container.
4. Delete the Supabase project last — it is the rollback target until both
   checks above have passed.

## Baseline an existing database before first deploy
This app now uses EF Core migrations instead of the old startup `DbInitializer`.
If the target Postgres database already has the current schema, mark the baseline
migration as applied before the first Cloud Run boot:

```sql
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" varchar(150) NOT NULL,
    "ProductVersion" varchar(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260707120000_InitialCreate', '10.0.8')
ON CONFLICT DO NOTHING;
```

Back up or restore into a scratch database first. If you point this app at an
empty Postgres database, leave the history table alone and EF will create the
schema on startup.

## After first deploy
1. Cloud Run prints a service URL (`https://financialapp-api-*.run.app`).
2. Point the Vercel frontend's API base URL at it.
3. Add that Cloud Run URL (and your Vercel origin) to `WebAuthn:AllowedOrigins`
   in `appsettings.json` if the RP origin ever needs to differ from the request
   origin. Fingerprint auth requires the origin to match exactly.
4. Health check: `curl https://<service-url>/api/ping`.

## Capacitor app origins and Android passkeys

The Capacitor Android WebView runs at `https://localhost`; the iOS WebView runs at
`capacitor://localhost`. Both exact origins are included in `Cors:AllowedOrigins`
for authenticated API calls. `WebAuthn:AllowedOrigins` continues to contain the
production website origin. Android Credential Manager supplies an additional
origin derived from the app signing certificate, which the API adds only when its
exact SHA-256 fingerprint is configured. Keep separate values for the Play app
signing certificate and the dedicated GitHub Actions device-test certificate:

```bash
gcloud run services update financialapp-api \
  --region=asia-southeast1 \
  --update-env-vars="WebAuthn__AndroidSigningCertificateSha256=PLAY_APP_SIGNING_CERT_SHA256,WebAuthn__AndroidDebugSigningCertificateSha256=DEVICE_TEST_SIGNING_CERT_SHA256"
```

Use the **Play App Signing** fingerprint for the release APK and the dedicated
GitHub Actions debug signing certificate fingerprint for device-test APKs. Set
the corresponding `ANDROID_APP_SIGNING_CERT_SHA256` and
`ANDROID_DEBUG_SIGNING_CERT_SHA256` Vercel build variables; both are published in
`/.well-known/assetlinks.json` for `com.financialapp.app`. Keep the four debug
keystore values (`ANDROID_DEBUG_KEYSTORE_BASE64`, `ANDROID_DEBUG_KEY_ALIAS`,
`ANDROID_DEBUG_KEYSTORE_PASSWORD`, and `ANDROID_DEBUG_KEY_PASSWORD`) in GitHub
Actions secrets. Set the same device-test fingerprint as the GitHub Actions
repository variable `ANDROID_DEBUG_SIGNING_CERT_SHA256`. Verify the file returns
HTTP 200 with `Content-Type: application/json` and no redirect before enabling
native passkey enrollment. Do not add wildcard Android origins to
`WebAuthn:AllowedOrigins`.

For Android push, register package `com.financialapp.app` in the Firebase project
configured by `Fcm:ProjectId`, ship its matching `google-services.json`, and keep
the Cloud Run runtime service identity's FCM send role from the dispatcher setup
below. The Android app registers its FCM token with the device subscription API;
do not send an APNs token to that endpoint. iOS obtains an FCM registration token
through the Capacitor FCM plugin after APNs and Firebase are configured.

## Notes carried over from the migration
- The background session sweeper was removed; expired rows are pruned lazily on
  auth activity and live sessions are bounded per device, so scale-to-zero is safe.
- EF Core migrations run on startup by default via `Database:MigrateOnStartup`.
  For stricter production deploys, set that value to `false` and run
  `dotnet ef database update` or an idempotent migration script as a deploy step.

## Push notification dispatcher (Cloud Scheduler -> `POST /api/push/dispatch`)

`POST /api/push/dispatch` is the daily fan-out job for recurring-payment push
reminders. It carries no user session — it is guarded entirely by
`AuthorizeGoogleOidc`, which requires a Google-signed OIDC identity token
whose issuer, configured audience, and service-account email are all
independently verified, and whose email must be on an explicit allowlist. The
dispatcher (`PushDispatchService`) itself fails closed: with no `Fcm:ProjectId`
configured it returns immediately without claiming a single reminder, and with
no `Push:OidcAudience`/`Push:AllowlistedServiceAccounts` configured the filter
above rejects every request before the dispatcher ever runs.

### Configuration
Three config sections gate this feature end-to-end; leaving any of them empty
disables the corresponding layer instead of degrading insecurely:

```bash
--update-env-vars="Push__OidcAudience=https://financialapp-api-i47taxhzba-as.a.run.app/api/push/dispatch"
--update-env-vars="Push__AllowlistedServiceAccounts__0=financialapp-scheduler@YOUR_PROJECT_ID.iam.gserviceaccount.com"
--update-env-vars="Fcm__ProjectId=YOUR_FIREBASE_PROJECT_ID"
```

- `Push:OidcAudience` — must exactly match the `--oidc-token-audience` the
  Scheduler job is created with (below).
- `Push:AllowlistedServiceAccounts` — the *only* service account emails the
  filter will accept an identity token from, even if the token is otherwise
  perfectly valid and Google-signed.
- `Fcm:ProjectId` — the Firebase project the FCM HTTP v1 API sends through.

### Grant FCM send permission via the Cloud Run service identity (no key file)
The sender (`FcmHttpV1PushSender`) uses Application Default Credentials — the
identity Cloud Run already attaches to the container — so no service-account
JSON key is ever generated, stored, or shipped:

```bash
gcloud projects add-iam-policy-binding YOUR_FIREBASE_PROJECT_ID \
  --member="serviceAccount:YOUR_CLOUD_RUN_RUNTIME_SA@YOUR_PROJECT_ID.iam.gserviceaccount.com" \
  --role="roles/firebasecloudmessaging.admin"
```

### Create the dedicated scheduler service account (allowlisted, no other privileges)
```bash
gcloud iam service-accounts create financialapp-scheduler \
  --display-name="Cloud Scheduler caller for push dispatch"

gcloud run services add-iam-policy-binding financialapp-api \
  --region=asia-southeast1 \
  --member="serviceAccount:financialapp-scheduler@YOUR_PROJECT_ID.iam.gserviceaccount.com" \
  --role="roles/run.invoker"
```
Add that exact email to `Push:AllowlistedServiceAccounts` above — this account
has no role except invoking this one Cloud Run service.

### Rollout order (do not skip the manual validation step)
1. Deploy the app with the three config sections above set, but **do not
   create the Cloud Scheduler job yet**.
2. Grant the FCM and `run.invoker` IAM bindings above.
3. Mint a short-lived identity token for the scheduler service account
   yourself and call the endpoint manually to validate the full path
   end-to-end (auth, recurrence matching, FCM send, token disabling) before
   anything runs unattended:
   ```bash
   TOKEN=$(gcloud auth print-identity-token \
     --impersonate-service-account=financialapp-scheduler@YOUR_PROJECT_ID.iam.gserviceaccount.com \
     --audiences="https://financialapp-api-i47taxhzba-as.a.run.app/api/push/dispatch")
   curl -X POST -H "Authorization: Bearer $TOKEN" \
     https://financialapp-api-i47taxhzba-as.a.run.app/api/push/dispatch
   ```
   Confirm the `{sent, skipped, disabled}` response looks right and that a
   real device actually receives a reminder before proceeding.
4. Only after that manual call succeeds, create the Cloud Scheduler job
   (below). Keep it **paused** immediately after creation if you want one
   more supervised run before it goes fully unattended:
   ```bash
   gcloud scheduler jobs create http financialapp-push-dispatch \
     --location=asia-southeast1 \
     --schedule="0 9 * * *" \
     --time-zone="Asia/Kuala_Lumpur" \
     --uri="https://financialapp-api-i47taxhzba-as.a.run.app/api/push/dispatch" \
     --http-method=POST \
     --oidc-service-account-email="financialapp-scheduler@YOUR_PROJECT_ID.iam.gserviceaccount.com" \
     --oidc-token-audience="https://financialapp-api-i47taxhzba-as.a.run.app/api/push/dispatch" \
     --max-retry-attempts=3 \
     --max-retry-duration=3600s \
     --min-backoff=60s \
     --max-backoff=600s
   ```
   One job, one cron (`0 9 * * *`, `Asia/Kuala_Lumpur`) — the reminder TTL
   already ends at the close of that same local day, so retries are bounded
   to the same day by design; `--max-retry-duration=3600s` just keeps
   Scheduler itself from retrying into the next one.

### Cost stays inside the free tier
- Cloud Run already runs with `--min-instances 0 --max-instances 2`, so this
  adds one extra request per day, billed the same request-based way as any
  other endpoint — no dedicated always-on worker.
- Cloud Scheduler's free tier covers 3 jobs/month; this uses exactly one.
- FCM sends are free; no paid add-on is introduced anywhere in this path.

### Monitoring without leaking sensitive data
- The dispatcher and sender never log an FCM token, a payment amount, or any
  other financial detail — only outcomes (`Sent` / `InvalidOrUnregistered` /
  `TransientFailure`) and non-sensitive identifiers (payment/occurrence IDs).
- Set a Cloud Billing budget alert as an early-warning notification (e.g. at
  50/90/100% of a small monthly threshold) — this is a **warning, not a
  spending cap**; Cloud Billing budgets cannot themselves stop billing or
  disable a project.
- Watch Cloud Run request logs and Cloud Scheduler job history for the daily
  `09:00 Asia/Kuala_Lumpur` run; a failed OIDC check shows up as `401`/`403`
  on that endpoint without any payload detail attached.
