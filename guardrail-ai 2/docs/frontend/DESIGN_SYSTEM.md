# Design System

Premium consumer-fintech feel: calm, precise, trustworthy. Built on shadcn/ui primitives with CSS-variable tokens (light + dark).

## 1. Brand

- **Name/wordmark:** "Guardrail" in semi-bold + "AI" in a muted pill.
- **Mark:** a rounded shield formed by two parallel rails (simple geometric SVG, original).
- **Voice:** direct, reassuring, numeric. "You have $37.00 left this week." Not "Great news!!".

## 2. Colour tokens (HSL, shadcn convention)

| Token | Light | Dark | Use |
|---|---|---|---|
| `--background` | 220 20% 98% | 222 30% 7% | page |
| `--card` | 0 0% 100% | 222 25% 10% | surfaces |
| `--foreground` | 222 30% 12% | 210 20% 96% | text |
| `--muted-foreground` | 220 10% 42% | 215 15% 65% | secondary text |
| `--primary` | 243 75% 58% (indigo) | 243 85% 68% | primary actions |
| `--accent` | 168 70% 38% (teal) | 168 60% 48% | positive money, remaining |
| `--decision-allowed` | 152 60% 36% | 152 55% 48% | ALLOWED |
| `--decision-approval` | 38 92% 45% | 38 92% 55% | REQUIRES_APPROVAL |
| `--decision-blocked` | 0 72% 50% | 0 75% 60% | BLOCKED |
| `--pending` | 220 14% 70% | 220 10% 40% | pending/reserved segments |
| `--border` | 220 15% 90% | 222 20% 18% | |
| `--ring` | = primary | = primary | focus |

All text/background pairs ≥ 4.5:1. Decision colours always paired with an icon + label.

## 3. Typography

- **Inter** (variable) for UI; `font-feature-settings: "tnum"` (tabular numbers) for all money.
- Scale: 12 / 14 / 16 (base) / 18 / 24 / 32 / 40. Headings 600, body 400, money 600.
- Large money figures: 32–40 px, tight tracking (−0.02em).

## 4. Spacing, radius, elevation

- 4 px grid; page padding 16 (mobile) / 32 (desktop).
- Radius: `--radius: 0.875rem` (cards), buttons 0.625rem, chips full.
- Shadows: subtle (`0 1px 2px rgb(0 0 0 / .05)`, raised `0 8px 24px rgb(0 0 0 / .08)`); dark mode uses borders instead.

## 5. Core components

| Component | Base | Notes |
|---|---|---|
| `MoneyText` | span | `Intl.NumberFormat(locale, { style: 'currency', currency })`; negative in blocked colour |
| `DecisionBadge` | Badge | icon (CheckCircle2 / ShieldAlert / Ban) + label |
| `BudgetBar` | div | three segments: spent (accent), pending (striped `--pending`), remaining (track); over-limit hatch in blocked colour |
| `BudgetRing` | Recharts RadialBarChart | centre label = remaining |
| `ReasonList` | Accordion | severity icon, message, rule type tag |
| `ApprovalPanel` | Card | line items, total, impacts, fingerprint, countdown, Approve/Reject |
| `StepUpDialog` | Dialog | "Confirm with your password", amount + merchant in title, password input, inline error, lockout countdown |
| `DataGrid` | AG Grid Community | themed via AG Grid Theming API mapped to our tokens (fonts, borders, row hover, dark mode); tabular numbers; status/money cell renderers |
| `StatusTimeline` | ol | Proposed → Reserved → Authorized → Final check → Captured (or Voided) with timestamps |
| `MerchantStatusChip` | Badge | APPROVED (teal), BLOCKED (red), NEW (gray) |
| `AgentMessage` | div | avatar (shield mark), markdown-lite text (no HTML), cards slot |
| `ToolTrace` | Collapsible | "Searched 4 products · Built cart · Checked your rules" |
| `PayPalButton` | Button | PayPal-yellow style **not** imitated; use our primary button with label "Pay with PayPal" + small PayPal wordmark only if permitted by PayPal brand guidelines **[VERIFY]**; otherwise text only |

## 6. Icons (Lucide)

Overview `LayoutDashboard`, AI Shopping `Sparkles`, Rules `ShieldCheck`, Merchants `Store`, Approvals `BadgeCheck`, Transactions `ReceiptText`, Settings `Settings`, Budget `Wallet`, Blocked `Ban`, Approval `ShieldAlert`.

## 7. Motion

150–200 ms ease-out for hovers/sheets; budget bars animate width once on mount; no motion when `prefers-reduced-motion`.

## 8. Content rules

- Always show currency symbol and two decimals.
- Dates: relative for < 24 h ("2 min ago"), else `MMM d, h:mm a` in profile time zone.
- Never say "approved" for a purchase unless `ApprovalStatus == APPROVED/CONSUMED`; never say "paid" unless `PurchaseStatus == COMPLETED`.
