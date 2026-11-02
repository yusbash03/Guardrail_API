# Architecture

## 1. Style

**Modular monolith** using **Clean Architecture**. One deployable ASP.NET Core (.NET 10 LTS) API, one PostgreSQL database (Neon), one React SPA (Vercel). No message broker, no Redis, no Kubernetes. Background work runs as `IHostedService`s inside the API process.

## 2. Context diagram

```
                ┌────────────────────────────┐
  Browser/PWA ─▶│ Vercel (React SPA)         │
                │  /api/* rewrite ──────────┐│
                └───────────────────────────┼┘
                                            ▼
                ┌────────────────────────────────────────────┐
                │ Render: Guardrail.Api (.NET 10, Docker)     │
                │                                              │
  PayPal ──────▶│ POST /api/v1/webhooks/paypal                 │
  (webhooks)    │                                              │
                │  ┌──────────┐  tools   ┌──────────────────┐  │      ┌──────────────┐
                │  │ AI Agent │────────▶ │ Application      │──┼────▶ │ Gemini/Groq  │
                │  │ Orchestr.│◀──────── │ services (use    │  │      └──────────────┘
                │  └──────────┘ results  │ cases)           │  │
                │                        │  ├ Policy Engine │  │      ┌──────────────┐
                │                        │  ├ Authorization │──┼────▶ │ PayPal REST  │
                │                        │  ├ Shopping      │  │      │ (Sandbox)    │
                │                        │  ├ Payment       │  │      └──────────────┘
                │                        │  └ Ledger        │  │
                │                        └────────┬─────────┘  │
                └─────────────────────────────────┼────────────┘
                                                  ▼
                                       ┌────────────────────┐
                                       │ Neon PostgreSQL    │
                                       └────────────────────┘
```

## 3. The five trust zones

| Zone | Responsibility | Can | Cannot |
|---|---|---|---|
| **AI Agent** | Understand intent, call tools, narrate | Read catalogue, read own policies/budgets, create carts, request evaluation, request approval, read status | Approve, pay, capture, edit policies, write ledger, see secrets, query DB |
| **Spending Policy Engine** | Pure function: `(PolicySnapshot, CartSnapshot, SpendingSnapshot, Clock) → PolicyDecision` | Decide `ALLOWED/REQUIRES_APPROVAL/BLOCKED` | Do I/O, mutate state |
| **Authorization Service** | Create approval requests, verify approvals, invalidate/expire/consume them | Bind approvals to snapshots; gate checkout | Call PayPal |
| **Shopping Service** | Carts, cart items, pricing from DB, snapshots, purchases | Lock carts, compute totals | Decide policy, pay |
| **Payment Service** | PayPal OAuth, orders, authorize, final check, capture/void, webhooks, reconciliation | Move money (sandbox) after verifying authorization | Be called by the AI |

The **Spending Ledger** is a domain service used by Shopping (reserve/release) and Payment (authorize/capture/fail/refund). Only those services write ledger entries.

## 4. Layering

```
Guardrail.Domain          Entities, value objects, enums, domain events, PolicyEngine (pure)
Guardrail.Application     Use cases (commands/queries), interfaces (ports), validators, DTOs
Guardrail.Infrastructure  EF Core, repositories, PayPal client, LLM client, clock, hashing, jobs
Guardrail.Api             Controllers/minimal endpoints, auth, rate limiting, ProblemDetails, Swagger
```

Dependency rule: `Api → Application → Domain`; `Infrastructure → Application, Domain`. Domain references nothing.

The Policy Engine lives in **Domain** so it has zero I/O and can be tested exhaustively.

## 5. Key request flows

### 5.1 Agent turn

