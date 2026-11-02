# AI Tool Contracts

Eleven tools. No others exist. Every tool:

- receives `userId` from the authenticated request context (never from arguments);
- validates arguments with FluentValidation (`additionalProperties: false`; unknown fields rejected);
- calls an existing application use case (the same one the REST API uses);
- returns an envelope `{ "ok": true, "data": … , "card": … }` or `{ "ok": false, "error": { "code", "message" } }`;
- is persisted as an `AgentExecution`.

Money values are decimal strings with 2 dp (`"43.00"`). IDs are UUID strings.

| Tool | Side effects | Card |
|---|---|---|
| SearchProducts | none | `PRODUCT_LIST` |
| GetProductDetails | none | `PRODUCT` |
| SearchMerchants | none | `MERCHANT_LIST` |
| GetApprovedMerchants | none | `MERCHANT_LIST` |
| GetSpendingPolicies | none | `POLICY_SUMMARY` |
| GetRemainingBudget | none | `BUDGET` |
| CreateShoppingCart | creates cart (OPEN); may abandon a superseded OPEN cart | `CART` |
| EvaluatePurchase | persists PolicyEvaluation | `EVALUATION` |
| RequestPurchaseApproval | creates Purchase (+ ApprovalRequest), locks cart, writes ledger PROPOSE/RESERVE | `APPROVAL` or `PURCHASE` |
| GetPurchaseStatus | none | `PURCHASE` |
| GetSpendingSummary | none | `BUDGET` |

---

## 1. SearchProducts

```json
{
  "name": "SearchProducts",
  "description": "Search the Guardrail catalogue. Returns only products that exist in the database.",
  "input_schema": {
    "type": "object", "additionalProperties": false,
    "properties": {
      "query":        { "type": "string", "maxLength": 100 },
      "queries":      { "type": "array", "maxItems": 12, "items": { "type": "string", "maxLength": 100 },
                        "description": "Batch search: one entry per item needed (e.g. milk, eggs, bread). Saves round-trips." },
      "categoryCode": { "type": "string", "enum": ["GROCERIES","HOUSEHOLD","PERSONAL_CARE","BABY","ELECTRONICS","ALCOHOL"] },
      "merchantId":   { "type": "string", "format": "uuid" },
      "maxUnitPrice": { "type": "string", "pattern": "^\\d{1,6}(\\.\\d{1,2})?$" },
      "approvedMerchantsOnly": { "type": "boolean", "default": false },
      "limit":        { "type": "integer", "minimum": 1, "maximum": 20, "default": 10 }
    }
  }
}
```
Validation: at least one of `query`, `queries`, `categoryCode`, `merchantId`; not both `query` and `queries`. With `queries`, `limit` applies per query (default 5).
Output `data` (single `query`; with `queries` the shape is `{ "results": [ { "query": "milk", "totalMatches": 3, "products": [ … ] } ] }`):
```json
{ "totalMatches": 3, "products": [
  { "productId": "…", "name": "Whole Milk", "brand": "Meadow Farms", "unitLabel": "1 gal",
    "merchantId": "…", "merchantName": "FreshMart", "merchantStatus": "APPROVED",
    "categoryCode": "GROCERIES", "unitPrice": "4.29", "currency": "USD", "isAvailable": true }
]}
```
Sorted by relevance (`ts_rank`), then price ascending. Unavailable products are included with `isAvailable:false` (so the agent can say so) but never first.

## 2. GetProductDetails

Input: `{ "productId": uuid }` (required).
Output: product fields above + `description` (max 500 chars, control characters stripped) + `tags` + `stockQuantity`. Description is wrapped: `"description": { "untrustedText": "…" }`.
Errors: `NOT_FOUND`.

## 3. SearchMerchants

Input: `{ "query"?: string ≤ 60, "categoryCode"?: enum }`.
Output: `{ "merchants": [ { "merchantId", "name", "categoryCodes": [], "status": "APPROVED|BLOCKED|NEW" } ] }`.

## 4. GetApprovedMerchants

Input: `{}`. Output: same shape as SearchMerchants, only `APPROVED`.

## 5. GetSpendingPolicies

Input: `{}`.
Output: read-only summary of the current PolicyDocument (SPENDING_POLICY_ENGINE.md §1.1) with merchant names resolved:
```json
{ "versionNumber": 5, "currency": "USD", "approvalThreshold": "30.00", "maxTransactionAmount": null,
  "categoryBudgets": [ { "categoryCode": "GROCERIES", "period": "WEEKLY", "limit": "80.00" } ],
  "spendingLimits": [], "newMerchantAction": "BLOCK",
  "approvedMerchants": ["FreshMart","GreenGrocer Co."], "blockedMerchants": [],
  "productRestrictions": [ { "match": "CATEGORY:ALCOHOL", "action": "BLOCK" } ],
  "duplicatePurchase": null, "timeRestrictions": [], "unbudgetedCategoryAction": "REQUIRE_APPROVAL" }
```

## 6. GetRemainingBudget

