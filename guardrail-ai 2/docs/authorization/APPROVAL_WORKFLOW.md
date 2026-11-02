# Approval Workflow

## 1. Purpose

When the policy engine returns `REQUIRES_APPROVAL`, a human must approve **this exact cart** before any payment can start. The approval is a narrowly-scoped, single-use, expiring permission.

## 2. Binding

| Bound field | Source | Checked at |
|---|---|---|
| `user_id` | JWT | approve, reject, consume |
| `purchase_id` | created with the approval | all |
| `cart_id`, `cart_version` | locked cart | approve, consume |
| `cart_snapshot_hash` | SHA-256 of canonical cart snapshot (ADR-010) | approve (client echo), consume (recomputed) |
| `amount`, `currency` | cart snapshot | consume (vs purchase and recomputed cart) |
| `merchant_id` | cart | consume |
| `policy_version_id` | evaluation | approve (must equal current version) |
| `policy_evaluation_id`, `reasons` | evaluation | display |
| `expires_at` | `created_at + Approvals:TtlMinutes` (default 30) | approve, consume |

### Canonical snapshot (hash input)

```json
{"cartId":"…","cartVersion":3,"currency":"USD","items":[{"categoryCode":"GROCERIES","lineTotal":"4.29","productId":"…","quantity":1,"sku":"FM-MLK-1G","unitPrice":"4.29"}],"merchantId":"…","total":"43.00"}
```
Rules: keys sorted ordinally at every level; items sorted by `productId`; no whitespace; amounts as fixed 2-dp strings; UTF-8; lower-case hex digest. A golden-file test pins the hash of a fixed cart so frontend/backend/test fixtures never drift.

## 3. Lifecycle

```
CreatePurchase(REQUIRES_APPROVAL) ─▶ PENDING ──approve──▶ APPROVED ──checkout──▶ CONSUMED
                                       │ │ │                 │ │
                                reject │ │ │expire     expire│ │invalidate
                                       ▼ │ ▼                 ▼ ▼
                                 REJECTED │ EXPIRED      EXPIRED INVALIDATED
                                          └─invalidate─▶ INVALIDATED
```

## 4. Approve

```
POST /api/v1/approvals/{approvalId}/approve
Authorization: Bearer <user JWT>
X-Guardrail-Client: web
Content-Type: application/json

{ "cartSnapshotHash": "9f2c…e1", "password": "<current password>" }
```

**Step-up (ADR-015), before the transaction:**
0a. Step-up rate limit: ≥ 5 failed approval step-ups for this user in the last 15 min → `429 APPROVAL_STEP_UP_LOCKED` (`Retry-After`).
0b. Verify `password` against the user's hash (constant-time via Identity hasher). Wrong → `401 REAUTHENTICATION_FAILED`, audit `APPROVAL_STEP_UP_FAILED`, failure counted toward SEC-05 lockout. The approval is **not** changed. The access token stays valid (the client must not treat this 401 as a session expiry — it carries `code`, see ERROR_HANDLING.md).
The `password` field is excluded from request logging and never persisted.

Server steps (one transaction, approval row `FOR UPDATE`):
1. Load approval by id **and** `user_id` → else `404 NOT_FOUND`.
2. `status == PENDING` → else `409 APPROVAL_NOT_PENDING` (body includes current status).
3. `now < expires_at` → else mark `EXPIRED` (+ release) and return `410 APPROVAL_EXPIRED`.
4. `body.cartSnapshotHash == approval.cart_snapshot_hash` → else `409 APPROVAL_SNAPSHOT_MISMATCH`.
5. Recompute hash from the locked cart; must equal stored hash → else invalidate, `409 APPROVAL_INVALIDATED` (`CART_CHANGED`).
6. `profile.current_policy_version_id == approval.policy_version_id` → else invalidate, `409 APPROVAL_INVALIDATED` (`POLICY_VERSION_CHANGED`).
7. Set `APPROVED`, `decided_at`; purchase `READY_FOR_CHECKOUT`; ledger `CONFIRM_RESERVATION`; audit `APPROVAL_GRANTED`.
8. Response `200` with approval + purchase.

## 5. Reject

`POST /api/v1/approvals/{id}/reject` `{ "reason": "Too expensive" }` (reason optional, ≤ 200 chars).
→ `REJECTED`; purchase `REJECTED`; ledger `RELEASE`; cart `OPEN` + `version++`; audit `APPROVAL_REJECTED`.

## 6. Expiry

- `ApprovalExpirySweeper` (60 s) expires `PENDING`/`APPROVED` approvals past `expires_at`, sets purchase `EXPIRED`, writes `RELEASE`, unlocks cart.
- Every read/approve/consume also checks expiry lazily, so correctness does not depend on the sweeper timing.

## 7. Invalidation triggers

| Trigger | Reason code |
|---|---|
| User cancels the purchase / chooses "Edit cart" | `PURCHASE_CANCELLED` |
| Policy version changes (BR-13) | `POLICY_VERSION_CHANGED` |
| Recomputed cart hash differs (should be impossible while locked; defence in depth) | `CART_CHANGED` |
| Product becomes unavailable before checkout | `PRODUCT_UNAVAILABLE` |

## 8. Non-reuse

- Single-use via atomic conditional update to `CONSUMED` (PAYMENT_STATE_MACHINE.md §2).
- `purchase_id` is unique on `approval_requests` → one approval per purchase.
- `(cart_id, cart_version)` is unique on `purchases` → one purchase per cart version. Changing the cart creates a new version, a new purchase and a new approval.

## 9. UI requirements

The approval card/page must show: merchant, every line item with quantity and price, total, currency, policy reasons (with codes), budget impact (before → after), expiry countdown, and the first 8 chars of the snapshot hash ("Approval fingerprint"). Buttons: **Approve $43.00** (primary) and **Reject**. **Approve** opens a confirmation dialog: "Confirm with your password to approve $43.00 at FreshMart" (password input, `autocomplete="current-password"`). The approve request sends the hash that the rendered card was built from (from `GET /approvals/{id}`), never a hash recomputed client-side. The password is cleared from component state immediately after the request.

## 10. Acceptance criteria

- [ ] Approve with wrong password → 401 `REAUTHENTICATION_FAILED`, state unchanged; 6th attempt in 15 min → 429.
- [ ] Approve without password field → 400 `VALIDATION_FAILED`.
- [ ] Approve with wrong hash → 409, state unchanged.
- [ ] Approve after expiry → 410, reservation released.
- [ ] Approve twice → second call 409 `APPROVAL_NOT_PENDING`.
- [ ] Policy edit while pending → approval INVALIDATED, budget released.
- [ ] Checkout twice concurrently → exactly one payment row; other request gets the existing payment (idempotent) or 409.
- [ ] Another user's approval id → 404.
