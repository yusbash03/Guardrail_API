# Domain Model

This file is the **source of truth for names and enums**. DATABASE_SCHEMA.md, API_CONTRACT.md, AI_TOOL_CONTRACTS.md and the frontend Zod schemas must use exactly these values.

## 1. Aggregates and ownership

```
User ─1:1─ SpendingProfile ─1:N─ PolicyVersion ─1:N─ SpendingPolicy (rule)
  │                 └── current_policy_version_id ──▶ PolicyVersion
  ├─1:N─ ShoppingSession ─1:N─ ShoppingCart ─1:N─ CartItem ──▶ Product ──▶ Merchant
  │                                  │
  │                                  ├─1:N─ PolicyEvaluation
  │                                  └─1:N─ Purchase ─1:0..1─ ApprovalRequest
  │                                             ├─1:N─ Payment ◀── PayPalWebhookEvent
  │                                             └─1:N─ SpendingLedgerEntry
  ├─1:N─ AgentConversation ─1:N─ AgentMessage ─1:N─ AgentExecution
  ├─1:N─ RefreshToken
  └─1:N─ AuditLog
Merchant ─1:N─ Product ──▶ Category
```

User-owned aggregates carry `UserId` directly (denormalised) so every query can filter by owner without joins.

## 2. Value objects

| Name | Shape | Rules |
|---|---|---|
| `Money` | `(decimal Amount, string Currency)` | Currency is ISO 4217 upper-case; arithmetic only between same currency; USD rounded to 2 dp |
| `CartSnapshot` | merchantId, currency, items[], total, cartId, cartVersion | Canonical JSON → `SnapshotHash` (SHA-256 hex, 64 chars) |
| `PolicySnapshot` | policyVersionId, versionNumber, rules[], timeZone, currency | Built from one `PolicyVersion` |
| `SpendingSnapshot` | committed amounts per (category, week/month) and overall, recent purchases for duplicate checks | Built from ledger at evaluation time |
| `PolicyDecision` | decision, reasons[], amounts | Output of the engine |
| `BudgetPeriod` | `WEEKLY` (ISO week, Monday start) or `MONTHLY` (calendar month), in profile time zone | |

## 3. Enumerations (canonical)

### PolicyRuleType
`CATEGORY_BUDGET`, `SPENDING_LIMIT`, `MAX_TRANSACTION_AMOUNT`, `APPROVAL_THRESHOLD`, `MERCHANT_ALLOW`, `MERCHANT_BLOCK`, `NEW_MERCHANT_POLICY`, `PRODUCT_RESTRICTION`, `DUPLICATE_PURCHASE`, `TIME_RESTRICTION`, `UNBUDGETED_CATEGORY_POLICY`

### BudgetPeriodType
`WEEKLY`, `MONTHLY`

### RuleAction (for rules that choose an outcome)
`ALLOW`, `REQUIRE_APPROVAL`, `BLOCK`

### PolicyDecisionType
`ALLOWED`, `REQUIRES_APPROVAL`, `BLOCKED`

### MerchantStatus (derived per user, not stored)
`APPROVED`, `BLOCKED`, `NEW`

### CartStatus
`OPEN`, `LOCKED`, `CHECKED_OUT`, `ABANDONED`

### PurchaseStatus
`AWAITING_APPROVAL`, `READY_FOR_CHECKOUT`, `CHECKOUT_PENDING`, `PAYER_APPROVED`, `PAYMENT_AUTHORIZED`, `CAPTURE_PENDING`, `COMPLETED`, `REJECTED`, `CANCELLED`, `EXPIRED`, `FAILED`, `REFUNDED`, `PARTIALLY_REFUNDED`

### ApprovalStatus
`PENDING`, `APPROVED`, `REJECTED`, `EXPIRED`, `INVALIDATED`, `CONSUMED`

