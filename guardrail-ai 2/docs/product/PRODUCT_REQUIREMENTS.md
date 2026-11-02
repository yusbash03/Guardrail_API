# Product Requirements

Requirement IDs are referenced from tests, the roadmap and other documents. Priority: **P0** = required for the demo, **P1** = should ship, **P2** = stretch.

## FR-AUTH — Authentication and user management

| ID | Requirement | P |
|---|---|---|
| FR-AUTH-01 | Users register with email, password (min 10 chars, zxcvbn-style strength check optional), display name. | P0 |
| FR-AUTH-02 | Login returns a 15-minute JWT access token and a rotating refresh token (7 days) in an httpOnly cookie. | P0 |
| FR-AUTH-03 | Refresh rotates the token; reuse of a revoked refresh token revokes the whole token family. | P1 |
| FR-AUTH-04 | Logout revokes the refresh token and clears client caches. | P0 |
| FR-AUTH-05 | A demo account can be reset to seed state by an admin-only endpoint (disabled outside `Demo` environment). | P1 |

## FR-PROF — Spending profile

| ID | Requirement | P |
|---|---|---|
| FR-PROF-01 | Each user has exactly one active spending profile with a currency (MVP: `USD`) and an IANA time zone. | P0 |
| FR-PROF-02 | Week boundaries use ISO weeks (Monday 00:00) in the profile's time zone; months use calendar months in that zone. | P0 |

## FR-POL — Spending policies

| ID | Requirement | P |
|---|---|---|
| FR-POL-01 | Users configure category budgets (weekly or monthly) per category. | P0 |
| FR-POL-02 | Users configure overall weekly and monthly spending limits. | P0 |
| FR-POL-03 | Users configure a maximum single-transaction amount. | P0 |
| FR-POL-04 | Users configure an approval threshold; carts above it require approval. | P0 |
| FR-POL-05 | Users maintain approved and blocked merchant lists. | P0 |
| FR-POL-06 | Users choose how new (unlisted) merchants are treated: `BLOCK`, `REQUIRE_APPROVAL`, or `ALLOW`. | P0 |
| FR-POL-07 | Users restrict products by product, category or tag, with action `BLOCK` or `REQUIRE_APPROVAL`. | P0 |
| FR-POL-08 | Users enable duplicate-purchase protection with a time window. | P1 |
| FR-POL-09 | Users define time windows when purchases are blocked or need approval. | P1 |
| FR-POL-10 | Every policy change creates a new immutable policy version; previous versions are viewable. | P0 |
| FR-POL-11 | Every policy change writes an audit log entry with before/after hashes and a diff summary. | P0 |
| FR-POL-12 | Policy updates use optimistic concurrency (`expectedVersionNumber`). | P0 |
| FR-POL-13 | Users can simulate a cart against their policies without creating a purchase. | P2 |

## FR-MER / FR-CAT — Merchants and catalogue

| ID | Requirement | P |
|---|---|---|
| FR-MER-01 | List seeded merchants with name, categories, logo, and the user's status for each (approved / blocked / new). | P0 |
| FR-MER-02 | One-click approve/block from the Merchants page (creates a policy version). | P0 |
| FR-CAT-01 | Products are seeded in PostgreSQL across ≥ 4 merchants and ≥ 5 categories, ≥ 60 products. | P0 |
| FR-CAT-02 | Search by text, category, merchant, max price; results come only from the database. | P0 |
| FR-CAT-03 | Products have `isAvailable` and `stockQuantity`; unavailable products cannot be added to carts. | P0 |
| FR-CAT-04 | Catalogue access goes through an `ICatalogueProvider` abstraction. | P1 |

## FR-AGT — AI shopping agent

| ID | Requirement | P |
|---|---|---|
| FR-AGT-01 | Conversational interface with persisted conversations and messages. | P0 |
| FR-AGT-02 | The agent uses structured tool calls (see AI_TOOL_CONTRACTS.md) and nothing else to read or change state. | P0 |
| FR-AGT-03 | The agent can search, compare, build a cart, request evaluation, request approval, report status and summarise spending. | P0 |
| FR-AGT-04 | Assistant responses include structured "cards" (products, cart, evaluation, approval, purchase) rendered from server data, not from model text. | P0 |
| FR-AGT-05 | Every tool call is persisted as an `AgentExecution` with arguments, result, status and latency. | P0 |
| FR-AGT-06 | Per-turn limits: ≤ 6 LLM requests, ≤ 16 tool calls, ≤ 60 s wall time. | P0 |
| FR-AGT-08 | AI runs on free provider tiers only (Gemini primary, Groq fallback); app remains fully usable when the AI is unavailable. | P0 |
| FR-AGT-07 | The agent never receives payment, approval or policy-mutation tools. | P0 |

