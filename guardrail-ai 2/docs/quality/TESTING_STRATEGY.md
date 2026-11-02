# Testing Strategy

## 1. Pyramid

| Layer | Tooling | Scope | Target |
|---|---|---|---|
| Domain unit | xUnit, FluentAssertions/Shouldly | Policy engine, ledger math, state machines, snapshot hashing, Money | 95 % line coverage of `Guardrail.Domain` |
| Application unit | xUnit + fakes (in-memory clock, fake PayPal, fake LLM) | Use cases, tool dispatcher, validators | all use cases happy + main failure path |
| Integration | xUnit + `WebApplicationFactory` + Testcontainers PostgreSQL 16 | HTTP → DB, concurrency, idempotency, isolation, webhooks | all P0 endpoints |
| Architecture | NetArchTest | Layer deps; agent tools can't reference payment/approval/policy-write or any PayPal SDK/MCP client | all rules |
| Frontend unit | Vitest + Testing Library | components, Zod schemas, formatters | core components |
| E2E | Playwright | Core demo journey against local stack with PayPal **fake** | J1, J2, J3 |
| Manual Sandbox | Checklist | Real PayPal Sandbox + real LLM | before every demo recording |

## 2. Mandatory domain tests

### Policy engine
- E1–E9 from SPENDING_POLICY_ENGINE.md §5 (exact decisions, reason codes, `remainingBudget`).
- Each reason code has at least one producing test.
- Boundaries: limit equality inclusive; threshold equality strict.
- Time windows: same-day, overnight, DST transition (America/New_York 2026-11-01).
- Week boundary: Sunday 23:59 vs Monday 00:00 local.
- Determinism: evaluating the same inputs 100× yields identical serialized output.
- Property-based (FsCheck, P1): decision is BLOCKED iff any BLOCK reason; adding items never turns BLOCKED into ALLOWED for budget rules.

### Hashing
- Golden test: fixed cart → fixed SHA-256 hex.
- Item order permutation → same hash; quantity change → different hash.

### Ledger
- Every transition table row; invalid source bucket rejected; committed calculation per bucket.

### State machines
- All legal transitions succeed; a table-driven test asserts every illegal pair throws.

## 3. Mandatory integration tests

| # | Test |
|---|---|
| IT-01 | Register → login → refresh rotation → reuse detection revokes family |
| IT-02 | Policy PUT creates version n+1, audit row; stale `expectedVersionNumber` → 409 |
| IT-03 | Cart → evaluate → purchase (REQUIRES_APPROVAL) → ledger PROPOSE |
| IT-04 | Approve with correct hash + password → READY_FOR_CHECKOUT + CONFIRM_RESERVATION |
| IT-05 | Approve with wrong hash → 409; wrong password → 401 `REAUTHENTICATION_FAILED` (state unchanged, audited); 6th failure → 429; approve after expiry → 410 + RELEASE |
| IT-06 | Policy change during PENDING → INVALIDATED + RELEASE |
| IT-07 | Checkout → payment INITIATED → fake PayPal order → approveUrl |
| IT-08 | Double checkout (parallel) → one payment, one consume |
| IT-09 | Capture when fake PayPal order not APPROVED → 409 PAYMENT_NOT_APPROVED; no authorize call made |
| IT-10 | Order APPROVED → authorize CREATED → final check PASS → capture COMPLETED → ledger AUTHORIZE + CAPTURE; dashboard remaining 37.00; `PayPal-Request-Id`s distinct per step and reused on retry |
| IT-10b | Merchant blocked after checkout → authorize → final check FAIL → void called → ledger VOID, purchase FAILED (`FINAL_CHECK_FAILED`), committed back to 0 |
| IT-10c | Authorize denied → purchase FAILED, ledger RELEASE, no capture call |
| IT-10d | Capture 5xx then retry → same capture request id, single CAPTURE row |
| IT-10e | `PayPal:Intent=CAPTURE` fallback path → COMPLETED with AUTHORIZE+CAPTURE rows |
| IT-11 | Webhook PAYMENT.CAPTURE.COMPLETED after capture → PROCESSED, no extra ledger rows |
| IT-12 | Webhook with FAILURE verification → 400, REJECTED |
| IT-13 | Duplicate webhook delivery → 200, single row |
| IT-14 | Parallel purchases $45 + $45 vs $80 budget → exactly one succeeds |
| IT-15 | Cross-user: user B GET/POST on user A's cart, purchase, approval, conversation → 404 |
| IT-16 | Blocked merchant cart → evaluate BLOCKED → POST /purchases 422 EVALUATION_BLOCKED |
| IT-17 | Amount mismatch from fake PayPal → AMOUNT_MISMATCH, no CAPTURE |
| IT-18 | Idempotency-Key replay returns identical response; different body → 422 |
| IT-19 | Agent turn with scripted fake LLM performs J1 tool sequence and produces cards |
| IT-20 | Agent red-team scripts (AI_GUARDRAILS.md §6) |
| IT-21 | Webhooks `PAYMENT.AUTHORIZATION.CREATED` / `VOIDED` idempotent and forward-only |
| IT-22 | `/internal/jobs/run` rejects missing/wrong `X-Job-Token` |

## 4. Fakes

- `FakePayPalClient`: scripted order states (`CREATED`→`APPROVED`), authorize outcomes (`CREATED`/denied/422), capture outcomes (`COMPLETED`/`PENDING`/`DECLINED`/5xx), void outcomes; records `PayPal-Request-Id`s to assert reuse.
- `FakeWebhookVerifier`: returns configured status.
- `ScriptedLlmClient`: replays a list of tool calls / texts for deterministic agent tests (CI never calls a real LLM, so it never spends free quota).
- `FlakyLlmClient`: returns 429/5xx to test Gemini → Groq failover and `AGENT_UNAVAILABLE`.
- `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing).

## 5. CI (GitHub Actions)

1. `dotnet build -warnaserror`, `dotnet test` (Testcontainers needs Docker on runner).
2. `npm ci && npm run lint && npm run typecheck && npm test`.
3. `npx playwright test` (against `docker compose` stack, fakes enabled) — P1.
4. gitleaks, `dotnet list package --vulnerable`, `npm audit --omit=dev`.

## 6. Manual Sandbox checklist (pre-demo)

- [ ] OAuth token obtained; create order returns approve/payer-action link.
- [ ] Buyer approves with Sandbox personal account; return URL reached.
- [ ] Authorization CREATED (authorization id stored); final check PASS; capture COMPLETED; capture id stored.
- [ ] Last-mile stop: block merchant mid-checkout → authorization VOIDED in Sandbox.
- [ ] Approval with wrong password rejected; correct password approves.
- [ ] Webhook delivered, verification SUCCESS, PROCESSED.
- [ ] Ledger and dashboard show $37.00 remaining.
- [ ] Blocked merchant scenario works with the live free model (Gemini) 3 times in a row, and once on the Groq fallback.
