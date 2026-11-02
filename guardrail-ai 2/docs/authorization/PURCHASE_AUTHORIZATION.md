# Purchase Authorization

How a cart becomes money movement, and every check on the way. Two meanings of "authorization" appear here: **Guardrail authorization** (permission to start a PayPal checkout) and **PayPal authorization** (funds held via Orders v2 intent `AUTHORIZE`, ADR-005).

## 1. Gates

```
Cart (OPEN)
  │  Gate 1: Evaluation  — PolicyEngine on DB data            → BLOCKED stops here
  ▼
Purchase created        — Gate 2: Re-evaluation under per-user lock, reservation
  │
  ├─ REQUIRES_APPROVAL → Gate 3: Human approval bound to snapshot + password step-up (ADR-015)
  ▼
READY_FOR_CHECKOUT
  │  Gate 4: Checkout authorization (consume approval, re-verify cart/amount/policy/expiry)
  ▼
PayPal order created     — Gate 5: Buyer approves in PayPal (PayPal-side)
  │
  │  Gate 6: Backend verifies order APPROVED via PayPal API, amount/currency/custom_id match
  ▼
PayPal authorization     — funds held; ledger AUTHORIZED
  │  Gate 7: Final guardrail check (snapshot integrity, merchant/product still permitted) — fail → void
  ▼
Capture (server-side)    — Gate 8: Capture result or verified webhook decides CAPTURED
```

No gate can be satisfied by the AI or by the frontend alone.

## 2. Create purchase — `POST /api/v1/purchases`

Request: `{ "cartId": "…", "evaluationId": "…" }`, header `Idempotency-Key`.

Transaction:
1. `SELECT … FROM spending_profiles WHERE user_id=@u FOR UPDATE`.
2. Load cart (owned, `OPEN`) `FOR UPDATE`. Load evaluation (owned, same cart id+version, `< 15 min` old, policy version == current).
   - else `409 EVALUATION_STALE` / `410 EVALUATION_EXPIRED`.
3. Rebuild CartSnapshot and SpendingSnapshot; `PolicyEngine.Evaluate`.
   - `BLOCKED` → `422 EVALUATION_BLOCKED` (with reasons).
   - decision or snapshot hash ≠ referenced evaluation → `409 EVALUATION_STALE` (persist the new evaluation and return its id).
4. Lock cart. Insert purchase (`budget_week_start`, `budget_month_start` computed from now in profile tz).
5. If `REQUIRES_APPROVAL`: insert ApprovalRequest, ledger `PROPOSE` per category, status `AWAITING_APPROVAL`.
   Else: ledger `RESERVE`, status `READY_FOR_CHECKOUT`, `checkout_expires_at = now + 30 min`.
6. Audit `PURCHASE_CREATED` (+ `APPROVAL_REQUESTED`). Commit.

## 3. Checkout — `POST /api/v1/purchases/{id}/checkout`

Header `Idempotency-Key`. Two DB transactions around one PayPal call (never hold a DB transaction across network I/O).

**Tx 1 — authorize**
1. Load purchase (owned) `FOR UPDATE`.
2. If status `CHECKOUT_PENDING` and a payment with `approve_url` exists → return it (idempotent re-checkout). If payment `INITIATED` without order id → skip to PayPal call with the same `paypal_create_request_id`.
3. Require `READY_FOR_CHECKOUT` → else `409 INVALID_STATE`.
4. Recompute cart hash from locked cart; must equal `purchase.cart_snapshot_hash` → else `409 CART_CHANGED`.
5. Re-check products still available → else cancel + release, `409 PRODUCT_UNAVAILABLE`.
6. If an approval exists: atomic consume (PAYMENT_STATE_MACHINE.md §2). 0 rows → read status → `410 APPROVAL_EXPIRED` | `409 APPROVAL_INVALIDATED` | `409 APPROVAL_NOT_APPROVED`.
   If no approval: require `now < checkout_expires_at` and `purchase.policy_version_id == current` → else expire/cancel and `409 EVALUATION_STALE`.
7. Insert payment (`INITIATED`, new `paypal_create_request_id`, new `paypal_capture_request_id`), purchase `CHECKOUT_PENDING`, `checkout_expires_at = now + 3 h`. Audit `CHECKOUT_STARTED`. Commit.

**PayPal call** — `POST /v2/checkout/orders` (intent `AUTHORIZE`) with `PayPal-Request-Id = paypal_create_request_id`, amounts from the **purchase/cart snapshot rows**.

**Tx 2 — record**
- Success: payment `CREATED`/`PAYER_ACTION_REQUIRED`, `paypal_order_id`, `approve_url`. Response `200 { approveUrl, paypalOrderId, purchaseStatus }`.
- PayPal 5xx/timeout: payment stays `INITIATED`; return `503 PAYMENT_PROVIDER_UNAVAILABLE` (client retries the same endpoint; same request id → no duplicate order).
- PayPal 4xx: payment `FAILED` with debug id; purchase stays `CHECKOUT_PENDING` without active payment; a retry creates a new payment row with a new request id (approval is not re-consumed: it is already bound to this purchase). After 3 failed creates → purchase `FAILED`, `RELEASE`.

## 4. Capture — `POST /api/v1/purchases/{id}/capture`

See PAYPAL_INTEGRATION.md §6. Summary of checks:
- Owner; status `CHECKOUT_PENDING` / `PAYER_APPROVED` / `PAYMENT_AUTHORIZED` / `CAPTURE_PENDING` / `COMPLETED`.
- PayPal `GET` order: `status = APPROVED` (or later → reconcile), amount/currency/`custom_id` equal to purchase.
- Authorize with `paypal_authorize_request_id`; verify authorization amount/currency.
- Final guardrail check (PAYMENT_STATE_MACHINE.md §1) — fail → void with `paypal_void_request_id`.
- Capture the authorization with `paypal_capture_request_id`.

The capture endpoint is user-triggered (on return from PayPal), but its outcome depends only on PayPal's server responses. A malicious client calling it early gets `409 PAYMENT_NOT_APPROVED`.

## 5. Cancel — `POST /api/v1/purchases/{id}/cancel`

Allowed in `AWAITING_APPROVAL`, `READY_FOR_CHECKOUT`, `CHECKOUT_PENDING` (before payer approval). → `CANCELLED`, `RELEASE`, approval `INVALIDATED`, cart `OPEN`+`version++` (or `ABANDONED` if a PayPal order existed). `PAYER_APPROVED` and later cannot be cancelled in MVP.

## 6. Authorization matrix

| Action | User (web) | Agent tool | System job | PayPal webhook |
|---|---|---|---|---|
| Create cart | ✅ | ✅ | — | — |
| Evaluate | ✅ | ✅ | — | — |
| Create purchase / approval request | ✅ | ✅ | — | — |
| Approve (password step-up) / reject | ✅ | ❌ | — | — |
| Checkout (create PayPal order) | ✅ | ❌ | — | — |
| Authorize / final check / capture / void | ✅ (trigger only) | ❌ | ✅ reconciler | — |
| Mark captured | — | ❌ | ✅ (from PayPal API) | ✅ (verified) |
| Expire / release | — | ❌ | ✅ | — |
| Edit policy / merchants | ✅ | ❌ | — | — |