## FR-EVAL / FR-APR / FR-PAY / FR-LED

| ID | Requirement | P |
|---|---|---|
| FR-EVAL-01 | Deterministic evaluation of a cart returns `ALLOWED`, `REQUIRES_APPROVAL` or `BLOCKED` with reason codes. | P0 |
| FR-EVAL-02 | Every evaluation is persisted with the policy version and cart snapshot hash used. | P0 |
| FR-APR-01 | Approval requests are bound to user, purchase, cart snapshot hash, amount, currency, merchant, policy version, expiry. | P0 |
| FR-APR-02 | Approving requires the client to echo the snapshot hash it displayed and the user to re-enter their password (step-up). | P0 |
| FR-APR-03 | Approvals are single-use, expire (default 30 min), and are invalidated if the cart or policy version changes. | P0 |
| FR-PAY-01 | Checkout creates a PayPal Orders v2 order server-side and returns the PayPal `approve` link. | P0 |
| FR-PAY-02 | After buyer approval, the backend authorizes the order (intent `AUTHORIZE`), runs the final guardrail check, then captures the authorization or voids it. | P0 |
| FR-PAY-03 | PayPal webhooks are signature-verified and processed idempotently. | P0 |
| FR-PAY-04 | A reconciliation job re-checks non-terminal payments against PayPal. | P1 |
| FR-LED-01 | Ledger records proposed, reserved, authorized, captured, failed, released and refunded amounts. | P0 |
| FR-LED-02 | Budget reservations are concurrency-safe per user. | P0 |
| FR-LED-03 | The ledger is append-only; corrections are new entries. | P0 |

## FR-UI — Frontend

| ID | Requirement | P |
|---|---|---|
| FR-UI-01 | Navigation: Overview, AI Shopping, Spending Rules, Merchants, Purchase Approvals, Transactions, Settings. | P0 |
| FR-UI-02 | Dashboard: weekly budget, monthly budget, total spent, remaining allowance, pending approvals, recent purchases, AI insights. | P0 |
| FR-UI-03 | Responsive from 360 px to desktop; installable PWA; no offline caching of financial data. | P0 |
| FR-UI-04 | Transactions, ledger, approval history, policy versions and audit log use AG Grid Community on desktop. | P1 |
| FR-API-01 | Frontend API transport generated from the OpenAPI spec with APIMatic; published API reference. | P1 |

## Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-01 | p95 API latency < 300 ms for non-agent endpoints on Render starter tier (excluding cold start). |
| NFR-02 | Agent turn p95 < 15 s (LLM-bound; free-tier Gemini Flash, including client-side rate limiting). |
| NFR-03 | All money stored as `numeric(14,2)` with ISO 4217 currency code. |
| NFR-04 | All user-owned data filtered by `user_id` at the repository layer and enforced by tests. |
| NFR-05 | No secrets in the repository; secrets via environment variables only. |
| NFR-06 | Structured JSON logs with correlation IDs; no PII or secrets in logs. |
| NFR-07 | WCAG 2.1 AA colour contrast and keyboard navigation for all core flows. |

## Acceptance criteria (demo-level)

1. A $43 grocery cart from an approved merchant, with an $80 weekly grocery budget and $30 threshold, evaluates to `REQUIRES_APPROVAL` with reason `APPROVAL_THRESHOLD_EXCEEDED`, `remainingBudgetAfter = 37.00`.
2. A cart from a merchant not on the approved list, with new-merchant mode `BLOCK`, evaluates to `BLOCKED` with `MERCHANT_NOT_APPROVED`, and no purchase can be created from it.
3. After PayPal Sandbox capture completes, the ledger shows $43.00 captured for GROCERIES in the current ISO week and the dashboard shows $37.00 remaining.
4. Editing the cart after approval makes checkout fail with `APPROVAL_INVALIDATED`.
5. Blocking the merchant after checkout starts results in a voided PayPal authorization and `FINAL_CHECK_FAILED`; the buyer is not charged.
6. Approval with a wrong password changes nothing.
