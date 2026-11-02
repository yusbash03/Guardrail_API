# Payment State Machine

Three state machines move together: **Purchase**, **ApprovalRequest**, **Payment**. Transitions not listed are illegal and throw `InvalidStateTransition` (→ `409 INVALID_STATE`).

Primary flow uses PayPal intent **`AUTHORIZE`** (ADR-005). The `CAPTURE` fallback is described in §5.

## 1. Purchase

```
                 (REQUIRES_APPROVAL)                                   (ALLOWED)
   create ─────▶ AWAITING_APPROVAL ──approve (password + hash)──▶ READY_FOR_CHECKOUT ◀──── create
                   │  │  │                                           │      │
            reject │  │  │ expire/invalidate                          │      │ cancel / expiry
                   ▼  │  ▼                                           │      ▼
             REJECTED │ EXPIRED                                      │   CANCELLED / EXPIRED
                      │ cancel                                       │ checkout (approval consumed, PayPal order created)
                      ▼                                              ▼
                  CANCELLED                                   CHECKOUT_PENDING ──3h no approval──▶ EXPIRED
                                                                     │   │
                                                                     │   └─ cancel ─▶ CANCELLED
                                          PayPal order APPROVED (GET) │
                                                                     ▼
                                                              PAYER_APPROVED
                                                                     │ POST /orders/{id}/authorize
                                                  ┌──────────────────┼────────────────────┐
                                     authorization CREATED         DENIED / error       (approval reversed)
                                                  ▼                    ▼                    ▼
                                         PAYMENT_AUTHORIZED          FAILED               FAILED
                                           │            │
                         final guardrail check PASS   FAIL ──void──▶ FAILED (failure_code = FINAL_CHECK_FAILED)
                                           │
                               POST /authorizations/{id}/capture
                          ┌────────────────┼──────────────────┐
                      COMPLETED         PENDING            DECLINED ──void──▶ FAILED
                          ▼                ▼
                      COMPLETED     CAPTURE_PENDING ──webhook/reconciler──▶ COMPLETED | FAILED
                          │
                     refund (P2)
                          ▼
            PARTIALLY_REFUNDED ─▶ REFUNDED
```

| From | To | Trigger | Ledger | Side effects |
|---|---|---|---|---|
| — | AWAITING_APPROVAL | CreatePurchase (REQUIRES_APPROVAL) | PROPOSE | cart LOCKED; ApprovalRequest PENDING |
| — | READY_FOR_CHECKOUT | CreatePurchase (ALLOWED) | RESERVE | cart LOCKED; `checkout_expires_at = now + 30 min` |
| AWAITING_APPROVAL | READY_FOR_CHECKOUT | Approval APPROVED (password step-up passed) | CONFIRM_RESERVATION | — |
| AWAITING_APPROVAL | REJECTED | Approval REJECTED | RELEASE | cart OPEN, version++ |
| AWAITING_APPROVAL | EXPIRED | Sweeper | RELEASE | approval EXPIRED; cart OPEN, version++ |
| AWAITING_APPROVAL / READY_FOR_CHECKOUT | CANCELLED | user cancel; policy change (BR-13) | RELEASE | approval INVALIDATED; cart OPEN, version++ |
| READY_FOR_CHECKOUT | EXPIRED | approval `expires_at` (or `checkout_expires_at` if no approval) passes | RELEASE | cart OPEN, version++ |
| READY_FOR_CHECKOUT | CHECKOUT_PENDING | Checkout | — | approval CONSUMED; Payment INITIATED → CREATED/PAYER_ACTION_REQUIRED after PayPal responds; `checkout_expires_at = now + 3h` |
| CHECKOUT_PENDING | PAYER_APPROVED | GET order = APPROVED, or verified `CHECKOUT.ORDER.APPROVED` | — | payment APPROVED |
| CHECKOUT_PENDING | EXPIRED | 3 h without approval | RELEASE | payment VOIDED (internal); cart ABANDONED |
| CHECKOUT_PENDING | CANCELLED | user cancel | RELEASE | payment VOIDED (internal); cart ABANDONED |
| PAYER_APPROVED | PAYMENT_AUTHORIZED | authorize response with authorization `CREATED`, or verified `PAYMENT.AUTHORIZATION.CREATED` | AUTHORIZE | payment AUTHORIZED; `paypal_authorization_id` |
| PAYER_APPROVED | FAILED | authorize denied/failed; approval reversed | RELEASE | payment FAILED; cart ABANDONED |
| PAYMENT_AUTHORIZED | FAILED | final guardrail check fails → void | VOID | payment VOIDED; `failure_code = FINAL_CHECK_FAILED`; cart ABANDONED |
| PAYMENT_AUTHORIZED | COMPLETED | capture COMPLETED | CAPTURE | payment COMPLETED; cart CHECKED_OUT |
| PAYMENT_AUTHORIZED | CAPTURE_PENDING | capture PENDING | — | payment CAPTURE_PENDING |
| PAYMENT_AUTHORIZED / CAPTURE_PENDING | FAILED | capture DECLINED/FAILED (then void remaining authorization) | FAIL | payment DECLINED/FAILED; cart ABANDONED |
| PAYMENT_AUTHORIZED | FAILED | authorization voided/expired externally (verified `PAYMENT.AUTHORIZATION.VOIDED`) | VOID | payment VOIDED |
| CAPTURE_PENDING | COMPLETED | verified webhook / reconciler | CAPTURE | payment COMPLETED |
| COMPLETED | PARTIALLY_REFUNDED / REFUNDED | verified refund webhook (P2) | REFUND | payment refunded_amount |

Terminal: `REJECTED`, `CANCELLED`, `EXPIRED`, `FAILED`, `REFUNDED`. (`COMPLETED` is terminal except for refunds.)

