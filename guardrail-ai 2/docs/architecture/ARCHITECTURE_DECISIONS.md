# Architecture Decision Records

Format: Context → Decision → Consequences. Status is `Accepted` unless noted.

---

## ADR-001 Modular monolith with Clean Architecture
**Context:** 6-week hackathon, one developer team, financial correctness matters more than scale.
**Decision:** One ASP.NET Core service, four projects (Domain, Application, Infrastructure, Api). Modules are folders/namespaces with explicit interfaces.
**Consequences:** Simple deploy, ACID transactions across modules. Module boundaries enforced by convention + architecture tests (NetArchTest).

## ADR-002 Policy Engine is a pure domain function
**Context:** The policy engine is the final authority and must be provably deterministic.
**Decision:** `PolicyEngine.Evaluate(PolicySnapshot, CartSnapshot, SpendingSnapshot, DateTimeOffset now)` with no I/O, no randomness, no LLM.
**Consequences:** Exhaustive unit tests; the same inputs always give the same output; evaluation is re-run inside the reservation transaction.

## ADR-003 AI has a closed, read-mostly tool set
**Context:** LLMs can be prompt-injected and can hallucinate.
**Decision:** The agent gets 11 tools. The only state-changing tools are `CreateShoppingCart` and `RequestPurchaseApproval`; neither can move money or grant permission. `userId` is injected from the JWT, never taken from tool arguments.
**Consequences:** Worst case from a compromised model: an unwanted cart or an approval *request* the user can reject.

## ADR-004 Policies are versioned as immutable rule sets
**Context:** Approvals must bind to the policy that produced them; audits must show history.
**Decision:** `PolicyVersion` rows are immutable; `SpendingPolicy` rule rows belong to one version. Editing = copy-on-write to version n+1. `SpendingProfile.current_policy_version_id` points to the active version.
**Consequences:** Cheap diffing and exact replay of past decisions. Slight write amplification (rules copied per version) — negligible at this scale.

## ADR-005 PayPal Orders v2 with intent `AUTHORIZE`, guarded server-side capture
**Status:** Accepted (revised 1 Nov 2026; supersedes the earlier `CAPTURE` decision).
**Context:** Need real PayPal integration without implying autonomous access to the buyer's account. The ledger distinguishes *authorized* from *captured* spending, and the product promise is that Guardrail controls money up to the last moment.
**Decision:** Backend creates the order with `intent: AUTHORIZE` and `payment_source.paypal.experience_context`. Buyer approves on PayPal. Backend then:
1. `GET /v2/checkout/orders/{id}` → must be `APPROVED`, amounts must match;
2. `POST /v2/checkout/orders/{id}/authorize` → PayPal authorization (funds held) → ledger `AUTHORIZE`;
3. runs the **final guardrail check** (snapshot integrity + merchant/product still permitted);
4. pass → `POST /v2/payments/authorizations/{authorizationId}/capture` → ledger `CAPTURE`;
   fail → `POST /v2/payments/authorizations/{authorizationId}/void` → ledger `VOID`.
**Why:** (a) the ledger's "authorized" state maps to a real PayPal authorization, not an internal flag; (b) the demo can show Guardrail stopping money *after* the buyer approved (e.g. merchant blocked in between) — a strong agentic-commerce safety story; (c) all three calls are documented Orders/Payments v2 operations available in Sandbox.
**Fallback:** `PayPal:Intent=CAPTURE` switches to `POST /v2/checkout/orders/{id}/capture` (no final void window). Used only if the Day-1 Sandbox spike shows authorization is unavailable for the Sandbox business account.
**Consequences:** One extra PayPal call per purchase; void handling and two more webhook events (`PAYMENT.AUTHORIZATION.CREATED`, `PAYMENT.AUTHORIZATION.VOIDED`). Every payment still requires buyer approval in PayPal.

## ADR-006 Webhook verification via PayPal postback API
**Context:** PayPal supports self-verification (CRC32 + certificate) or postback to `POST /v1/notifications/verify-webhook-signature`.
**Decision:** Use postback for the MVP (less crypto code to get wrong). Wrap in `IPayPalWebhookVerifier` so self-verification can replace it later.
**Consequences:** One extra PayPal call per webhook; acceptable. The postback requires the event body to be passed as received — we store the raw body and deserialize it once to a `JsonElement` for the request.

## ADR-007 Same-origin API via Vercel rewrites
**Context:** Cross-site cookies (Vercel ↔ Render) need `SameSite=None` and complicate CSRF.
**Decision:** Vercel rewrites `/api/:path*` to the Render URL. Refresh token cookie is `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`.
**Consequences:** Simpler CORS (only for local dev). Adds one proxy hop. Webhooks bypass Vercel.

