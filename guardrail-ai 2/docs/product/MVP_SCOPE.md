# MVP Scope

Hackathon submission deadline: **12 Nov 2026** (PayPal "Build What's Next with PayPal and AI"). Scope is cut to what the demo needs, built properly.

## In scope (P0)

- JWT auth (register/login/refresh/logout), single user role + `Admin` for demo reset.
- One spending profile per user, `USD` only.
- Policy rule types: `CATEGORY_BUDGET`, `SPENDING_LIMIT`, `MAX_TRANSACTION_AMOUNT`, `APPROVAL_THRESHOLD`, `MERCHANT_ALLOW`, `MERCHANT_BLOCK`, `NEW_MERCHANT_POLICY`, `PRODUCT_RESTRICTION`, `UNBUDGETED_CATEGORY_POLICY`.
- Versioned, audited policy edits.
- Seeded catalogue: 5 merchants, 6 categories, ~60 products.
- Tool-calling agent with 11 tools, persisted conversations, structured cards.
- Render Blueprint (`render.yaml`) with Cron Job backstop (sponsor: Render).
- Deterministic policy engine with full unit-test coverage of rule types used in the demo.
- Purchases, approvals bound to cart snapshots, expiry sweeper.
- PayPal Orders v2: create (intent `AUTHORIZE`), approve redirect, server-side authorize → final guardrail check → capture authorization / void, webhook verification via the `verify-webhook-signature` postback API, idempotent event storage. (`CAPTURE` intent kept as config fallback.)
- Password step-up on approval.
- Tool-calling agent on a **free** LLM tier: Gemini API (primary) with Groq failover.
- Ledger with reservation/authorization/capture/release/failure.
- Frontend pages listed in FRONTEND_SPECIFICATION.md, responsive, installable PWA.
- Deployment: Vercel, Render (Docker), Neon, PayPal Sandbox.

## Should ship (P1)

- `DUPLICATE_PURCHASE` and `TIME_RESTRICTION` rules.
- Reconciliation job for non-terminal payments.
- Refresh-token reuse detection.
- Policy version history viewer with diff.
- Demo reset endpoint.
- AG Grid Community tables (sponsor: AG Grid).
- APIMatic-generated TypeScript SDK + API reference (sponsor: APIMatic).

## Stretch (P2)

- Refund handling via webhook.
- Policy simulation endpoint and UI.
- Streaming agent responses (SSE).
- Channel3 read-only market-price comparison tool (sponsor: Channel3, ADR-019).
- Voice input.

## Explicitly out of scope

- Live PayPal, real merchants, scraping, live inventory.
- Multiple currencies, FX.
- Multi-merchant carts (one cart = one merchant = one PayPal order).
- Multi-user households, shared approvals.
- Autonomous payment without buyer approval in PayPal.
- Native mobile apps.
- Redis, Kubernetes, microservices, message brokers.
- PayPal Agent Toolkit / MCP in the agent (ADR-016).
- Bryntum components (ADR-021).

## Scope guards

Any new feature must answer: *does it make the 4-minute demo clearer, the safety guarantee stronger, or win a prize category at low cost without bending the architecture?* If none, it waits.
