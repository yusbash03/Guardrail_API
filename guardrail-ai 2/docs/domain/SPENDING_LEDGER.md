# Spending Ledger

## 1. Purpose

A single, append-only record of where every dollar of a purchase is in its life: proposed, held, authorized by the buyer, captured, failed, released or refunded. Budgets are computed from it; the dashboard reads from it; PayPal reconciliation writes to it.

## 2. Bucket model (ADR-009)

Each entry moves `amount` for one `(purchase, category)` from `from_bucket` to `to_bucket`.

```
          PROPOSE                CONFIRM_RESERVATION
  NONE ───────────▶ PROPOSED ────────────────────────▶ RESERVED ──AUTHORIZE──▶ AUTHORIZED ──CAPTURE──▶ CAPTURED ──REFUND──▶ REFUNDED
   │                   │                                  ▲  │                    ├──FAIL──▶ FAILED
   └──── RESERVE ──────┼──────────────────────────────────┘  │                    └──VOID──▶ RELEASED
                       └──RELEASE──▶ RELEASED ◀──RELEASE─────┘
```

| Bucket | Meaning | Counts toward budget? |
|---|---|---|
| PROPOSED | Purchase awaiting human approval | **Yes** (prevents two pending approvals over-committing) |
| RESERVED | Approved or auto-allowed; held for checkout | Yes |
| AUTHORIZED | PayPal authorization created — funds held on the buyer's account (verified server-side). Under the `CAPTURE` fallback: buyer approval verified | Yes |
| CAPTURED | Funds captured (verified by PayPal API response or verified webhook) | Yes |
| RELEASED | Hold dropped (rejected, cancelled, expired) | No |
| FAILED | Capture declined/failed | No |
| REFUNDED | Refunded after capture | No |

`balance(bucket) = Σ amount where to_bucket = bucket − Σ amount where from_bucket = bucket`

`committed = balance(PROPOSED) + balance(RESERVED) + balance(AUTHORIZED) + balance(CAPTURED)`

Dashboard terminology:
- **Spent** = balance(CAPTURED)
- **Pending** = balance(PROPOSED) + balance(RESERVED) + balance(AUTHORIZED)
- **Remaining** = limit − committed

## 3. When entries are written

| Event | Transition | Writer | Source |
|---|---|---|---|
| Purchase created, decision `REQUIRES_APPROVAL` | PROPOSE (per category) | CreatePurchase | SYSTEM |
| Purchase created, decision `ALLOWED` | RESERVE | CreatePurchase | SYSTEM |
| Approval granted | CONFIRM_RESERVATION | ApprovePurchase | USER |
| Approval rejected / expired / invalidated; purchase cancelled before PayPal approval; checkout expired | RELEASE | Approvals / Sweeper / Cancel | USER / SWEEPER |
| PayPal authorization `CREATED` (authorize response or verified `PAYMENT.AUTHORIZATION.CREATED`); `CAPTURE` fallback: order verified `APPROVED` | AUTHORIZE | Capture use case / WebhookProcessor / Reconciler | PAYPAL_API / PAYPAL_WEBHOOK / RECONCILER |
| Authorization voided (final guardrail check failed, stale authorization, or verified `PAYMENT.AUTHORIZATION.VOIDED`) | VOID | Capture use case / Reconciler / WebhookProcessor | PAYPAL_API / RECONCILER / PAYPAL_WEBHOOK |
| Capture `COMPLETED` (capture API response or verified `PAYMENT.CAPTURE.COMPLETED`) | CAPTURE | Capture / WebhookProcessor / Reconciler | PAYPAL_API / PAYPAL_WEBHOOK / RECONCILER |
| Capture `DECLINED` / `FAILED` | FAIL | same | same |
| Verified `PAYMENT.CAPTURE.REFUNDED` | REFUND (`external_ref` = refund id; amount allocated pro rata across categories, remainder cent to largest category) | WebhookProcessor | PAYPAL_WEBHOOK |

A capture that is `PENDING` writes nothing new: money stays in AUTHORIZED (still counted) until a terminal result.

## 4. Period attribution

Each purchase fixes `budget_week_start` and `budget_month_start` at creation time (profile time zone). **All** of its ledger entries carry those dates, even if capture happens after midnight Sunday. Rationale: the budget decision was made against that period; moving it later would let a purchase escape its evaluation.

Spending snapshot query (per user, per period):

```sql
select category_code,
       sum(case when to_bucket   in ('PROPOSED','RESERVED','AUTHORIZED','CAPTURED') then amount else 0 end)
     - sum(case when from_bucket in ('PROPOSED','RESERVED','AUTHORIZED','CAPTURED') then amount else 0 end) as committed
from spending_ledger_entries
where user_id = @user and budget_week_start = @weekStart
group by category_code;
```
(Same for `budget_month_start`.)

## 5. Idempotency and double-counting prevention

1. **Unique constraint** `(purchase_id, category_code, transition, external_ref)`: a transition can only happen once per purchase/category. A duplicate insert (e.g. capture response and webhook both report completion) raises `23505`, which the writer treats as "already applied" — success, no-op.
2. **State guard:** writers check the purchase/payment status inside the transaction before writing (e.g. only write CAPTURE if payment not already `COMPLETED`).
3. **Valid source bucket:** `LedgerService` computes the current bucket for `(purchase, category)` and refuses transitions whose `from_bucket` doesn't match (`LEDGER_INVALID_TRANSITION`, logged + alert).
4. **Amount guard:** CAPTURE amount must equal the AUTHORIZED amount for the purchase total; mismatch → `AMOUNT_MISMATCH`, payment flagged, no ledger write, audit + log at Error.

## 6. Concurrency

- Purchase creation: `SELECT … FROM spending_profiles WHERE user_id = @u FOR UPDATE`, rebuild SpendingSnapshot, re-evaluate, insert PROPOSE/RESERVE, commit. Concurrent purchase creation for the same user serializes; the second sees the first's committed amount.
- Other transitions do not increase committed spend (they move between counted buckets or out of them), so they need only row-level locks on the purchase/payment (`FOR UPDATE`) plus the unique constraint.
- Isolation level: `READ COMMITTED` + explicit row locks (sufficient because all budget-increasing writes take the same profile lock).

## 7. API surface

- `GET /api/v1/ledger/summary?period=WEEKLY|MONTHLY&date=YYYY-MM-DD` → per-category limit/spent/pending/remaining.
- `GET /api/v1/ledger/entries?purchaseId=&from=&to=&page=` → raw entries (owner only).

Example summary:

```json
{
  "period": "WEEKLY", "periodStart": "2026-11-02", "periodEnd": "2026-11-08", "currency": "USD",
  "categories": [
    { "categoryCode": "GROCERIES", "limit": "80.00", "spent": "43.00", "pending": "0.00", "remaining": "37.00" }
  ],
  "overall": { "limit": "200.00", "spent": "43.00", "pending": "0.00", "remaining": "157.00" }
}
```

## 8. Tests that must exist

- Each transition writes exactly one row; replays are no-ops.
- Capture via API + webhook for same purchase → one CAPTURE row.
- Two parallel purchase creations each $45 against $80 budget → exactly one succeeds, the other gets `422 EVALUATION_BLOCKED` (`CATEGORY_BUDGET_EXCEEDED`) (integration test with real PostgreSQL).
- Reject after PROPOSE → committed returns to previous value.
- Week rollover: purchase created Sunday 23:59 local, captured Monday 00:05 → counts in the earlier week.
