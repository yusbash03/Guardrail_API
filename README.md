# Guardrail AI — API

> **Your money. Your rules. Your AI.**

Guardrail AI lets you hand your shopping to an AI agent without handing over your wallet. The agent finds products and builds carts. A deterministic **Spending Policy Engine** decides whether each cart is allowed. **You** approve the exact cart. Then the backend runs **PayPal** checkout and records every dollar in an append-only ledger.

Built for **Build What's Next with PayPal and AI** (PayPal AI Hackathon, 2026). **PayPal Sandbox only: no real money moves.**

This repository is the backend API. The web app lives in [Guardrail-UI](https://github.com/yusbash03/Guardrail-UI).

## How it works

```
You ──"get my groceries"──▶ AI agent ──tools──▶ catalogue / carts
                                  │
                                  ▼
                      Spending Policy Engine  →  ALLOWED | REQUIRES_APPROVAL | BLOCKED
                                  │
                                  ▼
                       You approve the exact cart with your password (hash-bound, single-use, expiring)
                                  │
                                  ▼
                         PayPal order → buyer approves → authorize → final guardrail check → capture (or void)
                                  │
                                  ▼
                          Spending ledger  →  proposed → reserved → authorized → captured
```

### What the AI can't do

The agent has exactly 11 tools: search, budgets, carts, evaluation, and requesting your approval. It **cannot** approve purchases, pay, capture, change your rules, approve merchants or write to the ledger. Those tools don't exist, and an architecture test fails the build if any tool can reach that code. On top of that:

- Tool arguments are validated strictly. A tool call that tries to set a `price` is rejected.
- Product descriptions and other merchant data are passed to the model as untrusted data.
- If a reply claims something was approved or paid and no tool result backs it up, the server adds a note saying the status card is authoritative.

### Money safety

- **Final guardrail check.** Payments are authorized first. The rules are checked again just before capture, and the authorization is voided if they now fail.
- **No overspending.** Budgets are reserved in the ledger with row locks, so two purchases at once can't overspend a budget.
- **Idempotency.** Every money-moving endpoint takes an `Idempotency-Key`, and every PayPal call carries a `PayPal-Request-Id`.
- **Backstops.** PayPal webhooks are verified with PayPal before use. A reconciler job catches anything a webhook or the browser missed.

## Tech stack

| Layer | Tech |
|---|---|
| API | C#, ASP.NET Core (.NET 10), minimal APIs, Clean Architecture modular monolith |
| Data | PostgreSQL 16, EF Core (Npgsql), UUID v7 keys, optimistic concurrency |
| AI | Google Gemini (`gemini-3.5-flash-lite`) primary, Groq (`openai/gpt-oss-120b`) fallback, both via OpenAI-compatible tool calling |
| Payments | PayPal Orders v2 (intent `AUTHORIZE`), Payments v2 capture/void, verified webhooks (Sandbox) |
| Tests | xUnit, Shouldly, Testcontainers (real Postgres), NetArchTest |
| Hosting | Render (Docker web service, via Blueprint), Neon (PostgreSQL) |

## Repository layout

```
guardrail-ai 2/
├── backend/
│   ├── src/
│   │   ├── Guardrail.Domain/          # entities, policy engine, ledger rules (no dependencies)
│   │   ├── Guardrail.Application/     # use cases, AI agent + tools, ports
│   │   ├── Guardrail.Infrastructure/  # EF Core, PayPal, LLM clients, background jobs
│   │   └── Guardrail.Api/             # endpoints, auth, rate limiting, ProblemDetails
│   ├── tests/                         # domain, architecture and integration tests
│   ├── Dockerfile
│   └── docker-compose.yml             # local PostgreSQL
├── docs/                              # product, architecture, domain, AI, payments and API specs
└── render.yaml                        # Render Blueprint
```

## Running locally

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Docker.

```bash
cd "guardrail-ai 2/backend"
docker compose up -d                                   # local PostgreSQL 16

# Secrets go in user-secrets, never in appsettings
dotnet user-secrets --project src/Guardrail.Api set "Jwt:SigningKey" "$(openssl rand -base64 64 | tr -d '\n')"
dotnet user-secrets --project src/Guardrail.Api set "Seed:DemoUserPassword" "<demo password>"
dotnet user-secrets --project src/Guardrail.Api set "PayPal:ClientId" "<sandbox client id>"
dotnet user-secrets --project src/Guardrail.Api set "PayPal:ClientSecret" "<sandbox secret>"
dotnet user-secrets --project src/Guardrail.Api set "Llm:Primary:ApiKey" "<Google AI Studio key>"
dotnet user-secrets --project src/Guardrail.Api set "Llm:Fallback:ApiKey" "<Groq key>"   # optional

dotnet run --project src/Guardrail.Api
```

In Development, the API creates the database tables and seeds demo data on startup, and the demo user is `demo@guardrail.ai`. Interactive API docs are at http://localhost:5080/scalar (Scalar) and http://localhost:5080/swagger. To call protected endpoints, log in with `POST /api/v1/auth/login` and paste the `accessToken` as the Bearer token. The docs are off in Production unless `ApiDocs__Enabled=true`; the Render Blueprint turns them on for the demo. Without PayPal keys the app still runs, but checkout returns `503 PAYMENT_PROVIDER_UNAVAILABLE`.

The PayPal **Business** sandbox account linked to your app must be in a country that can receive payments, such as the US. You pay with a **Personal** sandbox account.

## Tests

```bash
cd "guardrail-ai 2/backend"
dotnet test        # needs Docker: integration tests start a throwaway PostgreSQL
```

There are 135 tests:

- **Domain:** 66 tests of the policy engine, including every worked example in the spec, plus hashing, ledger and state machines.
- **Architecture:** 7 tests of layering, plus a check that no AI tool can reach payment, approval or rule-changing code.
- **Integration:** 62 tests that run the full API against real PostgreSQL. They cover auth, purchase isolation between users, approvals, PayPal flows using fakes, webhooks, concurrency and the agent.

## Deploying to Render

[`render.yaml`](guardrail-ai%202/render.yaml) defines the API as a free Docker web service. The database is [Neon](https://neon.tech) PostgreSQL.

1. **Migrate the database.** Run migrations against Neon's **direct** endpoint, which is the host without `-pooler`, because migrations take a lock that the pooled endpoint doesn't handle reliably:
   ```bash
   cd "guardrail-ai 2/backend"
   ConnectionStrings__Default='postgresql://<user>:<password>@ep-xxx.<region>.aws.neon.tech/neondb?sslmode=require' \
     dotnet ef database update --project src/Guardrail.Infrastructure --startup-project src/Guardrail.Infrastructure
   ```
2. In Render, create a **New → Blueprint** from this repo, with the blueprint path `guardrail-ai 2/render.yaml`.
3. Add the secrets to the `guardrail-secrets` env group. [Environment Configuration](guardrail-ai%202/docs/api/ENVIRONMENT_CONFIGURATION.md) lists every variable. The group holds:
   - `ConnectionStrings__Default`: Neon's **pooled** URL;
   - the JWT signing key;
   - the PayPal and LLM keys;
   - the jobs token;
   - the PayPal webhook ID.
4. After the first deploy, register `https://<your-app>.onrender.com/api/v1/webhooks/paypal` as a webhook on your PayPal sandbox app. Then set `PayPal__WebhookId` to the ID PayPal gives you.

The app accepts the connection string either as a `postgresql://` URL (as Neon and Render provide it, including `sslmode` and `channel_binding`) or in Npgsql's `Host=…;Username=…` form.

## Documentation

The full specs are in [`guardrail-ai 2/docs`](guardrail-ai%202/docs), and [`guardrail-ai 2/README.md`](guardrail-ai%202/README.md) maps them out. Highlights:

- [Spending Policy Engine](guardrail-ai%202/docs/domain/SPENDING_POLICY_ENGINE.md)
- [AI Guardrails](guardrail-ai%202/docs/ai/AI_GUARDRAILS.md)
- [PayPal Integration](guardrail-ai%202/docs/payments/PAYPAL_INTEGRATION.md)
- [API Contract](guardrail-ai%202/docs/api/API_CONTRACT.md)

## Honesty notes

- Sandbox only. The merchants and products are fictional seed data.
- The buyer approves every PayPal order in PayPal. Guardrail never accesses a PayPal account on its own.

## License

[MIT](LICENSE) © 2026 Yusuf Bashir
