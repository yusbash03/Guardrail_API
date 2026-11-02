# Spending Policy Engine

The engine is the **final authority** on whether a purchase may proceed. It is a pure, deterministic function in `Guardrail.Domain.Policy`:

```csharp
public static PolicyDecision Evaluate(
    PolicySnapshot policy,
    CartSnapshot cart,
    SpendingSnapshot spending,
    DateTimeOffset now);
```

- No I/O, no clock reads, no randomness, no LLM.
- Same inputs → byte-identical output (reasons sorted deterministically).
- Called by `EvaluateCartUseCase` (persisted as `PolicyEvaluation`) and again inside the purchase-creation transaction (ARCHITECTURE.md §5.3).

## 1. Inputs

### 1.1 PolicySnapshot
Built from the user's current `PolicyVersion`. Exposed to users and the API as a **PolicyDocument**:

```json
{
  "policyVersionId": "0193…",
  "versionNumber": 5,
  "currency": "USD",
  "timeZone": "America/New_York",
  "approvalThreshold": { "amount": "30.00" },
  "maxTransactionAmount": { "amount": "150.00" },
  "spendingLimits": [
    { "period": "WEEKLY",  "limit": "200.00" },
    { "period": "MONTHLY", "limit": "600.00" }
  ],
  "categoryBudgets": [
    { "categoryCode": "GROCERIES", "period": "WEEKLY",  "limit": "80.00" },
    { "categoryCode": "HOUSEHOLD", "period": "MONTHLY", "limit": "50.00" }
  ],
  "merchantRules": {
    "approvedMerchantIds": ["<FreshMart>", "<GreenGrocer Co.>"],
    "blockedMerchantIds": ["<TechNest>"],
    "newMerchantAction": "BLOCK"
  },
  "productRestrictions": [
    { "id": "pr_1", "match": { "type": "CATEGORY", "value": "ALCOHOL" }, "action": "BLOCK", "note": "Never buy alcohol" },
    { "id": "pr_2", "match": { "type": "TAG", "value": "gift-card" }, "action": "BLOCK", "note": null }
  ],
  "duplicatePurchase": { "enabled": true, "windowHours": 24, "scope": "SAME_PRODUCT", "action": "REQUIRE_APPROVAL" },
  "timeRestrictions": [
    { "id": "tr_1", "daysOfWeek": ["MON","TUE","WED","THU","FRI","SAT","SUN"], "startLocalTime": "23:00", "endLocalTime": "06:00", "action": "BLOCK" }
  ],
  "unbudgetedCategoryAction": "REQUIRE_APPROVAL"
}
```

Mapping to `spending_policies` rows (one row per rule):

| Document field | rule_type | Column / `parameters` |
|---|---|---|
| `approvalThreshold` | `APPROVAL_THRESHOLD` | `{ "amount": "30.00" }` |
| `maxTransactionAmount` | `MAX_TRANSACTION_AMOUNT` | `{ "amount": "150.00" }` |
| `spendingLimits[]` | `SPENDING_LIMIT` | `{ "period": "WEEKLY", "limit": "200.00" }` |
| `categoryBudgets[]` | `CATEGORY_BUDGET` | `category_code`; `{ "period", "limit" }` |
| `approvedMerchantIds[]` | `MERCHANT_ALLOW` | `merchant_id` |
| `blockedMerchantIds[]` | `MERCHANT_BLOCK` | `merchant_id` |
| `newMerchantAction` | `NEW_MERCHANT_POLICY` | `{ "action": "BLOCK" }` |
| `productRestrictions[]` | `PRODUCT_RESTRICTION` | `product_id` or `category_code` or tag in `{ "matchType", "matchValue", "action", "note" }` |
| `duplicatePurchase` | `DUPLICATE_PURCHASE` | `{ "windowHours", "scope", "action" }`; row absent or `is_enabled=false` when disabled |
| `timeRestrictions[]` | `TIME_RESTRICTION` | `{ "daysOfWeek", "startLocalTime", "endLocalTime", "action" }` |
| `unbudgetedCategoryAction` | `UNBUDGETED_CATEGORY_POLICY` | `{ "action" }` |

