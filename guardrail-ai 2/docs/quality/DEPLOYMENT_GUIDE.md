# Deployment Guide

Target: **Neon** (PostgreSQL) + **Render** (API, Docker) + **Vercel** (SPA) + **PayPal Sandbox**.

## 1. Neon

1. Create project `guardrail` (region close to Render region, e.g. US East).
2. Database `guardrail`; branches `main` (demo) and `dev`.
3. Copy the **pooled** connection string; convert to Npgsql format:
   `Host=<pooler-host>;Database=guardrail;Username=<user>;Password=<pwd>;SSL Mode=Require;Maximum Pool Size=20`
4. Run migrations from local/CI (not on app start):
   `dotnet ef database update --project src/Guardrail.Infrastructure --startup-project src/Guardrail.Api --connection "<direct (non-pooled) connection string>"`
   (Use the direct host for migrations; pooled for the app.)

## 2. PayPal Sandbox

Follow PAYPAL_INTEGRATION.md §9. Register the webhook only after the Render URL exists.

## 3. Render (API)

1. New **Web Service** → repo → Root `backend/` → Runtime **Docker**.
2. `backend/Dockerfile`:
   ```dockerfile
   FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
   WORKDIR /src
   COPY . .
   RUN dotnet publish src/Guardrail.Api/Guardrail.Api.csproj -c Release -o /app /p:UseAppHost=false

   FROM mcr.microsoft.com/dotnet/aspnet:10.0
   WORKDIR /app
   COPY --from=build /app .
   ENV ASPNETCORE_URLS=http://+:${PORT:-8080}
   USER app
   ENTRYPOINT ["dotnet", "Guardrail.Api.dll"]
   ```
   (`USER app` relies on the non-root user in .NET 8+ images **[VERIFY]** for .NET 10 images.)
3. Health check path: `/api/v1/health`.
4. Env vars: ENVIRONMENT_CONFIGURATION.md (backend table).
5. Instance: a paid starter tier is recommended for the demo to avoid free-tier spin-down cold starts during judging **[VERIFY current Render plans]**; otherwise warm it up before demo.
6. Forwarded headers: enable `UseForwardedHeaders` (X-Forwarded-For/Proto) so rate limiting by IP and HTTPS detection work behind Render's proxy.

### 3.1 Render Blueprint (`render.yaml`, ADR-020)

```yaml
services:
  - type: web
    name: guardrail-api
    runtime: docker
    rootDir: backend
    dockerfilePath: ./Dockerfile
    healthCheckPath: /api/v1/health
    envVars:
      - fromGroup: guardrail-secrets
      - key: ASPNETCORE_ENVIRONMENT
        value: Production
      - key: PayPal__BaseUrl
        value: https://api-m.sandbox.paypal.com
      - key: PayPal__Intent
        value: AUTHORIZE
      - key: Llm__Primary__BaseUrl
        value: https://generativelanguage.googleapis.com/v1beta/openai/
      - key: Llm__Fallback__BaseUrl
        value: https://api.groq.com/openai/v1
      - key: Llm__Fallback__Model
        value: openai/gpt-oss-120b
  - type: cron
    name: guardrail-jobs
    runtime: docker            # or a tiny image with curl
    schedule: "*/10 * * * *"
    dockerCommand: >-
      curl -fsS -X POST -H "X-Job-Token: $JOBS_TOKEN"
      https://guardrail-api.onrender.com/api/v1/internal/jobs/run
    envVars:
      - key: JOBS_TOKEN
        fromGroup: guardrail-secrets   # [VERIFY] exact syntax for referencing a group value
envVarGroups:
  - name: guardrail-secrets   # values set in the dashboard, never in git
```
Blueprint field names follow Render's Blueprint spec **[VERIFY against current Render docs before first sync]**. Secrets (`ConnectionStrings__Default`, `Jwt__SigningKey`, `PayPal__ClientSecret`, `PayPal__WebhookId`, `Llm__Primary__ApiKey`, `Llm__Fallback__ApiKey`, `Llm__Primary__Model`, `Jobs__InternalToken`) live only in the env group.

## 4. Vercel (SPA)

1. Import repo → Root `frontend/` → Framework **Vite** → Build `npm run build` → Output `dist`.
2. `vercel.json` with `/api` rewrite to the Render URL (ENVIRONMENT_CONFIGURATION.md).
3. Env: `VITE_API_BASE_URL=/api/v1`, `VITE_PAYPAL_ENV_LABEL=Sandbox`.
4. Set `PayPal__ReturnUrl`/`CancelUrl` on Render to the Vercel production domain.

## 5. Post-deploy smoke test

1. `GET https://<vercel>/api/v1/health` → `{ "status": "Healthy", "db": "Healthy" }`.
2. Register/login works; refresh cookie set on Vercel domain.
3. PayPal smoke: create order via UI, approve with buyer, authorization CREATED, final check PASS, capture COMPLETED.
4. Developer Dashboard → Webhooks events shows delivery 200; DB `paypal_webhook_events.status = PROCESSED`.
5. Agent turn returns cards.

## 5.1 API docs & SDK (APIMatic, ADR-018)

- CI exports `/openapi/v1.json` from the built API, runs `apimatic sdk generate --language=typescript` into `frontend/src/lib/sdk/`, and fails if the committed SDK is stale.
- Publish the API reference (APIMatic portal or static docs) and link it from the README **[VERIFY APIMatic plan/hackathon access]**.

## 6. Demo-day runbook

- T-60 min: hit `/health` to warm Render; run manual Sandbox checklist; check remaining free LLM quota in AI Studio / Groq console (don't burn it rehearsing right before recording).
- T-30 min: `POST /admin/demo/reset` for the demo user; verify policy v1 state.
- Keep a second, pre-recorded video as fallback.
- Have Sandbox buyer credentials in a password manager, not on screen.

## 7. Rollback

- Render: redeploy previous commit from dashboard.
- DB: migrations are additive in MVP; Neon branch restore (point-in-time) for emergencies.
