# User Journeys

Each journey lists steps, system actors, and the observable outcome. Step numbers in **J1** match DEMO_SCENARIO.md.

## J0 — Onboarding

1. User registers → system creates `User`, `SpendingProfile` (USD, browser time zone), and policy version 1 with safe defaults:
   - new merchant mode `BLOCK`, approval threshold `$0.00` (everything needs approval), no budgets, `UNBUDGETED_CATEGORY` = `REQUIRE_APPROVAL`.
2. User lands on Spending Rules with a guided "set your first rules" panel.

## J1 — Core happy path (the demo)

| # | User action | System behaviour |
|---|---|---|
| 1 | Sets Groceries budget $80 weekly | `PUT /policies` → policy v2, audit `POLICY_UPDATED` |
| 2 | Approves FreshMart and GreenGrocer Co. | `POST /merchants/{id}/approve` ×2 → v3, v4 |
| 3 | Sets approval threshold $30 | v5 |
| 4 | Types "Get my groceries for the week — milk, eggs, bread, chicken, rice, vegetables, keep it under budget" | Agent turn begins |
| 5 | — | Agent calls `GetRemainingBudget(GROCERIES)`, `GetApprovedMerchants`, `SearchProducts` (several) |
| 6 | — | Agent calls `CreateShoppingCart` at FreshMart; cart total $43.00 |
| 7 | — | Agent calls `EvaluatePurchase(cartId)` → `REQUIRES_APPROVAL` (`APPROVAL_THRESHOLD_EXCEEDED`), remaining after $37.00 |
| 8 | — | Agent calls `RequestPurchaseApproval` → Purchase `AWAITING_APPROVAL`, ApprovalRequest `PENDING`, $43 `RESERVED` in ledger. Approval card appears |
| 9 | Reviews cart, taps **Approve $43.00**, confirms with password | `POST /approvals/{id}/approve` with `cartSnapshotHash` + `password` → step-up verified → `APPROVED`; Purchase `READY_FOR_CHECKOUT` |
| 10 | Taps **Pay with PayPal** | `POST /purchases/{id}/checkout` → approval `CONSUMED`, PayPal order (intent `AUTHORIZE`) created, redirect to PayPal approve link |
| 11 | Logs in as Sandbox buyer and approves | PayPal redirects to `/checkout/return?purchaseId=…` |
| 12 | — | Frontend calls `POST /purchases/{id}/capture`; backend verifies order `APPROVED` with PayPal → authorizes (ledger `RESERVED`→`AUTHORIZED`) → final guardrail check passes → captures the authorization (ledger `AUTHORIZED`→`CAPTURED`). Webhooks `PAYMENT.AUTHORIZATION.CREATED` and `PAYMENT.CAPTURE.COMPLETED` arrive, are verified, matched, and marked processed (idempotent) |
| 13 | Sees "Payment confirmed" | Dashboard: Groceries $43 / $80 |
| 14 | Asks "How much grocery budget do I have left?" | Agent calls `GetRemainingBudget` → "$37.00 left this week (resets Monday)" |

## J2 — Blocked merchant

1. User: "Order snacks from QuickCart Express."
2. Agent searches, creates a cart at QuickCart Express (not approved), calls `EvaluatePurchase`.
3. Engine → `BLOCKED`, `MERCHANT_NOT_APPROVED`.
4. Agent explains and suggests an approved alternative merchant. `RequestPurchaseApproval` on a blocked evaluation returns `EVALUATION_BLOCKED`; no purchase is created.
5. The user may approve the merchant on the Merchants page (a human action), then ask again.

## J3 — Cart changed after approval

1. Approval granted for cart hash `H1`.
2. User (or agent) attempts to add an item → API returns `CART_LOCKED` (409).
3. User chooses **Edit cart** → purchase cancelled, reservation released, approval `INVALIDATED`, cart unlocked as version n+1.
4. New evaluation and new approval are required.

## J4 — Approval expires

1. Approval `PENDING` for 30 minutes without action → background sweeper marks `EXPIRED`, purchase `EXPIRED`, reservation `RELEASED`.
2. Approve attempt after expiry → `APPROVAL_EXPIRED` (410).

## J5 — Buyer cancels at PayPal

1. Buyer clicks cancel → PayPal redirects to `cancel_url` → `/checkout/cancel?purchaseId=…`.
2. Purchase remains `CHECKOUT_PENDING`; user can retry checkout (re-uses the same PayPal order while it is still `CREATED`) or cancel the purchase (releases reservation).

## J6 — Payment fails or is pending

- Capture returns `DECLINED` → Payment `DECLINED`, Purchase `FAILED`, ledger `FAILED` + reservation released.
- Capture returns `PENDING` → Purchase `CAPTURE_PENDING`; amount stays `AUTHORIZED` (counts toward budget) until a verified webhook moves it to `CAPTURED` or `FAILED`.
- Authorization denied → Purchase `FAILED`, ledger `RELEASE`.
- Wrong password at approval → nothing changes; after 5 failures in 15 min the approve button is locked with a countdown.

## J6b — Last-mile stop (final guardrail check)

1. Purchase is `CHECKOUT_PENDING`; the buyer is on PayPal.
2. Meanwhile the user blocks FreshMart on the Merchants page (policy v6).
3. Buyer approves in PayPal and returns. Backend authorizes ($43 held), runs the final check → `MERCHANT_BLOCKED` → voids the authorization.
4. Purchase `FAILED` (`FINAL_CHECK_FAILED`), ledger `VOID`, buyer not charged; UI explains why.

## J7 — Refund (P2)

- Admin/demo refund in PayPal Sandbox → verified `PAYMENT.CAPTURE.REFUNDED` → ledger `REFUNDED` entry, budget restored for the original period.

## J8 — Policy changes during a pending approval

- Policy version changes from v5 to v6 while approval is `PENDING` → approval is `INVALIDATED` at approve-time or checkout-time with `POLICY_VERSION_CHANGED`; user must re-evaluate.
