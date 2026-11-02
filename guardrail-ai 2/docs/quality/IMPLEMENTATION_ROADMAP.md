# Implementation Roadmap

**Today:** Sun 1 Nov 2026. **Submission deadline:** Thu 12 Nov 2026. That leaves ~11 days. The plan front-loads the riskiest integration (PayPal authorize/capture + webhooks on a deployed host) and keeps a buffer day.

**Gate 0:** no application code until these contracts are approved.

## Decisions log (1 Nov 2026)

| # | Question | Decision | Record |
|---|---|---|---|
| D1 | LLM provider/model | **Free:** Google Gemini API free tier (Gemini Flash) primary, Groq free tier (`openai/gpt-oss-120b`) fallback — revised from Claude Sonnet 5.5 at your request | ADR-011 |
| D2 | PayPal intent | **`AUTHORIZE`** → final guardrail check → capture or void; `CAPTURE` as config fallback | ADR-005 |
| D3 | Approval step-up | **Password** re-entry on approve | ADR-015 |
| D4 | PayPal Agent Toolkit / MCP | **Not connected to the agent**; stated as a design choice | ADR-016 |
| D5 | Sponsor targets | AG Grid (P1), APIMatic (P1), Render (P0, already used), Channel3 (P2 stretch), Bryntum (not targeted) | ADR-017–021 |

## Phase plan

| Day | Date | Phase | Deliverables | Exit criteria |
|---|---|---|---|---|
| 0 | Sun 1 Nov | **Contracts & provisioning** | Contracts approved; repo scaffold (solution, 4 projects + tests, Vite app, shadcn init); CI; Neon, Render (Blueprint), Vercel, PayPal Sandbox app + accounts; free Google AI Studio key + free Groq key; confirm Gemini model id, function calling and free RPM/RPD in AI Studio | CI green on skeleton; `/health` live on Render via Vercel rewrite |
| 1 | Mon 2 Nov | **PayPal spike (de-risk)** | OAuth → create order (`AUTHORIZE`) → manual approve → authorize → capture authorization; void path; webhook endpoint with postback verification | Real Sandbox authorization + capture COMPLETED; a void succeeds; webhook verified SUCCESS on Render. All [VERIFY] items resolved, docs updated. If authorize is unavailable → switch to `CAPTURE` fallback today |
| 2 | Tue 3 Nov | **Domain + persistence + auth** | Entities, EF configs, migrations, seed (categories, 5 merchants, ~60 products, demo user), JWT auth + refresh, password step-up service | IT-01; seed returns the $43 cart items |
| 3 | Wed 4 Nov | **Policy engine + policies** | PolicyEngine (all P0 rules), hashing, PolicyDocument mapping, `GET/PUT /policies`, versions, merchants approve/block, audit | Domain tests E1–E9, IT-02 |
| 4 | Thu 5 Nov | **Carts, evaluation, purchases, approvals, ledger** | Carts API, evaluate, CreatePurchase with lock, approve (password) / reject, sweepers, ledger service (incl. VOID), dashboard/ledger summary | IT-03–06, IT-14–16 |
| 5 | Fri 6 Nov | **Payments** | Checkout (2-tx), CompletePayment (verify → authorize → final check → capture/void), webhook processor (incl. authorization events), idempotency keys, reconciler + `/internal/jobs/run` | IT-07–13, IT-10b–e, IT-17, IT-18, IT-21, IT-22; end-to-end via Swagger + real Sandbox |
| 6 | Sat 7 Nov | **AI agent** | OpenAI-compatible client + rate-limited router (Gemini → Groq), ToolRegistry (11 tools, batch search), dispatcher, orchestrator, conversations API, card hydration | IT-19, IT-20; J1 & J2 via API with live Gemini, ≤ 6 LLM requests per turn; failover to Groq tested |
| 7 | Sun 8 Nov | **Frontend I** | Shell, auth, Overview, Rules, Merchants | Rules editing creates versions; dashboard renders ledger data |
| 8 | Mon 9 Nov | **Frontend II** | AI Shopping (chat + cards), Approvals + StepUpDialog, Checkout return/cancel (progress states), Transactions/Ledger with **AG Grid** | Full J1 + J6b in browser on deployed stack |
| 9 | Tue 10 Nov | **Hardening + sponsors** | **APIMatic** SDK swap-in + CI step + API reference; PWA, rate limits, headers, red-team, accessibility; P1 rules if green; **Channel3** only if everything else is green | Security checklist; Lighthouse; 3 clean rehearsals |
| 10 | Wed 11 Nov | **Demo & submission** | Record video, screenshots, Devpost write-up (incl. sponsor sections), architecture diagram | Submission draft complete |
| 11 | Thu 12 Nov | **Buffer / submit early** | Fixes only | Submitted before deadline (confirm deadline time zone on Devpost) |

