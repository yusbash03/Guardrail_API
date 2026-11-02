# Policy Evaluation Contract

The single wire contract for a policy decision, used by the REST API, the AI tool `EvaluatePurchase`, the approval card and persisted `policy_evaluations`. Semantics are defined in SPENDING_POLICY_ENGINE.md; this file fixes the shape.

## Endpoint

```
POST /api/v1/carts/{cartId}/evaluate
Authorization: Bearer <JWT>
(no body)
```

| Status | When |
|---|---|
| 200 | Evaluation performed (any decision, including BLOCKED) |
| 404 `NOT_FOUND` | Cart doesn't exist or isn't owned |
| 409 `CART_NOT_OPEN` | Cart is LOCKED / CHECKED_OUT / ABANDONED |
| 422 `NO_ACTIVE_POLICY` | Profile has no policy version (should not happen post-onboarding) |

A `BLOCKED` decision is a successful evaluation → **200**, not an error.

## Response schema (JSON Schema, abbreviated)

```json
{
  "type": "object",
  "required": ["evaluationId","decision","isAllowed","requiresApproval","reason","reasonCode",
               "purchaseAmount","remainingBudget","currency","policyVersionId","policyVersionNumber",
               "cartId","cartVersion","cartSnapshotHash","reasons","budgetImpacts","evaluatedAt"],
  "properties": {
    "evaluationId":        { "type": "string", "format": "uuid" },
    "decision":            { "enum": ["ALLOWED","REQUIRES_APPROVAL","BLOCKED"] },
    "isAllowed":           { "type": "boolean" },
    "requiresApproval":    { "type": "boolean" },
    "reason":              { "type": "string" },
    "reasonCode":          { "type": "string" },
    "purchaseAmount":      { "$ref": "#/$defs/money" },
    "remainingBudget":     { "anyOf": [ { "$ref": "#/$defs/money" }, { "type": "null" } ] },
    "currency":            { "type": "string", "pattern": "^[A-Z]{3}$" },
    "policyVersionId":     { "type": "string", "format": "uuid" },
    "policyVersionNumber": { "type": "integer" },
    "cartId":              { "type": "string", "format": "uuid" },
    "cartVersion":         { "type": "integer" },
    "cartSnapshotHash":    { "type": "string", "pattern": "^[0-9a-f]{64}$" },
    "reasons": { "type": "array", "items": {
      "type": "object", "required": ["code","severity","message"],
      "properties": {
        "code":     { "type": "string" },
        "severity": { "enum": ["BLOCK","REQUIRE_APPROVAL","INFO"] },
        "message":  { "type": "string" },
        "ruleType": { "type": ["string","null"] },
        "ruleId":   { "type": ["string","null"] },
        "details":  { "type": "object" } } } },
    "budgetImpacts": { "type": "array", "items": {
      "type": "object",
      "required": ["scope","period","periodStart","limit","committedBefore","purchaseAmount","remainingBefore","remainingAfter","exceeded"],
      "properties": {
        "scope":           { "enum": ["CATEGORY","OVERALL"] },
        "categoryCode":    { "type": ["string","null"] },
        "period":          { "enum": ["WEEKLY","MONTHLY"] },
        "periodStart":     { "type": "string", "format": "date" },
        "limit":           { "$ref": "#/$defs/money" },
        "committedBefore": { "$ref": "#/$defs/money" },
        "purchaseAmount":  { "$ref": "#/$defs/money" },
        "remainingBefore": { "$ref": "#/$defs/money" },
        "remainingAfter":  { "$ref": "#/$defs/signedMoney" },
        "exceeded":        { "type": "boolean" } } } },
    "evaluatedAt": { "type": "string", "format": "date-time" }
  },
  "$defs": {
    "money":       { "type": "string", "pattern": "^\\d{1,12}\\.\\d{2}$" },
    "signedMoney": { "type": "string", "pattern": "^-?\\d{1,12}\\.\\d{2}$" }
  }
}
```

`remainingBudget` uses `signedMoney` (may be negative when BLOCKED by budget).

## Invariants