### 1.2 Policy document validation (on `PUT /policies`)

| Rule | Error |
|---|---|
| At most one approval threshold, max transaction amount, new merchant policy, unbudgeted policy | `POLICY_INVALID` |
| At most one spending limit per period; one category budget per (category, period) | `POLICY_INVALID` |
| A merchant cannot be both approved and blocked | `POLICY_MERCHANT_CONFLICT` |
| Amounts: decimal strings, 2 dp, `0 ≤ threshold ≤ 100000`, `0 < limit ≤ 100000` | `VALIDATION_FAILED` |
| `windowHours` 1–720; times `HH:mm`; `daysOfWeek` non-empty, unique | `VALIDATION_FAILED` |
| Referenced merchants, products, categories exist | `VALIDATION_FAILED` |
| `currency` equals profile currency | `CURRENCY_MISMATCH` |

### 1.3 CartSnapshot
```json
{
  "cartId": "…", "cartVersion": 3,
  "merchantId": "…", "merchantName": "FreshMart",
  "currency": "USD",
  "items": [
    { "productId": "…", "sku": "FM-MLK-1G", "name": "Whole Milk 1 gal", "categoryCode": "GROCERIES",
      "tags": ["dairy"], "unitPrice": "4.29", "quantity": 1, "lineTotal": "4.29",
      "isAvailable": true, "stockQuantity": 120 }
  ],
  "total": "43.00"
}
```
Prices and availability come from the **database** at snapshot time, never from the client or the AI.

### 1.4 SpendingSnapshot
Built from the ledger (SPENDING_LEDGER.md §4) for the periods containing `now` in the profile time zone:

```json
{
  "weekStart": "2026-11-02", "monthStart": "2026-11-01",
  "committedByCategory": {
    "GROCERIES": { "WEEKLY": "0.00", "MONTHLY": "112.40" }
  },
  "committedTotal": { "WEEKLY": "0.00", "MONTHLY": "112.40" },
  "recentPurchases": [
    { "purchaseId": "…", "createdAt": "…", "merchantId": "…", "productIds": ["…"], "cartSnapshotHash": "…" }
  ]
}
```
"Committed" = PROPOSED + RESERVED + AUTHORIZED + CAPTURED. `recentPurchases` contains purchases in any non-terminal-failure status (`AWAITING_APPROVAL`, `READY_FOR_CHECKOUT`, `CHECKOUT_PENDING`, `PAYER_APPROVED`, `CAPTURE_PENDING`, `COMPLETED`) created within the largest duplicate window.

## 2. Evaluation algorithm

All checks run (no short-circuit) so the user sees every problem at once. Each check emits zero or more `PolicyReason`s with a severity: `BLOCK`, `REQUIRE_APPROVAL`, or `INFO`.

