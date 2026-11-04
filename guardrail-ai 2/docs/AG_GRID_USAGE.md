# AG Grid Usage — Guardrail AI

**Goal:** win **Best Use of AG Grid** (1st $5,000 · 2nd $2,000 · 3rd ×3 $1,000) while making Guardrail better, not just decorated.

**What the sponsor is looking for** (from the hackathon's AG Grid page, read 4 Nov 2026):
- **AG Studio** dashboards that look polished: custom widgets, theming and layout, not just stock AG Grid / AG Charts components.
- The **Studio Agent Framework**: a custom agent that works alongside Studio's built-in agents, or your own harness.
- Their suggested ideas include an **anomaly-alert agent** and a **natural-language finance agent that builds live widgets**. Both fit Guardrail.
- Participants get a **45-day trial licence** (via AG Grid's licence pricing page). A starter repo (`paypaldev/hackathon-paypal-ag-grid-boilerplate`) shows AG Studio with PayPal data. Support is on the PayPal Discord, and an AG Grid team member is a judge.

**Our answer, in one line:** a **Guardrails Insights** workspace built with AG Studio, where a custom **Guardrail Analyst** agent team explains your spending, catches anomalies and builds live dashboards from your own ledger. It is strictly read-only, so the AI still can't touch money. Plus AG Grid itself powering every dense table in the app.

---

## 1. Where AG Grid products are used

| # | Surface | Product | Why it's the right tool | Priority |
|---|---|---|---|---|
| 1 | **Insights** page (new nav item) | **AG Studio** (dashboards, custom widgets, theming) | Self-serve analytics over the user's ledger, purchases, policy decisions and prices | **P0 for this prize** |
| 2 | Insights agent | **Studio Agent Framework** (custom agents + custom tools, LLM via our backend) | "Ask your money a question" and get a live widget back; anomaly watchdog | **P0 for this prize** |
| 3 | Transactions → Purchases | AG Grid | Order / authorization / capture ids, status chips, final-check result | P1 (already planned) |
| 4 | Transactions → Ledger | AG Grid | Makes the bucket model (Proposed → Captured) visible | P1 |
| 5 | Approvals → History | AG Grid | Decisions, who requested (AI or user), time-to-approve | P1 |
| 6 | Spending Rules → Category budgets | AG Grid **cell editing** | Edit budgets inline with validation; one Save creates one policy version | P1 |
| 7 | Rules → Version history, Settings → Audit log | AG Grid | Compliance-style history with filters | P1 |

Mobile: Studio opens in **view mode** (`mode="view"`) with a single-column layout. Editing dashboards and grids is desktop-first. Tables fall back to card lists under 1024 px (FRONTEND_SPECIFICATION.md §3.6).

---

## 2. Guardrails Insights (AG Studio)

### 2.1 Placement and loading

- Route `/insights`, nav label **Insights** (Lucide `ChartNoAxesCombined`; use `BarChart3` if that icon isn't in the installed version), between Transactions and Settings.
- Lazy-loaded (`React.lazy`) so AG Studio's bundle never slows the PWA shell or the checkout pages.
- Packages: `ag-studio-react` + `ag-studio` (docs show 3.1.0; pin the exact version at install) **[VERIFY at install]**.
- Licence: `<AgStudioProvider licenseKey={import.meta.env.VITE_AG_STUDIO_LICENSE_KEY}>`. The key is a client-side product key, not a secret, but it still lives in env config, not in git.

```tsx
// features/insights/InsightsPage.tsx (shape, not final code)
<AgStudioProvider licenseKey={import.meta.env.VITE_AG_STUDIO_LICENSE_KEY}>
  <AgStudio
    data={{ sources: datasets }}          // from GET /api/v1/insights/datasets
    initialState={savedReport ?? defaultReport}
    widgets={guardrailWidgets}            // custom widgets, §2.4
    ai={guardrailHarness}                 // agent team, §3
    mode={isDesktop ? 'edit' : 'view'}
  />
</AgStudioProvider>
```

### 2.2 Data sources (all scoped to the logged-in user)

The data is small per user (hundreds to low thousands of rows), so it is passed **in memory**. Studio's built-in engine handles grouping, filtering and calculated fields, and we don't need a custom `AgDataEngine` for the MVP.

`GET /api/v1/insights/datasets` (JWT, user-scoped, read-only) returns:

| Source id | One row per | Fields |
|---|---|---|
| `purchases` | purchase | `purchaseId`, `createdAt`, `weekStart`, `monthStart`, `merchant`, `merchantStatus`, `amount`, `currency`, `status`, `decision`, `requestedBy` (USER/AGENT), `approvalMinutes`, `finalCheckPassed`, `paymentStatus` |
| `lineItems` | cart line in a purchase | `purchaseId`, `createdAt`, `merchant`, `product`, `categoryCode`, `unitPrice`, `quantity`, `lineTotal` |
| `ledger` | ledger entry | `occurredAt`, `purchaseId`, `transition`, `fromBucket`, `toBucket`, `categoryCode`, `amount`, `source`, `weekStart`, `monthStart` |
| `evaluations` | policy evaluation | `evaluatedAt`, `merchant`, `decision`, `primaryReasonCode`, `amount`, `categoryCodes`, `requestedBy` |
| `budgets` | budget × period | `scope`, `categoryCode`, `period`, `periodStart`, `limit`, `spent`, `pending`, `remaining` |
| `merchants` | merchant | `merchant`, `status`, `categoryCodes` |

Rules:
- Built server-side from the ledger, purchases and evaluations: the same sources of truth as the rest of the app. No new money maths in the browser.
- No emails, names, PayPal ids or payer data in these datasets (they also go to the LLM, §3.5).
- Money is sent as numbers with 2 dp for charting. Display formatting uses the currency formatter.

### 2.3 Default report (three pages, shipped as JSON)

Built once in the Studio UI, exported with `api.getState()`, and committed as `frontend/src/features/insights/reports/default-report.json`.

**Page 1 — Where my money goes**
- KPI widgets: Spent this month · Pending · Left this week · Purchases this month
- **Budget Burn-down** (custom widget) for Groceries weekly and Household monthly
- Spend by category per week (stacked column), spend by merchant (bar)
- Grid widget: last 20 purchases

**Page 2 — Guardrails at work** (the page that makes the product's point)
- **Money Protected** (custom KPI): $ value of BLOCKED evaluations + voided authorizations
- **Decision Funnel** (custom widget): Evaluated → Allowed / Needed approval → Approved → Paid, with Blocked and Voided branches
- Top block reasons (bar by `primaryReasonCode`)
- AI-requested vs user-requested purchases over time
- Median time-to-approve

**Page 3 — Price watch**
- Unit price over time per product and merchant (line), from `lineItems`
- "Cheapest approved merchant per item" grid widget

Page filters on every page: period, category, merchant.

**Persistence:** the user's edited report is saved through `stateUpdated` (debounced 2 s) and `studioPreDestroyed`, to `PUT /api/v1/insights/reports/default`. Always store the `version` that `getState()` returns. "Reset to default" reloads the committed JSON.

### 2.4 Custom widgets (Guardrail-branded)

Built with `AgWidgetDefinition` and registered through `createWidgets({ additionalTypes, menu })` under a **Guardrail** menu group. Each widget fetches data through `widgetApi.getData(...)`, uses `setDisplayState('loading' | 'displayed' | 'noData')`, supports cross-filtering, and defines `formatShape` + `ai` so agents can add and configure it.

| Widget id | What it shows | Data | Why it's special |
|---|---|---|---|
| `guardrail-budget-burn` | Burn-down of a budget across its period with an ideal-pace line; turns amber when ahead of pace and red past the limit | `budgets`, `ledger` | The core Guardrail concept as a chart |
| `guardrail-decision-funnel` | Funnel from evaluations to payments with Blocked / Voided side branches; click a stage to cross-filter | `evaluations`, `purchases` | Shows the policy engine working |
| `guardrail-money-protected` | Big KPI ("$186.40 kept inside your rules this month") with a sparkline | `evaluations`, `purchases` | A memorable judge-facing number |
| `guardrail-ledger-flow` | One purchase's journey Proposed → Reserved → Authorized → Captured (or Voided), with timestamps | `ledger` | Reuses the `StatusTimeline` look inside Studio |
| `guardrail-alerts` | Anomaly cards written by the Watchdog agent (§3.3) with severity and "Show me" links | agent output | Where AI insight lands as a widget |

**Theming:** a Guardrail theme built with the Theme Builder from DESIGN_SYSTEM.md tokens (Inter, tabular numbers, indigo primary, teal/amber/red decision colours, 14 px card radius), in light and dark. Grids inside widgets use `studioGridTheme`. Charts use `getChartTheme(api)` with a transparent background, so everything matches the rest of the app.

---

## 3. Studio Agent Framework — "Guardrail Analyst"

### 3.1 Principles (same guardrails as the shopping agent)

- **Read-only.** Insights agents can query datasets and arrange widgets. They have **no** tools that create carts, request or grant approvals, change policies, touch PayPal or write the ledger (ADR-003, ADR-016).
- **Keys stay server-side.** Studio's adapter calls our backend `POST /api/v1/insights/llm`, never Gemini directly (the AG Studio docs recommend this pattern).
- **Same free LLM** as the shopping agent (ADR-011): Gemini free tier with Groq fallback. It uses a **separate Google AI Studio project/key** so dashboard chat can't eat the shopping demo's quota.
- **Bounded:** each agent has `maxTurns: 10` (the framework default is 100). Backend limit: 10 insights turns per user per 10 minutes, and ≤ 8 LLM requests per turn.

### 3.2 Team (harness)

Built with `createAiHarness(api, ({ builtIn, tools }) => …)`. Every agent uses `directLlmRunner` with our adapter.

| Agent | Based on | Role | Tools |
|---|---|---|---|
| `lead` → **Guardrail Analyst** (primary) | `builtIn.lead` with Guardrail instructions | Understands money questions, plans, delegates | `delegateTo(['data','widget','page','watchdog'])`, plan tools, `get_spending_rules`, `explain_reason_code` |
| `data` | `builtIn.data` | Queries datasets, answers numeric questions | `viewSchema`, `executeQuery` |
| `widget` | `builtIn.widget` | Configures widgets (including the Guardrail custom widgets) | `viewWidget`, `configureWidget`, expression tools |
| `page` | `builtIn.page` | Places widgets and page filters | `viewPage`, `addWidget`, `positionWidget`, `removeWidget`, page-filter tools |
| `watchdog` (custom) | new definition | Anomaly alerts (§3.3) | `viewSchema`, `executeQuery`, `get_budget_status`, `publish_alerts` |

`planning` is left out to save free-tier requests: the lead's plan tools are enough.

Analyst instructions (summary): *"You analyse the user's own spending inside Guardrail. Only use numbers returned by tools. You cannot change rules, approve or make purchases — if the user wants that, point them to Spending Rules or AI Shopping. Prefer Guardrail widgets for budgets, decisions and protected money. Keep answers short and cite the period."*

### 3.3 Watchdog: the anomaly-alert agent

Runs once when Insights opens (via `defaultToolCalls`) and on demand ("Check for anything unusual"). The checks are deterministic SQL-like queries over the datasets. The model only ranks them and explains them in words:

| Check | Example alert |
|---|---|
| Budget pace | "Groceries is at 54% with 5 days left — on pace to finish at $92 vs $80." |
| Merchant spike | "HomeBasics spend is 3× your 8-week average." |
| Blocked attempts | "4 blocked attempts at QuickCart Express this month — consider keeping it blocked or approving it." |
| Price change | "Greek Yogurt at FreshMart rose from $3.99 to $4.50." |
| AI vs user mix | "The assistant prepared 70% of purchases this month; all were approved by you." |

`publish_alerts` (a custom tool, `api.defineAiTool`) writes the result into the `guardrail-alerts` widget. Alerts are *advice only*; any action links to the normal, human-only screens.

### 3.4 Custom tools (all read-only)

Defined with `api.defineAiTool({ name, description, params: (s) => …, execute: async (args, ctx) => … })`. Each forwards `ctx.signal` and returns `ctx.success(...)` or `ctx.error(...)`. Optional params are modelled as unions with `null`, because some providers require every param to be listed as required.

| Tool | Calls | Returns |
|---|---|---|
| `get_spending_rules` | `GET /api/v1/policies` | Compact policy summary (budgets, threshold, merchant lists) |
| `get_budget_status` | `GET /api/v1/ledger/summary` | Limit / spent / pending / remaining per category and period |
| `explain_reason_code` | static catalogue (POLICY_EVALUATION_CONTRACT.md) | Plain-language meaning of a reason code |
| `publish_alerts` | local (Studio state) | Updates the `guardrail-alerts` widget |
| `draft_rule_suggestion` | local | Text such as "Raise Household monthly to $60?" shown as a card with **Open in Spending Rules**. The agent never applies it |

### 3.5 LLM adapter and backend proxy

**Frontend adapter:** implements `executeTurn(request, options)` and returns `{ stream, complete }` synchronously. It POSTs the `AgLlmRequest` to `/api/v1/insights/llm` with `signal: options.signal` and parses an NDJSON stream of AG-UI-style events (`TEXT_MESSAGE_START/CONTENT/END`, `TOOL_CALL_START/ARGS/END`). The adapter doesn't execute tools; Studio does.

**Backend `POST /api/v1/insights/llm`** (JWT, rate-limited):
1. Validates the request size (≤ 64 KB) and that the tool names are on the insights allowlist (Studio built-ins + the 5 custom tools above). Anything else gets `400 VALIDATION_FAILED`.
2. Calls Gemini's OpenAI-compatible chat completions with `stream: true` (Groq fallback), passing `instructions`, `input` and `tools`.
3. Translates provider deltas into AG-UI events and streams them as `application/x-ndjson`. Errors are reported on the final `complete` record (`status`, `error`), not as stream events, as the AG Studio docs require.
4. Logs model, tokens and duration per turn (no prompt bodies at Info level).

Data sent to the model: the user's own **query results**, which contain fictional merchant/product names and amounts, never PII or payment identifiers (consistent with SEC-21 and the free-tier training caveat in ADR-011).

### 3.6 Chat panel

Built-in Studio chat panel with `promptStarters`:
- "Where did my grocery money go this month?"
- "Show what my rules blocked and why"
- "Build a price-watch chart for milk and eggs"
- "Anything unusual this month?"

There's no model picker (omit `models`). Thread history is stored with a small `history` store backed by `GET/PUT /api/v1/insights/threads` (P2). Until then threads stay in memory.

---

## 4. AG Grid in the rest of the app

One shared wrapper, `components/DataGrid.tsx`, with a theme from `themeQuartz.withParams({...})` mapped to our CSS tokens (font, row height 40, borders, hover, dark mode), tabular numbers, and shared cell renderers:

| Renderer / feature | Used for |
|---|---|
| `MoneyCell` (valueFormatter + `Intl.NumberFormat`) | All amounts |
| `StatusChipCell` | Purchase / payment / approval statuses (icon + text) |
| `DecisionCell` | ALLOWED / REQUIRES APPROVAL / BLOCKED |
| `CopyIdCell` | PayPal order / authorization / capture ids |
| `rowClassRules` | Failed/voided rows tinted red, pending rows grey |
| Pinned bottom row | Totals (sum of captured, pending) |
| Quick filter + floating filters | Search and per-column filtering |
| CSV export | "Export CSV" on Transactions and Ledger |
| Column state persistence | Saved per user in `localStorage` (layout only, not data) |
| Server-side paging/sort | `/transactions`, `/ledger/entries`, `/audit-logs` (`page`, `pageSize` ≤ 100, `sort`) |

**Editable Category Budgets (Spending Rules):** an AG Grid with `editable` cells, a select editor for period (`WEEKLY`/`MONTHLY`) and a number editor for limit. Cells are validated with the same Zod schema as the API. Invalid cells are highlighted, and Save is disabled until they're fixed. **Save** sends one `PUT /policies`, which creates one policy version (ADR-004). Unsaved edits show a sticky "Unsaved changes" bar.

**With the trial licence, also turn on** (Enterprise features, low effort, high polish):
- Transactions: **row grouping by week → merchant** with sum aggregation; **master/detail** (purchase row expands to its ledger entries); status bar with totals; Excel export.
- Price grid: **sparklines** of unit price history per product **[VERIFY sparklines availability in the installed version]**.

If the licence becomes a problem, all of the above degrades to Community features without changing the screens' purpose.

---

## 5. API and data additions

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/insights/datasets?from=&to=` | The six datasets in §2.2 (default: last 90 days) |
| GET | `/api/v1/insights/reports/{name}` | Saved Studio state (or 404 → client loads the default) |
| PUT | `/api/v1/insights/reports/{name}` | `{ state, version }` → save |
| POST | `/api/v1/insights/llm` | LLM proxy for Studio agents (§3.5), NDJSON stream |
| GET/PUT | `/api/v1/insights/threads` (P2) | Chat thread history |

New table:

```sql
create table insight_reports (
  id           uuid primary key,
  user_id      uuid not null references users(id) on delete cascade,
  name         text not null check (length(name) between 1 and 60),
  state        jsonb not null,          -- AG Studio state as returned by api.getState()
  state_version text,                   -- Studio's state.version
  created_at   timestamptz not null,
  updated_at   timestamptz not null,
  unique (user_id, name)
);
```

Config: `Insights__Llm__ApiKey` (separate Gemini key), `Insights__MaxTurnsPerTenMinutes` = `10`; frontend `VITE_AG_STUDIO_LICENSE_KEY`.

**Demo history seed:** so the dashboards aren't empty, the demo user gets ~8 weeks of **seeded past purchases** (completed, with full ledger entries) and ~15 past evaluations (including blocked QuickCart attempts), all in earlier weeks. The current week stays at $0 groceries so the live demo still shows $80 → $37. It's labelled as demo data in the seed code and the README.

---

## 6. Demo moment (≈ 25 s, fits the < 3 min video)

1. Open **Insights → Guardrails at work**: Money Protected KPI, Decision Funnel, Budget Burn-down already updated with the $43 purchase.
2. Type in Studio's chat: *"Show what my rules blocked this month and why."* The Analyst queries `evaluations` and adds a bar chart of block reasons, live.
3. The Watchdog alert card: *"4 blocked attempts at QuickCart Express this month."*
4. Voice-over: "Even here, the AI can read and explain — never spend."

## 7. Submission notes for the AG Grid prize

- In the Devpost write-up, a section **"How we used AG Grid"**: AG Studio Insights, five custom widgets, Guardrail theme, a custom agent team (Analyst + Watchdog) with read-only custom tools over our own backend LLM proxy, plus AG Grid in five app screens including inline policy editing.
- Screenshots: Insights page 2, Studio chat building a widget, Watchdog alerts, Transactions grid with master/detail.
- Mention the **security design**: tool allowlist and read-only tools, which is the AG Studio blog's own advice ("restrict tools rather than relying on prompts").

## 8. Build plan and risks

| When | Work |
|---|---|
| Day 6–7 (with the agent work) | Request the 45-day trial; install Studio; datasets endpoint; default report built in the UI and exported |
| Day 8 | Custom widgets (`budget-burn`, `decision-funnel`, `money-protected`), Guardrail theme; AG Grid wrapper + Transactions/Ledger |
| Day 9 | Agent harness, adapter + `/insights/llm` proxy, Watchdog + custom tools; editable budgets grid |
| Day 10 | Polish, screenshots, demo beat |

| Risk | Mitigation |
|---|---|
| **Trial expires before judging ends** (45 days; winners announced 21 Dec) | Request the trial no earlier than ~6 Nov so it runs to ~21 Dec; ask the AG Grid team on the PayPal Discord whether keys can be extended through judging |
| Free LLM quota shared with the shopping agent | Separate Gemini key/project for Insights; low `maxTurns`; per-user limits |
| Studio agent makes many LLM calls (5-agent team) | Drop `planning`; `maxTurns: 10`; backend caps per turn |
| Bundle size | Lazy-load `/insights` |
| Some AG Studio docs details unverified (event names, exact versions) | Pin versions on Day 6 and adjust; the docs flag `stateUpdated` vs `onStateUpdated` naming |
| Time | If Day 9 slips: ship Studio + custom widgets + built-in agents via our adapter; drop the Watchdog and the editable budgets grid |

## 9. References

- Hackathon AG Grid details: paypalaihackathon.devpost.com/details/aggrid
- Boilerplate: github.com/paypaldev/hackathon-paypal-ag-grid-boilerplate
- AG Studio: quick start, custom widgets, state, licence install, server-side data, agents overview, built-in harness, built-in agents, built-in tools, custom tools, direct LLM runner (ag-grid.com/studio/react/…)
- AG Studio Agent Framework announcement (AG Grid blog, 28 Sep 2026)
- AG Grid React: key features, Community vs Enterprise (ag-grid.com/react-data-grid/…)