| # | Invariant |
|---|---|
| I1 | `isAllowed == (decision == "ALLOWED")` |
| I2 | `requiresApproval == (decision == "REQUIRES_APPROVAL")` |
| I3 | `decision == BLOCKED` ⇔ ∃ reason with severity `BLOCK` |
| I4 | `decision == REQUIRES_APPROVAL` ⇔ no `BLOCK` reasons ∧ ∃ `REQUIRE_APPROVAL` reason |
| I5 | `reasonCode` = code of the primary reason (SPENDING_POLICY_ENGINE.md §2) and `reason` its message |
| I6 | `purchaseAmount` = cart total from the snapshot whose hash is `cartSnapshotHash` |
| I7 | Persisted evaluation is immutable and retrievable via `GET /api/v1/evaluations/{id}` |

## Canonical demo responses

### Approval required ($43 cart)
```json
{
  "evaluationId": "0193f0a2-…",
  "decision": "REQUIRES_APPROVAL",
  "isAllowed": false,
  "requiresApproval": true,
  "reason": "Purchase exceeds approval threshold",
  "reasonCode": "APPROVAL_THRESHOLD_EXCEEDED",
  "purchaseAmount": "43.00",
  "remainingBudget": "37.00",
  "currency": "USD",
  "policyVersionNumber": 5,
  "reasons": [
    { "code": "MERCHANT_APPROVED", "severity": "INFO", "message": "FreshMart is on your approved list" },
    { "code": "APPROVAL_THRESHOLD_EXCEEDED", "severity": "REQUIRE_APPROVAL", "message": "Purchase exceeds approval threshold",
      "details": { "threshold": "30.00", "purchaseAmount": "43.00" } }
  ],
  "budgetImpacts": [
    { "scope": "CATEGORY", "categoryCode": "GROCERIES", "period": "WEEKLY", "periodStart": "2026-11-02",
      "limit": "80.00", "committedBefore": "0.00", "purchaseAmount": "43.00",
      "remainingBefore": "80.00", "remainingAfter": "37.00", "exceeded": false }
  ]
}
```

### Blocked (unapproved merchant)
```json
{
  "decision": "BLOCKED",
  "isAllowed": false,
  "requiresApproval": false,
  "reason": "QuickCart Express is not on your approved merchant list",
  "reasonCode": "MERCHANT_NOT_APPROVED",
  "purchaseAmount": "18.00",
  "remainingBudget": "62.00",
  "reasons": [
    { "code": "MERCHANT_NOT_APPROVED", "severity": "BLOCK", "message": "QuickCart Express is not on your approved merchant list",
      "ruleType": "NEW_MERCHANT_POLICY", "details": { "merchantId": "…", "newMerchantAction": "BLOCK" } }
  ]
}
```
(`remainingBudget` still reports the budget effect for transparency, even though the purchase cannot proceed.)

## Message templates (server-side, en-US)

| Code | Template |
|---|---|
| `MERCHANT_BLOCKED` | `{merchant} is on your blocked merchant list` |
| `MERCHANT_NOT_APPROVED` | `{merchant} is not on your approved merchant list` |
| `NEW_MERCHANT_REQUIRES_APPROVAL` | `{merchant} is new to you and needs your approval` |
| `PRODUCT_RESTRICTED` | `{product} is restricted by your rules` |
| `PRODUCT_REQUIRES_APPROVAL` | `{product} needs your approval under your rules` |
| `PRODUCT_UNAVAILABLE` | `{product} is unavailable` |
| `MAX_TRANSACTION_EXCEEDED` | `Purchase exceeds your maximum transaction amount of {limit}` |
| `CATEGORY_BUDGET_EXCEEDED` | `This would exceed your {period} {category} budget of {limit} by {overBy}` |
| `UNBUDGETED_CATEGORY` | `You have no budget set for {category}` |
| `WEEKLY_LIMIT_EXCEEDED` / `MONTHLY_LIMIT_EXCEEDED` | `This would exceed your {period} spending limit of {limit}` |
| `DUPLICATE_PURCHASE` | `You bought {product} in the last {hours} hours` |
| `TIME_RESTRICTED` | `Purchases are restricted between {start} and {end}` |
| `APPROVAL_THRESHOLD_EXCEEDED` | `Purchase exceeds approval threshold` |
| `ALL_POLICIES_SATISFIED` | `All your spending rules are satisfied` |
