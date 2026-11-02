# Guardrail AI

> **Your money. Your rules. Your AI.**

Guardrail AI lets you delegate shopping to an AI agent while every purchase stays inside spending rules you control. The AI proposes; a deterministic **Spending Policy Engine** decides; **you** approve the exact cart; the backend runs **PayPal** checkout and records every dollar in a ledger.

Built for **Build What's Next with PayPal and AI** (PayPal AI Hackathon, 2026). **PayPal Sandbox only — no real money moves.**

## Status

🛠️ **Backend API implemented** (`backend/`): domain + policy engine, auth, policies/merchants, carts, evaluation, purchases, approvals (password step-up), PayPal checkout (authorize → final check → capture/void), webhooks, ledger, dashboard, AI agent (11 tools, Gemini → Groq), background jobs. Not yet verified against the real PayPal Sandbox or a live LLM (Day-1 spike items still marked [VERIFY]). Frontend not started. See [`docs/quality/IMPLEMENTATION_ROADMAP.md`](docs/quality/IMPLEMENTATION_ROADMAP.md).

## How it works

```
You ──"get my groceries"──▶ AI Agent ──tools──▶ Catalogue / Carts
                                   │
                                   ▼
                       Spending Policy Engine  →  ALLOWED | REQUIRES_APPROVAL | BLOCKED
                                   │
                                   ▼
                        Authorization Service  →  you approve the exact cart with your password (hash-bound, single-use, expiring)
                                   │
                                   ▼
                          Payment Service  →  PayPal order → buyer approves → authorize → final guardrail check → capture (or void)
                                   │
                                   ▼
                           Spending Ledger  →  proposed → reserved → authorized → captured
```

The AI **cannot** approve, pay, capture, change rules, approve merchants or write the ledger — those tools don't exist, and it has no PayPal toolkit or MCP connection.

## Stack

| Layer | Tech |
|---|---|
| Backend | C#, ASP.NET Core (.NET 10 LTS), EF Core, PostgreSQL, FluentValidation, JWT, OpenAPI, xUnit |
| Frontend | React, TypeScript, Vite, PWA, Tailwind CSS, shadcn/ui, TanStack Query, React Hook Form, Zod, Recharts, Lucide, AG Grid Community |
| AI | **Free tier:** Google Gemini API (Gemini Flash, tool calling via OpenAI-compatible endpoint); Groq `openai/gpt-oss-120b` fallback |
| Payments | PayPal Orders v2 (intent `AUTHORIZE`) + Payments v2 capture/void, verified webhooks — Sandbox |
| Infra | Render (API, Docker, Blueprint + Cron), Vercel (SPA), Neon (PostgreSQL) |
| Sponsor tech | AG Grid Community (tables), APIMatic (TypeScript SDK + API reference), Render; Channel3 optional (P2) |

## Documentation map

| Area | Documents |
|---|---|
| Product | [Vision](docs/product/PRODUCT_VISION.md) · [Requirements](docs/product/PRODUCT_REQUIREMENTS.md) · [Personas](docs/product/USER_PERSONAS.md) · [Journeys](docs/product/USER_JOURNEYS.md) · [MVP Scope](docs/product/MVP_SCOPE.md) |
| Architecture | [Architecture](docs/architecture/ARCHITECTURE.md) · [ADRs](docs/architecture/ARCHITECTURE_DECISIONS.md) · [Project Structure](docs/architecture/PROJECT_STRUCTURE.md) · [Coding Standards](docs/architecture/CODING_STANDARDS.md) |
| Domain | [Domain Model](docs/domain/DOMAIN_MODEL.md) · [Database Schema](docs/domain/DATABASE_SCHEMA.md) · [Business Rules](docs/domain/BUSINESS_RULES.md) · [Policy Engine](docs/domain/SPENDING_POLICY_ENGINE.md) · [Ledger](docs/domain/SPENDING_LEDGER.md) |
| AI | [Agent Spec](docs/ai/AI_AGENT_SPECIFICATION.md) · [Tool Contracts](docs/ai/AI_TOOL_CONTRACTS.md) · [Guardrails](docs/ai/AI_GUARDRAILS.md) |
| Payments | [PayPal Integration](docs/payments/PAYPAL_INTEGRATION.md) · [Webhook Contract](docs/payments/PAYPAL_WEBHOOK_CONTRACT.md) · [State Machine](docs/payments/PAYMENT_STATE_MACHINE.md) |
| Authorization | [Approval Workflow](docs/authorization/APPROVAL_WORKFLOW.md) · [Purchase Authorization](docs/authorization/PURCHASE_AUTHORIZATION.md) · [Evaluation Contract](docs/authorization/POLICY_EVALUATION_CONTRACT.md) |
| API | [API Contract](docs/api/API_CONTRACT.md) · [Errors](docs/api/ERROR_HANDLING.md) · [Environment](docs/api/ENVIRONMENT_CONFIGURATION.md) |
| Frontend | [Frontend Spec](docs/frontend/FRONTEND_SPECIFICATION.md) · [Design System](docs/frontend/DESIGN_SYSTEM.md) · [PWA](docs/frontend/PWA_REQUIREMENTS.md) |
| Quality | [Security](docs/quality/SECURITY_REQUIREMENTS.md) · [Testing](docs/quality/TESTING_STRATEGY.md) · [Deployment](docs/quality/DEPLOYMENT_GUIDE.md) · [Demo](docs/quality/DEMO_SCENARIO.md) · [Roadmap](docs/quality/IMPLEMENTATION_ROADMAP.md) · [Submission](docs/quality/HACKATHON_SUBMISSION.md) |

**Source-of-truth order** when documents disagree: DOMAIN_MODEL (names/enums) → BUSINESS_RULES → SPENDING_POLICY_ENGINE / SPENDING_LEDGER → contracts (API, AI tools, evaluation, PayPal) → everything else.

## Getting started

```bash
# backend
cd backend
docker compose up -d                       # local PostgreSQL 16
# secrets (never in appsettings): 64-byte base64 JWT key, jobs token, demo password, PayPal/LLM keys
dotnet user-secrets --project src/Guardrail.Api set "Jwt:SigningKey" "$(openssl rand -base64 64 | tr -d '\n')"
dotnet user-secrets --project src/Guardrail.Api set "Seed:DemoUserPassword" "<demo password>"
dotnet user-secrets --project src/Guardrail.Api set "PayPal:ClientId" "<sandbox id>"   # optional locally
dotnet user-secrets --project src/Guardrail.Api set "Llm:Primary:ApiKey" "<AI Studio key>"   # + Llm:Primary:Model
dotnet run --project src/Guardrail.Api      # Development: migrates + seeds on startup; Swagger at http://localhost:5080/swagger
dotnet test                                 # domain + architecture + integration (Testcontainers; needs Docker)

# production migrations (separate step, ADR-014)
dotnet ef database update --project src/Guardrail.Infrastructure --startup-project src/Guardrail.Infrastructure --connection "<direct Neon connection>"

# frontend
cd frontend
npm install
npm run dev
```

See [Environment Configuration](docs/api/ENVIRONMENT_CONFIGURATION.md) for all variables.

## Honesty notes

- Sandbox only. Merchants and products are fictional seed data.
- The buyer approves every PayPal order in PayPal. Guardrail does not access PayPal accounts autonomously.