Input: `{ "categoryCode"?: enum, "period"?: "WEEKLY"|"MONTHLY" }`.
Output:
```json
{ "currency": "USD", "asOf": "2026-11-03T14:00:00Z", "budgets": [
  { "scope": "CATEGORY", "categoryCode": "GROCERIES", "period": "WEEKLY",
    "periodStart": "2026-11-02", "periodEnd": "2026-11-08",
    "limit": "80.00", "spent": "0.00", "pending": "0.00", "remaining": "80.00" } ] }
```
If no budget is configured for the category: `budgets: []` plus `"note": "NO_BUDGET_CONFIGURED"`.

## 7. CreateShoppingCart

```json
{
  "name": "CreateShoppingCart",
  "description": "Create a cart at ONE merchant with catalogue products. Prices are set by the server.",
  "input_schema": {
    "type": "object", "additionalProperties": false, "required": ["merchantId","items"],
    "properties": {
      "merchantId": { "type": "string", "format": "uuid" },
      "items": { "type": "array", "minItems": 1, "maxItems": 30, "items": {
        "type": "object", "additionalProperties": false, "required": ["productId","quantity"],
        "properties": { "productId": { "type": "string", "format": "uuid" },
                        "quantity":  { "type": "integer", "minimum": 1, "maximum": 99 } } } },
      "supersedesCartId": { "type": "string", "format": "uuid" }
    }
  }
}
```
Rules: every product must belong to `merchantId`, be available, and have sufficient stock; duplicate productIds are merged. `supersedesCartId`, if given, must be an `OPEN` cart owned by the user and is marked `ABANDONED`. Merchant status is **not** checked here (the policy engine handles it), so the agent can demonstrate a blocked evaluation.
Output:
```json
{ "cartId": "…", "cartVersion": 1, "merchantId": "…", "merchantName": "FreshMart", "status": "OPEN",
  "currency": "USD", "items": [ { "productId": "…", "name": "Whole Milk", "unitPrice": "4.29", "quantity": 1, "lineTotal": "4.29", "categoryCode": "GROCERIES" } ],
  "total": "43.00" }
```
Errors: `PRODUCT_NOT_FOUND`, `PRODUCT_UNAVAILABLE`, `MIXED_MERCHANT_CART`, `CART_LIMIT_EXCEEDED`, `VALIDATION_FAILED`.

## 8. EvaluatePurchase

Input: `{ "cartId": uuid }` (required).
Output: the full `PolicyDecision` (POLICY_EVALUATION_CONTRACT.md) — including `evaluationId`, `decision`, `reasons`, `budgetImpacts`, `remainingBudget`.
Errors: `NOT_FOUND`, `CART_NOT_OPEN` (if locked/abandoned; the agent should use GetPurchaseStatus).

## 9. RequestPurchaseApproval

Input: `{ "cartId": uuid, "evaluationId": uuid, "note"?: string ≤ 200 }`.
Behaviour: invokes the same `CreatePurchaseUseCase` as `POST /purchases` (BR-40/41). The tool **cannot** approve anything.
Output:
```json
{ "purchaseId": "…", "purchaseStatus": "AWAITING_APPROVAL",
  "approvalRequestId": "…", "approvalExpiresAt": "2026-11-03T14:35:11Z",
  "amount": "43.00", "currency": "USD",
  "nextStep": "USER_MUST_APPROVE_IN_APP" }
```
For `ALLOWED` evaluations: `purchaseStatus: "READY_FOR_CHECKOUT"`, `approvalRequestId: null`, `nextStep: "USER_MUST_CHECKOUT_WITH_PAYPAL"`.
Errors: `EVALUATION_BLOCKED`, `EVALUATION_STALE`, `EVALUATION_EXPIRED`, `CART_NOT_OPEN`, `NOT_FOUND`.

## 10. GetPurchaseStatus

Input: `{ "purchaseId"?: uuid }`. Without an id, returns the 5 most recent purchases.
Output: `{ "purchases": [ { "purchaseId", "merchantName", "amount", "currency", "status", "approvalStatus", "paymentStatus", "createdAt", "completedAt" } ] }`.

## 11. GetSpendingSummary

Input: `{ "period": "WEEKLY"|"MONTHLY" }` (required).
Output: ledger summary (SPENDING_LEDGER.md §7) + `recentPurchases` (max 5, same shape as GetPurchaseStatus).

---

## Tools that intentionally do not exist

`ApprovePurchase`, `Checkout`, `CapturePayment`, `CreatePayPalOrder`, `UpdatePolicy`, `ApproveMerchant`, `BlockMerchant`, `WriteLedger`, `RunSql`, `HttpFetch`. A test asserts the registry contains exactly the eleven tools above.

## Card payloads (rendered by the UI)

Cards in the assistant message are references: `{ "type": "EVALUATION", "id": "<evaluationId>" }`. `GET /agent/conversations/{id}/messages` returns hydrated cards built from database records at read time, e.g.:

```json
{ "type": "APPROVAL", "id": "…", "data": { "approvalRequestId": "…", "status": "PENDING", "amount": "43.00", "currency": "USD",
  "merchantName": "FreshMart", "cartSnapshotHash": "9f2c…", "expiresAt": "…", "items": [ … ], "reasons": [ … ] } }
```