Re-checkout from `CHECKOUT_PENDING`: returning from the cancel URL and clicking *Pay with PayPal* again returns the **existing** payment's `approve_url` (same PayPal order) — no new order, no second approval consumption.

### Final guardrail check (between authorize and capture)

Pure function over database state, run inside a transaction holding the purchase row lock:

| Check | Fails when | Reason |
|---|---|---|
| Snapshot integrity | recomputed cart hash ≠ `purchase.cart_snapshot_hash` | `CART_CHANGED` |
| Amount integrity | authorization amount/currency ≠ purchase | `AMOUNT_MISMATCH` |
| Merchant still permitted | merchant now in blocked list (current policy) | `MERCHANT_BLOCKED` |
| Products still permitted | any item now matches a `BLOCK` product restriction (current policy) | `PRODUCT_RESTRICTED` |

Budget is **not** re-checked here: it was reserved under lock at purchase creation and remains committed. Failing the final check voids the authorization; the buyer is not charged. This is the demo's "last-mile stop" (DEMO_SCENARIO.md, optional beat).

## 2. ApprovalRequest

| From | To | Trigger |
|---|---|---|
| — | PENDING | CreatePurchase with REQUIRES_APPROVAL |
| PENDING | APPROVED | user approve (password verified, hash match, not expired, policy version current) |
| PENDING | REJECTED | user reject |
| PENDING / APPROVED | EXPIRED | `now ≥ expires_at` (sweeper or lazy check) |
| PENDING / APPROVED | INVALIDATED | cart changed, purchase cancelled, policy version changed |
| APPROVED | CONSUMED | checkout (atomic conditional update) |

A failed password attempt changes nothing on the approval (it stays `PENDING`); see APPROVAL_WORKFLOW.md §4.

Atomic consume:
```sql
update approval_requests
   set status = 'CONSUMED', consumed_at = @now, updated_at = @now
 where id = @id and user_id = @user and status = 'APPROVED'
   and expires_at > @now and consumed_at is null
   and cart_snapshot_hash = @currentCartHash and amount = @purchaseAmount
returning id;
-- 0 rows → reject checkout (APPROVAL_EXPIRED / APPROVAL_INVALIDATED / APPROVAL_NOT_APPROVED, determined by a follow-up read)
```

## 3. Payment (internal, mirrors PayPal)

| Status | Meaning | Source |
|---|---|---|
| INITIATED | Row created and approval consumed in our DB; PayPal create-order call not yet confirmed. Retries reuse `paypal_create_request_id` | checkout tx 1 |
| CREATED | Order created, no payment source (`approve` link) | create response |
| PAYER_ACTION_REQUIRED | Order created with payment_source (`payer-action` link) | create response |
| APPROVED | Buyer approved the order; verified via GET order or webhook | GET order / webhook |
| AUTHORIZED | PayPal authorization exists (funds held) | authorize response / `PAYMENT.AUTHORIZATION.CREATED` |
| CAPTURE_PENDING | Capture accepted but pending | capture response / webhook |
| COMPLETED | Captured | capture response / webhook / reconciler |
| DECLINED | Authorization or capture declined | response / webhook |
| FAILED | Other failure (incl. approval reversed) | various |
| VOIDED | Authorization voided (final check failed, capture declined, expired) **or** order abandoned before approval (internal) | void response / webhook / sweeper |
| PARTIALLY_REFUNDED / REFUNDED / REVERSED | Post-capture (P2) | webhook |

PayPal order statuses (`CREATED`, `SAVED`, `APPROVED`, `VOIDED`, `COMPLETED`, `PAYER_ACTION_REQUIRED`) and authorization/capture statuses are stored raw in `last_paypal_response` and mapped. Authorization status values (expected: `CREATED`, `CAPTURED`, `DENIED`, `PARTIALLY_CAPTURED`, `VOIDED`, `PENDING`) **[VERIFY]** against the Payments v2 reference.

## 4. Forward-only rule

Webhook/reconciler updates may only move **forward** in rank: INITIATED (0) < CREATED/PAYER_ACTION_REQUIRED (1) < APPROVED (2) < AUTHORIZED (3) < CAPTURE_PENDING (4) < COMPLETED/DECLINED/FAILED/VOIDED (5) < PARTIALLY_REFUNDED (6) < REFUNDED/REVERSED (7).

## 5. `CAPTURE` fallback (`PayPal:Intent=CAPTURE`)

Only if the Day-1 Sandbox spike shows authorization is unavailable. Differences: no `PAYMENT_AUTHORIZED` state and no final void window; `PAYER_APPROVED` writes ledger `AUTHORIZE` (buyer approval = authorized), then `POST /v2/checkout/orders/{id}/capture` → `COMPLETED` / `CAPTURE_PENDING` / `FAILED` (ledger `FAIL`). The final guardrail check runs **before** the capture call and, on failure, the purchase is `FAILED` with ledger `VOID` (AUTHORIZED → RELEASED; the order is simply never captured).

## 6. Invariants (tested)

1. A purchase in `COMPLETED` has exactly one CAPTURE ledger row per category and a payment `COMPLETED` with matching `captured_amount`.
2. A purchase in a terminal non-completed status has committed amount 0 in the ledger.
3. No payment exists for a purchase that never reached `CHECKOUT_PENDING`.
4. No approval is `CONSUMED` without a payment row (status `INITIATED`) inserted in the same database transaction. PayPal calls happen **after** that commit (never inside a DB transaction); see PURCHASE_AUTHORIZATION.md §3.
5. No capture call is made unless the final guardrail check passed in the same request and its result is stored in `payments.final_check_result`.
6. Every `VOIDED` payment with an authorization id has a successful void response or a verified `PAYMENT.AUTHORIZATION.VOIDED` event.
