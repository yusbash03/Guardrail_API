# Frontend Specification

React 19 + TypeScript (strict) + Vite + Tailwind CSS + shadcn/ui + TanStack Query + React Router + React Hook Form + Zod + Recharts + Lucide + **AG Grid Community** (data-dense tables, ADR-017). PWA via `vite-plugin-pwa`. API transport via an **APIMatic-generated TypeScript SDK** (ADR-018, P1; hand-written client fallback).

## 1. App shell

- **Desktop (≥ 1024 px):** left sidebar (logo, nav, user menu), top bar (page title, "Sandbox" badge, pending approvals bell), content max-width 1200 px.
- **Mobile (< 1024 px):** top bar + bottom tab bar (Overview, Shop, Approvals, Activity, More). Rules, Merchants, Settings under **More**.
- Persistent "Sandbox mode — no real money moves" badge.

## 2. Routes

| Route | Page | Auth |
|---|---|---|
| `/login`, `/register` | Auth | anon |
| `/` | Overview | ✅ |
| `/shop`, `/shop/:conversationId` | AI Shopping | ✅ |
| `/rules` | Spending Rules | ✅ |
| `/rules/history` | Policy versions | ✅ |
| `/merchants` | Merchants | ✅ |
| `/approvals`, `/approvals/:id` | Purchase Approvals | ✅ |
| `/transactions`, `/transactions/:purchaseId` | Transactions | ✅ |
| `/settings` | Settings | ✅ |
| `/checkout/return` | PayPal return handler | ✅ |
| `/checkout/cancel` | PayPal cancel handler | ✅ |

## 3. Pages

### 3.1 Overview
- **Budget rings** (Recharts radial): Weekly overall, Monthly overall — spent / pending / remaining.
- **Category bars**: each budgeted category, spent vs pending vs limit; remaining labelled.
- **KPI tiles**: Total spent (month), Remaining allowance (week), Pending approvals (clickable), Active rules count.
- **Recent purchases** (5): merchant, amount, status chip, time.
- **AI insights** list from `/dashboard/overview.insights` (icon + one line).
- Empty state: "Set your first rule" CTA.
- Data: `GET /dashboard/overview` (refetch on focus, 30 s stale time).

### 3.2 AI Shopping
- Left: conversation list (desktop) / sheet (mobile).
- Center: chat thread. User bubbles right; assistant left with **cards** below text:
  - `PRODUCT_LIST`: horizontal scroll of product tiles (image, name, merchant + status chip, price).
  - `CART`: line items, total, merchant, "Edit cart" (only when OPEN).
  - `EVALUATION`: decision badge (green ALLOWED / amber REQUIRES_APPROVAL / red BLOCKED), primary reason, expandable reasons list, budget impact bar (before → after).
  - `APPROVAL`: approval panel (APPROVAL_WORKFLOW.md §9) with Approve / Reject — Approve opens the password confirmation dialog; calls REST endpoints directly, **not** via chat.
  - `PURCHASE`: status timeline + **Pay with PayPal** button when `READY_FOR_CHECKOUT`.
  - `BUDGET`: mini bar chart.
- Composer: textarea (Enter to send, Shift+Enter newline), suggestion chips ("Weekly groceries under budget", "What's left this week?", "Compare milk prices").
- "Agent is working…" state lists tool calls as they complete (from response `toolCalls`; P2 streaming).
- Right rail (desktop): live **Budget** panel for current week.

### 3.3 Spending Rules
Sections (cards), each editable in a dialog with RHF + Zod; a single **Save changes** creates one policy version (PUT with `expectedVersionNumber`; on 409 show "Rules changed elsewhere — reload").
1. Category budgets (table: category, period, limit; add/remove).
2. Overall limits (weekly, monthly).
3. Transaction rules: max transaction amount, approval threshold (slider + input).
4. Merchants: approved / blocked chips + "New merchants" segmented control (Block / Ask me / Allow).
5. Product restrictions (match type + value + action).
6. Duplicate protection (toggle, window hours, scope, action).
7. Time restrictions (days, start, end, action).
8. Unbudgeted categories action.
- Footer: "Version 5 · updated 2 min ago · View history".
- History page: AG Grid of versions (number, summary, created, hash); selecting two shows a field-level diff.

