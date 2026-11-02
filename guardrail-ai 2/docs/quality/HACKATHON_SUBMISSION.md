# Hackathon Submission

**Event:** Build What's Next with PayPal and AI (PayPal AI Hackathon, Devpost)
**Window:** submissions open 1 Oct 2026, close **12 Nov 2026**; winners announced 21 Dec 2026.
**Core requirement:** the project must meaningfully use both PayPal and AI.
**To confirm on Devpost:** judging criteria, video length/format, repo visibility, judge testing access, deadline time zone, and how to opt into sponsor bonus categories.

## 1. Prizes — $60,000+ USD in cash

| Track | Prize |
|---|---|
| **Best Overall** | 1st $12,000 · 2nd $8,000 · 3rd $5,000 |
| **Honorable Mentions** ($5,000 each) | Most Creative · Most Impactful · Best Demo Delivery · Best Use of PayPal + AI · Best Use of Agentic Commerce |
| **Sponsor Bonus — AG Grid** | 1st $5,000 · 2nd $2,000 · 3rd ×3 $1,000 each |
| **Sponsor Bonus — APIMatic** | ×3 $1,000 each |
| **Sponsor Bonus — Bryntum** | ×3 $1,000 each |
| **Sponsor Bonus — Channel3** | $1,500 |
| **Sponsor Bonus — Render** | 1st $1,000 · 2nd $750 · 3rd $500 (Render Credits) |

## 2. Target strategy

| Target | Priority | How Guardrail earns it | Evidence in demo/submission | Cost |
|---|---|---|---|---|
| Best Overall | **Primary** | Complete, deployed, safe agentic shopping with real Sandbox payments | Full 4-min demo, live URL, tests | — |
| Best Use of PayPal + AI | **Primary** | A Gemini-powered agent handles discovery → cart → explanation; PayPal Orders v2 **authorize → final guardrail check → capture/void** handles money; verified webhooks; strict separation | Return-page progress + webhook overlay; architecture slide | — |
| Best Use of Agentic Commerce | **Primary** | Tool-calling agent operating inside deterministic, user-owned financial guardrails; password-gated human consent; last-mile void | "AI tried, policy said no" beat; last-mile stop beat | — |
| Best Demo Delivery | **Primary** | Tight scripted demo with three "aha" moments (approval binding, blocked merchant, last-mile void) | DEMO_SCENARIO.md | rehearsal time |
| Most Impactful | Secondary | Consumer protection as AI agents start spending money | Problem framing, honesty notes | — |
| Most Creative | Secondary | "Spending policy as code" + approvals bound to cart fingerprints | Fingerprint in approval card | — |
| **AG Grid** | **Sponsor target** | AG Grid Community powers Transactions, Ledger, Approval history, Policy versions, Audit log — with custom money/status renderers and theming (ADR-017) | Transactions grid with order/authorization/capture ids and ledger tab | ~0.5 day |
| **APIMatic** | **Sponsor target (P1)** | Frontend talks to the API through an APIMatic-generated TypeScript SDK from our OpenAPI spec; published API reference (ADR-018) | SDK in repo, CI step, docs link | ~0.5 day |
| **Render** | **Sponsor target** | Docker web service + Blueprint IaC + Cron Job backstop for reconciliation (ADR-020) | `render.yaml`, live URL | already planned |
| **Channel3** | Stretch (P2) | Read-only `CompareMarketPrices` tool: the agent compares a seeded product's price with Channel3 market references; never purchasable, never used by the policy engine (ADR-019) | Chat beat: "FreshMart's milk is below the market reference" | ~0.5–1 day, only if green on Day 9 |
| **Bryntum** | Not targeted | No scheduling surface in the MVP; forcing one would dilute the demo (ADR-021) | — | — |

Rule: sponsor work never delays P0 and never weakens the safety architecture.

## 3. Positioning on PayPal's agent tooling

We evaluated PayPal's Agent Toolkit and MCP server. They expose merchant-level PayPal operations to an LLM using the merchant's credentials. Guardrail's core promise is that **the model never holds payment authority**, so the shopping agent has no PayPal tools; PayPal is called only by the backend Payment Service (ADR-016). State this explicitly in the submission — it is a deliberate design choice, not an omission.

## 4. Devpost write-up skeleton

**Name:** Guardrail AI
**Tagline:** Your money. Your rules. Your AI.

**Inspiration** — AI agents can shop, but nobody should hand one their wallet without rules.

**What it does**
- You set budgets, merchant lists, thresholds and product bans.
- You ask the AI to shop. It searches a seeded catalogue, compares prices and builds a cart.
- A deterministic policy engine decides: allowed, needs approval, or blocked — with reasons.
- You approve the exact cart with your password; any change voids the approval.
- You pay through PayPal. Guardrail authorizes the funds, runs a final check against your current rules, and only then captures — or voids, so you're never charged for something your rules no longer allow.
- A ledger tracks every dollar from proposed to captured; the AI explains what's left.

**How we built it** — .NET 10 modular monolith (Clean Architecture), PostgreSQL on Neon, React + TypeScript PWA on Vercel with AG Grid and an APIMatic-generated SDK, API on Render (Blueprint + Cron), a free-tier Gemini agent (Groq fallback) with 11 audited tools, PayPal Orders v2 + Payments v2 (Sandbox).

**PayPal usage** — OAuth client credentials; Orders v2 create (intent `AUTHORIZE`) with `experience_context`; buyer approval redirect; server-side order verification; authorize; final guardrail check; capture authorization or void; `PayPal-Request-Id` idempotency on every POST; webhook signature verification; event idempotency; reconciliation.

**AI usage** — Tool-calling agent on Google Gemini (free tier, OpenAI-compatible API) with Groq failover, with conversation memory, intent handling, price comparison, cart building, policy explanation and budget Q&A. Closed tool allowlist; no payment, approval, policy-write or PayPal tools; red-team test suite.

**Challenges** — Making safety structural rather than prompt-based; binding approvals to exact carts; keeping the ledger idempotent across API responses and webhooks; a last-mile check between authorization and capture.

**What's next** — Passkey step-up, household approvals, real merchant connectors, recurring baskets.

**Honesty notes** — Sandbox only. The buyer approves every PayPal order; Guardrail does not access buyer accounts autonomously. Merchants are fictional seeded data with one PayPal payee. Channel3 data (if shown) is reference-only.

## 5. Assets checklist

- [ ] Public GitHub repo with README, docs/, licence (MIT suggested)
- [ ] Live demo URL + judge account (and Sandbox buyer credentials if rules allow)
- [ ] Demo video (DEMO_SCENARIO.md), captions on
- [ ] Architecture diagram (from ARCHITECTURE.md)
- [ ] Screenshots: Overview, AI Shopping with approval card + password dialog, Rules, Transactions grid (AG Grid), blocked evaluation, last-mile void
- [ ] Sponsor evidence: AG Grid screens; APIMatic SDK folder + CI step + API reference link; `render.yaml`; (Channel3 tool, if shipped)
- [ ] "Built with" tags: paypal, gemini, groq, dotnet, csharp, postgresql, neon, react, typescript, tailwind, ag-grid, apimatic, render, vercel
