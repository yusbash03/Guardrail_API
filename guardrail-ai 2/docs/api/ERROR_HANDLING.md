# Error Handling

## 1. Format — RFC 9457 Problem Details

```json
{
  "type": "https://guardrail.ai/errors/approval-expired",
  "title": "Approval expired",
  "status": 410,
  "detail": "This approval expired at 14:35 UTC. Ask the assistant to prepare the cart again.",
  "instance": "/api/v1/approvals/0193…/approve",
  "code": "APPROVAL_EXPIRED",
  "correlationId": "01JC…",
  "errors": null,
  "data": { "expiredAt": "2026-11-03T14:35:11Z" }
}
```
- `code` is stable and machine-readable; clients switch on `code`, never on `title`/`detail`.
- `errors` (validation only): `{ "items[0].quantity": ["Must be between 1 and 99"] }`.
- `data` carries safe structured context (e.g. reasons for `EVALUATION_BLOCKED`).
- 5xx responses never include stack traces or provider payloads; `correlationId` links to logs.

## 2. Error catalogue

| Code | HTTP | Meaning |
|---|---|---|
| `VALIDATION_FAILED` | 400 | Request failed validation |
| `UNAUTHENTICATED` | 401 | Missing/invalid/expired token |
| `REAUTHENTICATION_FAILED` | 401 | Wrong password on approval step-up (session still valid — client must **not** refresh/logout) |
| `FORBIDDEN` | 403 | Authenticated but not allowed (e.g. non-admin) |
| `NOT_FOUND` | 404 | Not found **or not owned** (no existence leak) |
| `INVALID_STATE` | 409 | Illegal state transition |
| `POLICY_VERSION_CONFLICT` | 409 | `expectedVersionNumber` stale |
| `CART_LOCKED` | 409 | Cart has an active purchase |
| `CART_NOT_OPEN` | 409 | Cart locked/checked out/abandoned |
| `CART_CHANGED` | 409 | Cart hash differs from purchase/approval |
| `EVALUATION_STALE` | 409 | Re-evaluation differs; `data.newEvaluationId` provided |
| `APPROVAL_NOT_PENDING` | 409 | Approve/reject on non-PENDING approval |
| `APPROVAL_NOT_APPROVED` | 409 | Checkout with approval not APPROVED |
| `APPROVAL_SNAPSHOT_MISMATCH` | 409 | Client hash ≠ approval hash |
| `APPROVAL_INVALIDATED` | 409 | `data.reason` ∈ `CART_CHANGED`, `POLICY_VERSION_CHANGED`, `PURCHASE_CANCELLED`, `PRODUCT_UNAVAILABLE` |
| `PAYMENT_NOT_APPROVED` | 409 | Capture before PayPal buyer approval |
| `IDEMPOTENCY_IN_PROGRESS` | 409 | Same key still processing |
| `APPROVAL_EXPIRED` | 410 | Approval expired |
| `EVALUATION_EXPIRED` | 410 | Evaluation older than 15 min |
| `CART_VERSION_MISMATCH` | 412 | `If-Match` mismatch |
| `POLICY_INVALID` | 422 | Policy document violates rules |
| `POLICY_MERCHANT_CONFLICT` | 422 | Merchant both approved and blocked |
| `CURRENCY_MISMATCH` | 422 | Currency differs from profile |
| `EVALUATION_BLOCKED` | 422 | Purchase attempted from BLOCKED decision; `data.reasons` |
| `NO_ACTIVE_POLICY` | 422 | No policy version |
| `MIXED_MERCHANT_CART` | 422 | Product from another merchant |
| `PRODUCT_NOT_FOUND` | 422 | Product id unknown (in body) |
| `PRODUCT_UNAVAILABLE` | 422 / 409 | Not available / out of stock |
| `CART_LIMIT_EXCEEDED` | 422 | > 30 items |
| `IDEMPOTENCY_KEY_REUSED` | 422 | Same key, different request |
| `PAYMENT_INSTRUMENT_DECLINED` | 422 | Buyer must choose another funding source; `data.retryUrl` |
| `AMOUNT_MISMATCH` | 422 | PayPal amount/currency ≠ purchase (flagged for review) |
| `FINAL_CHECK_FAILED` | 422 | Pre-capture guardrail check failed; PayPal authorization voided; `data.reasons` |
| `RATE_LIMITED` | 429 | Too many requests |
| `APPROVAL_STEP_UP_LOCKED` | 429 | Too many failed approval password attempts |
| `CAPTURE_IN_PROGRESS` | 202 | Not an error; capture outcome pending |
| `PAYMENT_PROVIDER_UNAVAILABLE` | 503 | PayPal unreachable; safe to retry same request |
| `AGENT_UNAVAILABLE` | 503 | Primary and fallback LLM providers unreachable or out of free quota; `Retry-After` set |
| `AGENT_STEP_LIMIT` | 200 (in message) | Agent hit tool-call cap |
| `INTERNAL_ERROR` | 500 | Unexpected |

Agent tool errors reuse these codes plus `UNKNOWN_TOOL`.

## 3. Backend mechanics

- Use cases return `Result<T>`; endpoint filter maps `DomainError.Code` → HTTP status via a single table (`ErrorCodeMap`).
- Global exception middleware: `ValidationException` → 400; `DbUpdateConcurrencyException` → 409 `INVALID_STATE`; `PostgresException 23505` on known constraints → mapped code or 409; anything else → 500 `INTERNAL_ERROR` + Error log.
- PayPal client errors are wrapped in `PayPalApiException(status, name, debugId, issues[])`; never surfaced raw.
- Transient faults (PayPal 5xx/429, LLM 429/5xx, Npgsql transient) use `Microsoft.Extensions.Http.Resilience` (retry with jitter, max 2, circuit breaker). **POSTs to PayPal are only retried with the same `PayPal-Request-Id`.**

## 4. Frontend mechanics

- `apiClient` parses ProblemDetails with Zod; throws `ApiError { code, status, detail, data }`.
- 401 with `code = UNAUTHENTICATED` → single-flight refresh, replay once; refresh failure → logout to `/login`. 401 `REAUTHENTICATION_FAILED` → inline "Incorrect password" in the approval dialog (no refresh).
- Toasts for transient errors; inline banners for state errors (e.g. approval expired shows "Rebuild cart" CTA).
- Mapping table `errorCopy[code]` provides user-facing text; fallback to `detail`.
- Money-moving buttons disable on click and show progress; retries reuse the same `Idempotency-Key` (generated once per user intent, stored in component state).

## 5. Logging levels

| Situation | Level |
|---|---|
| Validation, 404, policy BLOCKED | Information |
| Rejected webhook, agent tool rejected, rate limited | Warning |
| AMOUNT_MISMATCH, ledger invalid transition, webhook processing FAILED, 5xx | Error |