### 3.4 Merchants
Grid of merchant cards: logo, name, categories, status chip, product count; actions Approve / Block / Reset. Filter by status. Each action shows confirm toast with new version number.

### 3.5 Purchase Approvals
Tabs: Pending (default), History (AG Grid on desktop). Pending cards show countdown; detail page shows full approval panel. Badge count in nav from `GET /approvals?status=PENDING` (poll 20 s when tab visible).

### 3.6 Transactions
- **Desktop:** AG Grid Community — columns: date, merchant, category mix, amount (`MoneyText` renderer), purchase status chip, payment status chip, PayPal order id / authorization id / capture id (copy buttons), final check (✓/✗). Server-side pagination & sort via `/transactions`; column filters for status and date range; CSV export (Community) of the current page; row click → detail.
- **Mobile:** card list with the same data.
- **Ledger tab:** AG Grid of `/ledger/entries` (transition, from → to bucket, amount, category, source, time) — makes the bucket model visible to judges.
- Detail: status timeline built from audit log + ledger entries (Proposed → Reserved → Authorized → Captured, or → Voided).

### 3.7 Settings
Profile (display name, time zone), security (logout everywhere), demo reset (admin + demo env only), about (Sandbox notice, version).

### 3.8 Checkout return / cancel
- `/checkout/return?purchaseId=…`: shows a 3-step progress ("Confirming with PayPal → Funds authorized → Final guardrail check → Captured"), calls `POST /purchases/{id}/capture` once (idempotency key from `sessionStorage` keyed by purchaseId — only an opaque key, no financial data), then polls `GET /purchases/{id}` every 2 s up to 30 s while `CAPTURE_PENDING`. Success → confetti-free success screen with remaining budget and "Back to shopping". `FINAL_CHECK_FAILED` → "Payment stopped by your rules — your PayPal authorization was voided, you weren't charged" with the reasons. It never marks success based on the URL.
- `/checkout/cancel?purchaseId=…`: "Payment not completed" with **Try again** (→ checkout) and **Cancel purchase**.

## 4. State and data

- Query keys: `['dashboard']`, `['policies']`, `['merchants', filters]`, `['approvals', status]`, `['purchase', id]`, `['conversation', id, 'messages']`, …
- After approve/reject/checkout/capture: invalidate `dashboard`, `approvals`, `purchase`, `ledger`.
- Access token in memory (React context); refresh via cookie on boot and on 401.
- TanStack Query cache: in-memory only (no persister). `queryClient.clear()` on logout.

## 5. Validation (Zod mirrors API)

```ts
export const Money = z.string().regex(/^\d{1,12}\.\d{2}$/);
export const PolicyDecisionSchema = z.object({
  evaluationId: z.string().uuid(),
  decision: z.enum(['ALLOWED','REQUIRES_APPROVAL','BLOCKED']),
  isAllowed: z.boolean(), requiresApproval: z.boolean(),
  reason: z.string(), reasonCode: z.string(),
  purchaseAmount: Money, remainingBudget: z.string().regex(/^-?\d{1,12}\.\d{2}$/).nullable(),
  currency: z.string().length(3),
  cartSnapshotHash: z.string().regex(/^[0-9a-f]{64}$/),
  reasons: z.array(z.object({ code: z.string(), severity: z.enum(['BLOCK','REQUIRE_APPROVAL','INFO']), message: z.string() }).passthrough()),
  budgetImpacts: z.array(z.any()),
}).passthrough();
```

## 6. Accessibility

- All cards keyboard navigable; decision badges include text, not colour alone.
- Approve button text includes the amount ("Approve $43.00").
- Live region announces agent replies and payment status changes.
- Respect `prefers-reduced-motion`.

## 7. Acceptance criteria

- [ ] Every number displayed for money comes from an API field (no client arithmetic beyond formatting).
- [ ] The approval panel can't be approved without a loaded `cartSnapshotHash` and a password entry.
- [ ] A `REAUTHENTICATION_FAILED` response never triggers token refresh or logout.
- [ ] Payment success UI only after `PurchaseDto.status == COMPLETED`.
- [ ] Works at 360 px width without horizontal scroll.
- [ ] Lighthouse PWA installable; Accessibility ≥ 90.