### PaymentStatus
`INITIATED`, `CREATED`, `PAYER_ACTION_REQUIRED`, `APPROVED`, `AUTHORIZED`, `CAPTURE_PENDING`, `COMPLETED`, `DECLINED`, `FAILED`, `VOIDED`, `REFUNDED`, `PARTIALLY_REFUNDED`, `REVERSED`

### LedgerBucket
`NONE`, `PROPOSED`, `RESERVED`, `AUTHORIZED`, `CAPTURED`, `RELEASED`, `FAILED`, `REFUNDED`

### LedgerTransition
| Transition | From → To |
|---|---|
| `PROPOSE` | NONE → PROPOSED |
| `RESERVE` | NONE → RESERVED |
| `CONFIRM_RESERVATION` | PROPOSED → RESERVED |
| `RELEASE` | PROPOSED or RESERVED → RELEASED |
| `AUTHORIZE` | RESERVED → AUTHORIZED |
| `CAPTURE` | AUTHORIZED → CAPTURED |
| `VOID` | AUTHORIZED → RELEASED (PayPal authorization voided before capture, e.g. final guardrail check failed) |
| `FAIL` | AUTHORIZED → FAILED |
| `REFUND` | CAPTURED → REFUNDED |

### WebhookEventStatus
`RECEIVED`, `PROCESSED`, `IGNORED`, `FAILED`, `REJECTED`

### ActorType
`USER`, `AGENT`, `SYSTEM`, `PAYPAL`

### AgentMessageRole
`USER`, `ASSISTANT`, `TOOL`

### AgentExecutionStatus
`SUCCEEDED`, `REJECTED` (validation/authorization failure), `FAILED` (unexpected error)

### Category codes (seed)
`GROCERIES`, `HOUSEHOLD`, `PERSONAL_CARE`, `BABY`, `ELECTRONICS`, `ALCOHOL`

## 4. Entity behaviour (key invariants)

### SpendingProfile
- `UpdatePolicy(rules, expectedVersionNumber, actor)` → creates `PolicyVersion n+1`; fails with `POLICY_VERSION_CONFLICT` if `expectedVersionNumber != current`.

### ShoppingCart
- `AddItem/UpdateQuantity/RemoveItem` allowed only when `Status == OPEN`; each increments `Version` and recalculates totals from current product prices.
- All items must share `MerchantId` and `Currency`.
- `Lock()` → `LOCKED`; `Unlock()` (only via purchase cancellation) → `OPEN`, `Version++`.
- `ToSnapshot()` produces the `CartSnapshot`.

### Purchase
- Created only from a non-`BLOCKED` evaluation of the cart's current version.
- Status transitions defined in PAYMENT_STATE_MACHINE.md; illegal transitions throw `InvalidStateTransition`.

### ApprovalRequest
- `Approve(userId, presentedHash, now, currentPolicyVersionId)` succeeds only if the caller has already passed password step-up (ADR-015), and: owner matches, `PENDING`, not expired, `presentedHash == CartSnapshotHash`, `currentPolicyVersionId == PolicyVersionId`.
- `Consume(now)` succeeds only if `APPROVED`, not expired, `ConsumedAt == null`; sets `CONSUMED`.
- `Invalidate(reason)` from `PENDING` or `APPROVED`.

### Payment
- One non-terminal payment per purchase.
- `Amount`/`Currency` copied from purchase; PayPal results with different amount/currency raise `AMOUNT_MISMATCH` and the payment is flagged, never silently accepted.

### SpendingLedgerEntry
- Immutable. Each `(PurchaseId, CategoryCode, Transition, ExternalRef)` occurs at most once.

## 5. Domain events (in-process)

`PolicyVersionCreated`, `CartLocked`, `PurchaseCreated`, `ApprovalGranted`, `ApprovalRejected`, `ApprovalExpired`, `ApprovalInvalidated`, `CheckoutStarted`, `PayerApproved`, `PaymentAuthorized`, `FinalCheckFailed`, `PaymentVoided`, `PaymentCaptured`, `PaymentFailed`, `PaymentRefunded`.

Handlers (same transaction) write `AuditLog` rows. No outbox is needed because there is no external bus.