1. `POST /agent/conversations/{id}/messages` (JWT).
2. `AgentOrchestrator` loads last N messages, builds system prompt + tool schemas (only the 11 allowlisted tools).
3. Loop (≤ 6 LLM requests, ≤ 16 tool calls): LLM → tool call(s) → `ToolDispatcher` validates args (FluentValidation), injects `userId` from JWT (never from model), invokes the matching application use case, persists `AgentExecution`, returns a size-bounded JSON result to the model.
4. Final assistant text + list of `cards` (references to server objects: product IDs, cart ID, evaluation ID, approval ID, purchase ID). The API hydrates cards from the database, so the UI never renders model-invented numbers.

### 5.2 Evaluation

`EvaluateCartUseCase` → load cart (owned by user) → build `CartSnapshot` with DB prices → load active `PolicySnapshot` → load `SpendingSnapshot` from ledger for affected periods → `PolicyEngine.Evaluate` → persist `PolicyEvaluation` → return decision.

### 5.3 Purchase creation + reservation

`CreatePurchaseUseCase` in one DB transaction:
1. `SELECT … FROM spending_profiles WHERE user_id = @u FOR UPDATE` (per-user serialization).
2. Re-evaluate the cart (fresh spending snapshot inside the lock). The decision must equal the referenced evaluation's decision and snapshot hash, else `EVALUATION_STALE`.
3. Lock cart (`status = LOCKED`, `version++`).
4. Insert `Purchase`, `ApprovalRequest` (if needed), ledger `RESERVED` entries.
5. Commit.

### 5.4 Checkout and capture

See PURCHASE_AUTHORIZATION.md and PAYPAL_INTEGRATION.md. Summary: verify approval (password step-up + atomic consume) → create PayPal order (intent `AUTHORIZE`) with `PayPal-Request-Id` → redirect buyer → on return, backend `GET`s the order, **authorizes** it (funds held, ledger AUTHORIZED), runs the **final guardrail check**, then captures the authorization (ledger CAPTURED) or voids it; webhooks confirm idempotently.

## 6. Background services (`IHostedService`)

| Job | Interval | Purpose |
|---|---|---|
| `ApprovalExpirySweeper` | 60 s | Expire approvals/purchases past `expires_at`, release reservations |
| `CheckoutExpirySweeper` | 5 min | Release reservations for PayPal orders not approved within 3 h (order `CREATED` lifetime) |
| `WebhookProcessor` | 5 s | Process stored, verified `PayPalWebhookEvents` with `status = RECEIVED` |
| `PaymentReconciler` (P1) | 10 min | `GET` PayPal order/authorization/capture for non-terminal payments; finish or void stale authorizations |

A Render Cron Job (ADR-020) also calls an internal, secret-protected `POST /internal/jobs/run` as a backstop if the web service was asleep.

All jobs use `SELECT … FOR UPDATE SKIP LOCKED` batch claiming so a second instance (if ever scaled) is safe.

## 7. Cross-cutting

- **Auth:** JWT bearer; `ICurrentUser` abstraction; every query filtered by `UserId`.
- **Validation:** FluentValidation on all commands, including AI tool arguments.
- **Errors:** RFC 9457 ProblemDetails with stable `code` (ERROR_HANDLING.md).
- **Idempotency:** `Idempotency-Key` header on purchase/checkout/capture endpoints; PayPal `PayPal-Request-Id` on PayPal POSTs; unique DB constraints.
- **Observability:** Serilog JSON, `X-Correlation-Id`, PayPal `debug_id` logged on every PayPal error.
- **Time:** `TimeProvider` injected everywhere (testable clock).

## 8. Deployment topology

- Vercel serves the SPA and rewrites `/api/*` to the Render service, making API calls same-origin for the browser (first-party cookies, simpler CORS). See ADR-007.
- PayPal webhooks hit Render's public URL directly.
- Neon connection via pooled connection string, `SSL Mode=Require`.

## 9. Scaling posture

Single Render instance for the hackathon. The design stays correct with N instances because concurrency control is in PostgreSQL (row locks, unique constraints, `SKIP LOCKED`), not in memory.
