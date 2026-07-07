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
Reuse the **same Postgres connection string you already use on Render** — it's
known-good. Cloud Run maps the env var `ConnectionStrings__DefaultConnection`
(double underscore) onto the app's `ConnectionStrings:DefaultConnection` config.

```bash
# Paste your Npgsql connection string when prompted (no trailing newline).
printf '%s' 'Host=...;Port=...;Database=postgres;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true' \
  | gcloud secrets create financialapp-db --data-file=-
```

> Supabase note: on Cloud Run, prefer the **pooler** host (`...pooler.supabase.com`,
> IPv4) over the direct `db.<ref>.supabase.co` host (IPv6-only without the paid
> add-on — Cloud Run can't reach it without a VPC connector). The transaction
> pooler (port `6543`) is the serverless-friendly choice.

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
  --set-secrets "ConnectionStrings__DefaultConnection=financialapp-db:latest"
```

- `--source .` builds the image from the `Dockerfile` via Cloud Build and deploys it.
- `--min-instances 0` = scale to zero = pay nothing while idle (accepts cold starts).
  Set it to `1` for an always-warm instance (leaves the free tier, ~a few $/mo).
- `--allow-unauthenticated` is required — this is a public API guarded by its own
  bearer-token auth, not Google IAM.

## After first deploy
1. Cloud Run prints a service URL (`https://financialapp-api-*.run.app`).
2. Point the Vercel frontend's API base URL at it.
3. Add that Cloud Run URL (and your Vercel origin) to `WebAuthn:AllowedOrigins`
   in `appsettings.json` if the RP origin ever needs to differ from the request
   origin. Fingerprint auth requires the origin to match exactly.
4. Health check: `curl https://<service-url>/api/ping`.

## Notes carried over from the migration
- The background session sweeper was removed; expired rows are pruned lazily on
  auth activity and live sessions are bounded per device, so scale-to-zero is safe.
- `DbInitializer` still runs its idempotent schema checks on every cold start.
  It's safe but does a handful of DB round-trips per boot; migrating it to EF Core
  migrations later would trim cold-start latency.