## Cut order if behind schedule

1. Channel3 (P2) and other P2 items (refunds, simulate, streaming).
2. APIMatic SDK (keep hand-written client; drop the sponsor target).
3. Reconciler auto-completion (keep webhook + synchronous complete-payment; keep stale-authorization void).
4. `DUPLICATE_PURCHASE`, `TIME_RESTRICTION` rules (docs only).
5. Policy history diff UI (keep list).
6. Playwright E2E (keep integration tests).

**Never cut:** policy engine tests, approval binding + password step-up, final guardrail check + void, webhook verification, ledger idempotency, isolation tests, AG Grid Transactions screen (cheap, high-value prize).

## Work breakdown

Single developer + Claude Code: one phase = one branch = one PR; the tests in TESTING_STRATEGY.md are the definition of done.

---

## Consistency review (re-run 1 Nov 2026 after decisions)

| Area | Result |
|---|---|
| Enums across DOMAIN_MODEL, DATABASE_SCHEMA CHECKs, API DTOs, AI tools | Aligned. Added `PAYMENT_AUTHORIZED` (purchase), `AUTHORIZED` (payment), `VOID` (ledger transition) everywhere |
| PayPal flow across integration, webhook contract, state machine, ledger, purchase authorization | Aligned on `AUTHORIZE` → final check → capture/void; fallback documented once (PAYMENT_STATE_MACHINE.md §5) |
| Password step-up across APPROVAL_WORKFLOW, API_CONTRACT, ERROR_HANDLING, SECURITY, FRONTEND, TESTING, BUSINESS_RULES | Aligned (`REAUTHENTICATION_FAILED` 401 is distinct from `UNAUTHENTICATED`) |
| Decision contract vs user example | Unchanged; superset |
| Tool count | 11 (12 only if ADR-019 is accepted) |
| Required DB entities | All 19 present; added `refresh_tokens`, `categories`, `idempotency_keys`; payments gained authorization/void columns |

## Risks

| # | Risk | Impact | Likelihood | Mitigation |
|---|---|---|---|---|
| R1 | 11-day timeline | High | High | Day-1 spike, strict cut order, Claude Code per phase |
| R2 | `AUTHORIZE` unavailable or behaving differently for the Sandbox business account | High | Low–Med | Day-1 spike; config fallback to `CAPTURE` (state machine §5) |
| R3 | Webhook verification postback fails due to body re-serialization | High | Medium | Raw-body splice; fallback self-verification behind `IPayPalWebhookVerifier` |
| R4 | Sandbox latency/outage during recording | High | Low–Med | Pre-recorded video; synchronous complete-payment path doesn't wait for webhooks |
| R5 | LLM variance in live demo | Medium | Medium | Explicit prompt, temperature 0.2, rehearsals, flow tolerant of different totals |
| R5b | Free-tier rate limits/quota hit during judging or recording | High | Medium | ≤ 6 requests/turn, client-side limiter, Gemini → Groq failover, app fully usable without AI, record demo early in the day (quotas reset midnight Pacific), separate key for the demo vs development |
| R5c | Free model weaker at multi-step tool use than paid models | Medium | Medium | Batch search tool, tight system prompt, red-team + 3 rehearsals on the exact free model; config switch to a paid model if ever funded |
| R6 | Render cold starts | Medium | High on free tier | Paid tier or warm-up |
| R7 | Password dialog slows the demo | Low | Medium | Password manager autofill off-camera; it reinforces "only I can approve" |
| R8 | Sponsor work steals time from P0 | Medium | Medium | Sponsor tasks scheduled Day 8–9 only, cut order above |
| R9 | APIMatic/AG Grid licensing or account limits | Low | Low | Community edition only; APIMatic fallback to hand-written client |
| R10 | Channel3 data quality or availability | Low | Medium | P2, reference-only, behind flag |
| R11 | Week/DST bugs | Low | Medium | FakeTimeProvider tests incl. 1 Nov 2026 DST change |
| R12 | "Agentic commerce" judged weak because buyer approves in PayPal | Medium | Medium | Frame honestly; showcase final check + void as agentic safety |

## Remaining open questions

1. **Hackathon rules** — confirm on Devpost: judging criteria, video length, whether sponsor bonuses require a separate opt-in or write-up section.
2. **Shipping** — demo uses `NO_SHIPPING`; acceptable?
3. **Currency** — USD only; fine for this product?
4. **Default policy for new users** (BR-12: approval required for everything) — acceptable?
5. **Domains** — default Vercel/Render domains or custom?
6. **Channel3** — attempt the stretch on Day 9, or drop it now to protect the schedule?
