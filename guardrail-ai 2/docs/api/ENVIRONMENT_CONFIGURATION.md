# Environment Configuration

Secrets are **only** environment variables (Render/Vercel dashboards, local `dotnet user-secrets` / `.env.local`). Nothing secret in `appsettings*.json` or the repo. .NET maps `Section__Key` env vars to `Section:Key`.

## Backend (Render)

| Variable | Required | Secret | Example / default |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | ✅ | | `Production` (Render), `Development` (local), `Demo` (optional) |
| `ConnectionStrings__Default` | ✅ | ✅ | `Host=ep-xxx-pooler.neon.tech;Database=guardrail;Username=…;Password=…;SSL Mode=Require` |
| `Jwt__Issuer` | ✅ | | `https://api.guardrail.example` |
| `Jwt__Audience` | ✅ | | `guardrail-web` |
| `Jwt__SigningKey` | ✅ | ✅ | ≥ 64 random bytes, base64 (HS256) |
| `Jwt__AccessTokenMinutes` | | | `15` |
| `Jwt__RefreshTokenDays` | | | `7` |
| `Jwt__RefreshReuseGraceSeconds` | | | `30` (seconds a rotated refresh token is still accepted as a retry; `0` = strict) |
| `PayPal__BaseUrl` | ✅ | | `https://api-m.sandbox.paypal.com` |
| `PayPal__ClientId` | ✅ | ✅ | sandbox app client id |
| `PayPal__ClientSecret` | ✅ | ✅ | sandbox app secret |
| `PayPal__WebhookId` | ✅ | ✅ | from Developer Dashboard |
| `PayPal__Intent` | | | `AUTHORIZE` (fallback `CAPTURE`) |
| `PayPal__BrandName` | | | `Guardrail AI` |
| `PayPal__ReturnUrl` | ✅ | | `https://guardrail.vercel.app/checkout/return` |
| `PayPal__CancelUrl` | ✅ | | `https://guardrail.vercel.app/checkout/cancel` |
| `Llm__Primary__Provider` | ✅ | | `Gemini` |
| `Llm__Primary__BaseUrl` | ✅ | | `https://generativelanguage.googleapis.com/v1beta/openai/` |
| `Llm__Primary__Model` | ✅ | | current free Gemini Flash id from AI Studio **[VERIFY]** |
| `Llm__Primary__ApiKey` | ✅ | ✅ | Google AI Studio API key (free, no card) |
| `Llm__Primary__RequestsPerMinute` | | | `8` |
| `Llm__Fallback__Provider` | | | `Groq` |
| `Llm__Fallback__BaseUrl` | | | `https://api.groq.com/openai/v1` |
| `Llm__Fallback__Model` | | | `openai/gpt-oss-120b` |
| `Llm__Fallback__ApiKey` | | ✅ | Groq API key (free, no card) |
| `Llm__Fallback__RequestsPerMinute` | | | `25` |
| `Llm__TimeoutSeconds` | | | `30` |
| `Agent__MaxLlmRequestsPerTurn` | | | `6` |
| `Agent__MaxToolCallsPerTurn` | | | `16` |
| `Agent__TurnTimeoutSeconds` | | | `100` (must stay under Vercel's 120 s proxy limit) |
| `Approvals__TtlMinutes` | | | `30` |
| `Approvals__StepUpMaxFailures` | | | `5` (per 15 min) |
| `Checkout__AuthorizationMaxOpenMinutes` | | | `30` |
| `Channel3__Enabled` | | | `false` (ADR-019, P2) |
| `Channel3__ApiKey` | | ✅ | only if enabled |
| `Evaluations__MaxAgeMinutes` | | | `15` |
| `Checkout__PayPalApprovalTimeoutHours` | | | `3` |
| `Cors__AllowedOrigins` | | | `http://localhost:5173` (dev only; prod is same-origin via Vercel rewrite) |
| `Jobs__InternalToken` | ✅ | ✅ | ≥ 32 random bytes; shared with the Render Cron Job |
| `Seed__Catalogue` | | | `true` |
| `Seed__CreateDemoUser` | | | `true` on demo env |
| `Demo__ResetEnabled` | | | `false` (true only on demo env) |
| `Serilog__MinimumLevel__Default` | | | `Information` |

### Startup validation
`IValidateOptions` for each section; app refuses to start if required values are missing, `Jwt__SigningKey` < 64 bytes, or `PayPal__BaseUrl` is not the sandbox host.

## Frontend (Vercel)

| Variable | Example | Notes |
|---|---|---|
| `VITE_API_BASE_URL` | `/api/v1` | same-origin via rewrite; `http://localhost:5080/api/v1` locally |
| `VITE_APP_ENV` | `production` | |
| `VITE_PAYPAL_ENV_LABEL` | `Sandbox` | shown in UI badge |

No secrets in `VITE_*` (they are public in the bundle).

`vercel.json`:
```json
{
  "rewrites": [
    { "source": "/api/v1/internal/:path*", "destination": "/404" },
    { "source": "/api/:path*", "destination": "https://guardrail-api.onrender.com/api/:path*" },
    { "source": "/((?!api/).*)", "destination": "/index.html" }
  ],
  "headers": [
    { "source": "/(.*)", "headers": [
      { "key": "X-Content-Type-Options", "value": "nosniff" },
      { "key": "Referrer-Policy", "value": "strict-origin-when-cross-origin" },
      { "key": "X-Frame-Options", "value": "DENY" },
      { "key": "Permissions-Policy", "value": "camera=(), microphone=(), geolocation=()" }
    ] }
  ]
}
```

## Local development

- Backend: `dotnet user-secrets set "PayPal:ClientSecret" "…"` etc.; PostgreSQL via `docker run postgres:16` or a Neon dev branch.
- Frontend: `.env.local` (git-ignored).
- Webhooks locally: tunnel + separate Sandbox webhook id (PAYPAL_WEBHOOK_CONTRACT.md).

## Environments

| Env | Backend | Frontend | DB | PayPal |
|---|---|---|---|---|
| Local | `dotnet run` | `vite` | local PG / Neon branch `dev` | Sandbox app (dev) |
| CI | Testcontainers | vitest | ephemeral | fakes |
| Demo/Prod | Render | Vercel | Neon `main` | Sandbox app (demo) |