| # | Check | Condition | Reason code | Severity |
|---|---|---|---|---|
| 0a | Policy present | no active policy | `NO_ACTIVE_POLICY` | BLOCK |
| 0b | Cart non-empty | 0 items | `CART_EMPTY` | BLOCK |
| 0c | Currency | cart ≠ policy currency | `CURRENCY_MISMATCH` | BLOCK |
| 0d | Availability | any item unavailable or quantity > stock | `PRODUCT_UNAVAILABLE` | BLOCK |
| 1a | Merchant blocked | merchant in blocked list | `MERCHANT_BLOCKED` | BLOCK |
| 1b | Merchant approved | merchant in approved list | `MERCHANT_APPROVED` | INFO |
| 1c | New merchant | not in either list, action `BLOCK` | `MERCHANT_NOT_APPROVED` | BLOCK |
| 1d | New merchant | action `REQUIRE_APPROVAL` | `NEW_MERCHANT_REQUIRES_APPROVAL` | REQUIRE_APPROVAL |
| 1e | New merchant | action `ALLOW` | `NEW_MERCHANT_ALLOWED` | INFO |
| 2 | Product restriction | item matches restriction (product → category → tag) | `PRODUCT_RESTRICTED` (BLOCK) / `PRODUCT_REQUIRES_APPROVAL` | per action |
| 3 | Max transaction | `total > maxTransactionAmount` | `MAX_TRANSACTION_EXCEEDED` | BLOCK |
| 4a | Category budget | for each cart category & each budget rule: `committed + cartCategoryAmount > limit` | `CATEGORY_BUDGET_EXCEEDED` | BLOCK |
| 4b | Unbudgeted category | cart category has no budget rule, action ≠ `ALLOW` | `UNBUDGETED_CATEGORY` | per action |
| 5 | Overall limit | `committedTotal[p] + total > limit[p]` | `WEEKLY_LIMIT_EXCEEDED` / `MONTHLY_LIMIT_EXCEEDED` | BLOCK |
| 6 | Duplicate | `SAME_PRODUCT`: any cart product in a recent purchase within window; `SAME_CART`: identical snapshot hash within window | `DUPLICATE_PURCHASE` | per action |
| 7 | Time window | `now` (profile tz) inside a restriction window on a listed day | `TIME_RESTRICTED` | per action |
| 8 | Approval threshold | `total > approvalThreshold.amount` | `APPROVAL_THRESHOLD_EXCEEDED` | REQUIRE_APPROVAL |
| 9 | All clear | no BLOCK / REQUIRE_APPROVAL reasons | `ALL_POLICIES_SATISFIED` | INFO |

**Decision:** any `BLOCK` → `BLOCKED`; else any `REQUIRE_APPROVAL` → `REQUIRES_APPROVAL`; else `ALLOWED`.

**Boundary semantics:** limits are inclusive (`projected == limit` is allowed). Threshold is strict (`total == threshold` does not need approval). "Purchases above $30 require approval" → `30.00` → `30.01` needs approval.

**Time windows:** `start < end` = same-day window `[start, end)`; `start > end` = overnight window (e.g. 23:00–06:00), and the day-of-week refers to the day the window *starts*. DST is handled by converting `now` with `TimeZoneInfo` to local time before comparison.

**Budget periods:** week = ISO week starting Monday 00:00 local; month = calendar month local.

**Primary reason:** first `BLOCK` reason in check order; else first `REQUIRE_APPROVAL`; else `ALL_POLICIES_SATISFIED`.

**Reason ordering:** by check number, then category code, then rule id (ordinal).

## 3. Output — PolicyDecision

```json
{
  "evaluationId": "0193…",
  "decision": "REQUIRES_APPROVAL",
  "isAllowed": false,
  "requiresApproval": true,
  "reason": "Purchase exceeds approval threshold",
  "reasonCode": "APPROVAL_THRESHOLD_EXCEEDED",
  "purchaseAmount": "43.00",
  "remainingBudget": "37.00",
  "currency": "USD",
  "policyVersionId": "0193…",
  "policyVersionNumber": 5,
  "cartId": "…",
  "cartVersion": 3,
  "cartSnapshotHash": "9f2c…e1",
  "reasons": [
    { "code": "MERCHANT_APPROVED", "severity": "INFO", "message": "FreshMart is on your approved list", "ruleType": "MERCHANT_ALLOW", "ruleId": "…", "details": {} },
    { "code": "APPROVAL_THRESHOLD_EXCEEDED", "severity": "REQUIRE_APPROVAL", "message": "Purchase exceeds approval threshold", "ruleType": "APPROVAL_THRESHOLD", "ruleId": "…", "details": { "threshold": "30.00", "purchaseAmount": "43.00" } }
  ],
  "budgetImpacts": [
    { "scope": "CATEGORY", "categoryCode": "GROCERIES", "period": "WEEKLY", "periodStart": "2026-11-02",
      "limit": "80.00", "committedBefore": "0.00", "purchaseAmount": "43.00", "remainingBefore": "80.00", "remainingAfter": "37.00", "exceeded": false }
  ],
  "evaluatedAt": "2026-11-03T14:05:11Z"
}
```