## ADR-008 Concurrency via PostgreSQL row locks, not Redis
**Context:** Two concurrent purchases must not both consume the last budget.
**Decision:** Purchase creation locks the user's `spending_profiles` row (`FOR UPDATE`) and re-evaluates inside the transaction. Optimistic concurrency (`xmin`) on carts, approvals, purchases, payments.
**Consequences:** Per-user serialization of reservations (fine: humans don't buy 100 carts/second).

## ADR-009 Ledger as bucket transitions
**Context:** Need to distinguish proposed/reserved/authorized/captured/failed/released/refunded without double-counting.
**Decision:** Each ledger entry moves an amount from one bucket to another (`from_bucket → to_bucket`). Budget consumption = balance(RESERVED) + balance(AUTHORIZED) + balance(CAPTURED). Unique constraint on `(purchase_id, category_code, transition)` makes each transition happen once.
**Consequences:** Append-only, auditable, idempotent. See SPENDING_LEDGER.md.

## ADR-010 Cart snapshot hashing
**Decision:** Canonical JSON (sorted keys, invariant culture, amounts as fixed 2-dp strings) → SHA-256 hex. Hash covers merchant, currency, line items (productId, sku, unitPrice, quantity, lineTotal, categoryCode), total.
**Consequences:** Any change to the cart changes the hash and voids approvals.

## ADR-011 LLM provider: free tier — Google Gemini API (primary), Groq (fallback)
**Status:** Accepted (revised 1 Nov 2026 — supersedes the Claude Sonnet 5.5 decision; requirement: **no paid AI**).
**Context:** The agent needs reliable structured tool calling and multi-step planning, at **zero cost** for development, judging and the demo.
**Decision:**
- **Primary:** Google **Gemini API free tier** (Google AI Studio key, no credit card), current **Gemini Flash** model, called through Gemini's **OpenAI-compatible endpoint** `https://generativelanguage.googleapis.com/v1beta/openai/`. Model id from configuration; pick the current free Flash model shown in AI Studio (Oct 2026 free list: Gemini 3.8 Flash, 3.5 Flash, 3.5 Flash-Lite, 2.5 Flash, Gemma 4) **[VERIFY exact id + function-calling support on Day 0]**.
- **Fallback:** **Groq free tier**, model `openai/gpt-oss-120b`, OpenAI-compatible base URL `https://api.groq.com/openai/v1`. Used automatically on 429/5xx from the primary.
- One adapter, `OpenAiCompatibleLlmClient`, serves both (and any other OpenAI-compatible provider, e.g. a paid model later) — provider switch is configuration only.
**Why Gemini first:** strongest free model with tool calling and the most generous free daily quota of the candidates (community-tracked ≈ 10 RPM / ≈ 1,500 requests per day for 3.x Flash — indicative; Google shows real per-project limits in AI Studio).
**Why not the others as primary:** Groq free tier is fast but capped at 8,000 tokens/minute and 200,000 tokens/day — a single agent turn with tool schemas can approach the per-minute cap. OpenRouter free models allow only 50 requests/day without a top-up. Self-hosted (Ollama) costs hosting money on Render and is slow on CPU.
**Free-tier constraints we design for:**
1. **Few LLM round-trips per turn** — `SearchProducts` accepts up to 12 queries in one call; the model may return several tool calls in one response (executed sequentially server-side). Target ≤ 6 LLM requests per shopping turn.
2. **Client-side rate limiter** (token bucket per provider, from config) so we queue instead of hitting 429; honour `Retry-After`.
3. **Fallback chain** Gemini → Groq; if both are exhausted, the API returns `503 AGENT_UNAVAILABLE` and the rest of the app (rules, approvals, checkout) keeps working — safety never depends on the LLM.
4. **Privacy:** Google's free tier may use prompts to improve products outside the EU/UK/EEA. Guardrail sends the model only fictional catalogue data and tool projections — no emails, names, payment data or secrets (SEC-21). Real-user production would move to a paid tier.
**Consequences:** Zero AI cost. Quality is slightly below top paid models, so the red-team suite and 3 demo rehearsals are mandatory on the chosen free model. Upgrading later (e.g. Claude) is a config change: any provider with an OpenAI-compatible endpoint, or a new adapter behind `ILlmClient`.

## ADR-012 One merchant per cart, platform as PayPal payee
**Context:** Seeded merchants are fictional; real multi-seller payouts need PayPal multiparty onboarding.
**Decision:** The Guardrail sandbox business account is the payee for all orders. Merchant name is passed as item/description metadata. Carts are single-merchant.
**Consequences:** Honest demo; a production version needs a payee per merchant (out of scope).

## ADR-013 No offline financial data in the PWA
**Decision:** Service worker precaches only the app shell and static assets; `/api/*` is network-only; TanStack Query cache is memory-only and cleared on logout.

## ADR-014 Docker on Render
**Context:** Render has no native .NET runtime.
**Decision:** Multi-stage Dockerfile (`mcr.microsoft.com/dotnet/sdk:10.0` → `aspnet:10.0`). EF migrations applied by a separate one-off command, not at app startup in production.

## ADR-015 Password step-up to approve a purchase
**Status:** Accepted (1 Nov 2026).
**Context:** An approval is the human's consent to spend. A stolen access token, an XSS bug, or a confused-deputy path should not be enough to approve.
**Decision:** `POST /approvals/{id}/approve` requires the user's current password in the body alongside `cartSnapshotHash`. The server verifies it with the Identity password hasher before any state change. Failures count toward the account lockout (SEC-05) and a dedicated limit of 5 failed approval attempts per 15 minutes per user.
**Consequences:** Approval cannot be completed by anything that holds only a token — including any future agent integration. One extra field in the UI (password dialog on the approval card). Passwords are never logged and the field is excluded from request logging. Post-hackathon option: WebAuthn/passkey step-up instead of password.

## ADR-016 PayPal Agent Toolkit / MCP server is not given to the shopping agent
**Status:** Accepted (1 Nov 2026).
**Context:** PayPal offers an Agent Toolkit and an MCP server (local `@paypal/mcp`, remote `mcp.sandbox.paypal.com`) that expose PayPal business operations (e.g. invoices, transaction reporting) to an LLM, authenticated with the merchant's access token or client credentials.
**Decision:** The Guardrail shopping agent gets **no** PayPal tools, toolkit or MCP connection. All PayPal calls stay in the backend Payment Service.
**Why:** Connecting the toolkit would put merchant-level PayPal credentials and money-moving operations within the LLM's reach — exactly what the product promises never to do — and the merchant-oriented tools don't fit a consumer spending app. The submission states this as a deliberate design choice ("we evaluated PayPal's agent tooling and kept payment authority out of the model").
**Allowed:** Developers may use the PayPal MCP server in their own coding assistant during development to inspect Sandbox transactions. That is tooling, not product.

## ADR-017 AG Grid Community for data-dense tables (sponsor: Best Use of AG Grid)
**Status:** Accepted (1 Nov 2026).
**Decision:** Use AG Grid **Community** (free) in React for Transactions, Ledger entries, Approval history, Policy version history and Audit log on screens ≥ 1024 px. Mobile keeps card lists. No Enterprise features (licence required) unless a sponsor licence is provided.
**Consequences:** One themed grid wrapper (`DataGrid`) using design tokens; server-side pagination via our API (`pageSize` ≤ 100). Money columns use custom cell renderers (`MoneyText`, status chips).

## ADR-018 APIMatic-generated TypeScript SDK and API docs (sponsor: Best Use of APIMatic)
**Status:** Accepted, P1 (1 Nov 2026).
**Decision:** The OpenAPI document (`/openapi/v1.json`) is the source for an APIMatic-generated TypeScript SDK (`apimatic sdk generate --language=typescript`) consumed by the frontend, and for published API reference docs. Generation runs in CI on contract changes; generated code is never hand-edited.
**Guardrail:** The frontend still validates every response with Zod at the boundary; the SDK is transport only.
**Fallback:** If APIMatic access or output blocks progress, keep the hand-written `apiClient` (same interface) and drop the sponsor target.

## ADR-019 Channel3 as an optional, read-only market-price reference (sponsor: Best Use of Channel3)
**Status:** Proposed, P2 — decide on Day 9 only if P0/P1 are green.
**Context:** Channel3 provides a multi-store product catalogue for agents (UCP; REST `POST /v1/search`, `POST /v1/lookup`, `GET /v1/products/{id}`; API key via `x-api-key`). Guardrail cannot sell those products: PayPal orders go to our Sandbox payee and seeded merchants only.
**Decision (if adopted):** `Channel3MarketReferenceProvider` behind `ICatalogueProvider`, exposed to the agent as a 12th **read-only** tool `CompareMarketPrices(productId)` that returns external reference prices labelled "market reference — not purchasable in Guardrail". Results are never added to carts, never used by the policy engine, and cached ≤ 15 minutes.
**Consequences:** Adds an external dependency and an LLM-visible untrusted data source (sanitised like catalogue text). The tool-count architecture test becomes 12 only if this ADR is accepted.

## ADR-020 Render Blueprint (sponsor: Best Use of Render)
**Status:** Accepted (1 Nov 2026).
**Decision:** Infrastructure as code with `render.yaml` (Blueprint): Docker web service for the API with health check, environment groups for secrets, and a Render **Cron Job** that calls the reconciler/sweeper endpoints as a backstop to the in-process hosted services. Preview environments for PRs if the plan allows **[VERIFY]**.
**Consequences:** Reproducible deploys; clear story for the Render sponsor category.

## ADR-021 Bryntum not adopted
**Status:** Accepted (1 Nov 2026).
**Context:** Bryntum's products are scheduling/Gantt/calendar components.
**Decision:** Not used. Guardrail has no scheduling surface in the MVP; adding one only to target a $1,000 sponsor prize would dilute the demo. Revisit if "recurring baskets" (post-MVP) becomes a scheduled-shopping calendar.