- `isAllowed` is `true` **only** for `ALLOWED`.
- `remainingBudget` = the smallest `remainingAfter` across category budget impacts for the cart's categories; if none, smallest across overall limits; if none, `null`. It may be negative when blocked.
- Messages are generated from templates in code (not by the LLM). The agent explains them; it cannot change them.

## 4. Reason code catalogue

`NO_ACTIVE_POLICY`, `CART_EMPTY`, `CURRENCY_MISMATCH`, `PRODUCT_UNAVAILABLE`, `MERCHANT_BLOCKED`, `MERCHANT_APPROVED`, `MERCHANT_NOT_APPROVED`, `NEW_MERCHANT_REQUIRES_APPROVAL`, `NEW_MERCHANT_ALLOWED`, `PRODUCT_RESTRICTED`, `PRODUCT_REQUIRES_APPROVAL`, `MAX_TRANSACTION_EXCEEDED`, `CATEGORY_BUDGET_EXCEEDED`, `UNBUDGETED_CATEGORY`, `WEEKLY_LIMIT_EXCEEDED`, `MONTHLY_LIMIT_EXCEEDED`, `DUPLICATE_PURCHASE`, `TIME_RESTRICTED`, `APPROVAL_THRESHOLD_EXCEEDED`, `ALL_POLICIES_SATISFIED`.

## 5. Worked examples

Policy v5 = demo policy (GROCERIES $80 weekly, threshold $30, approved: FreshMart & GreenGrocer, new merchants `BLOCK`, ALCOHOL blocked, unbudgeted `REQUIRE_APPROVAL`). Committed GROCERIES this week = $0.

| Case | Cart | Decision | Reasons |
|---|---|---|---|
| E1 | FreshMart, GROCERIES $43.00 | REQUIRES_APPROVAL | MERCHANT_APPROVED, APPROVAL_THRESHOLD_EXCEEDED; remaining 37.00 |
| E2 | FreshMart, GROCERIES $24.50 | ALLOWED | MERCHANT_APPROVED, ALL_POLICIES_SATISFIED; remaining 55.50 |
| E3 | QuickCart Express, GROCERIES $18.00 | BLOCKED | MERCHANT_NOT_APPROVED |
| E4 | FreshMart, GROCERIES $85.00 | BLOCKED | CATEGORY_BUDGET_EXCEEDED (+ APPROVAL_THRESHOLD_EXCEEDED); remaining −5.00 |
| E5 | After E1 captured: FreshMart GROCERIES $40.00 | BLOCKED | CATEGORY_BUDGET_EXCEEDED (43 + 40 > 80) |
| E6 | QuickCart Express, ALCOHOL $15.00 | BLOCKED | MERCHANT_NOT_APPROVED, PRODUCT_RESTRICTED |
| E7 | FreshMart, HOUSEHOLD $12.00 (no budget for HOUSEHOLD in this variant) | REQUIRES_APPROVAL | UNBUDGETED_CATEGORY |
| E8 | FreshMart, GROCERIES exactly $80.00 | REQUIRES_APPROVAL | APPROVAL_THRESHOLD_EXCEEDED; remaining 0.00 (inclusive limit) |
| E9 | FreshMart, GROCERIES exactly $30.00 | ALLOWED | strict threshold |

These examples are mandatory unit tests (TESTING_STRATEGY.md).

## 6. What the engine does NOT do

- It does not reserve budget (the purchase use case does, under lock).
- It does not decide approval validity (Authorization Service).
- It does not know about PayPal.
- It never accepts a decision, amount or reason from the AI.
