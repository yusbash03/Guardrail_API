# Product Vision — Guardrail AI

> **Your money. Your rules. Your AI.**

## 1. Problem

AI agents can now search, compare and assemble purchases faster than people can. What stops people from letting them is trust: an agent that can spend money can overspend, buy from the wrong seller, buy the wrong thing, or be talked into a purchase by text hidden in a product page.

Most "AI shopping" demos skip this. They either never pay, or they hand the model a payment tool and hope.

## 2. Vision

Guardrail AI lets a person hand routine shopping to an AI agent while **every cent stays inside rules the person wrote**. The agent is fast and helpful; the rules are deterministic and final.

The product rests on one principle:

> **The AI proposes. The Spending Policy Engine decides. The human authorizes. The backend pays.**

No language model output is ever treated as a permission, a policy result, an approval, or proof of payment.

## 3. What the user gets

| Capability | Who does it |
|---|---|
| Understand "get me groceries for the week under budget" | AI agent |
| Search the catalogue, compare prices, suggest swaps | AI agent (via read-only tools) |
| Build a cart | AI agent (via validated cart service; prices come from the database) |
| Decide whether the cart is allowed | Spending Policy Engine (deterministic code) |
| Decide whether a human must approve, and verify that approval | Authorization Service |
| Create and capture the PayPal order | Payment Service (backend only, never the AI) |
| Approve the payment in PayPal | The buyer, in PayPal's own UI |
| Record what was spent | Spending Ledger (driven by verified PayPal results only) |
| Explain what happened and what's left | AI agent (reading ledger tools) |

## 4. Differentiators (for judges)

1. **Policy-as-authority, not prompt-as-authority.** Budgets, merchant lists, thresholds and product bans live in versioned database records and are enforced in code. The model cannot reach them except through read-only tools.
2. **Approval bound to an exact cart.** An approval is cryptographically tied (SHA-256 snapshot hash) to the cart contents, amount, currency, merchant, policy version and an expiry. Change one item and the approval is void.
3. **Honest payments.** PayPal Orders v2 with a server-side capture and verified webhooks. A browser redirect is never treated as proof of payment.
4. **A real ledger.** Proposed → reserved → authorized → captured → released/refunded, with concurrency-safe reservations so two simultaneous carts can't both spend the last $20.
5. **Explainable decisions.** Every decision returns machine-readable reason codes that the UI renders and the AI explains in plain language.

## 5. Target outcomes for the hackathon

- A live, deployed demo (Vercel + Render + Neon + PayPal Sandbox) that runs the 14-step core scenario and the "unapproved merchant" rejection in under 4 minutes.
- Clear evidence of meaningful PayPal use (Orders v2, OAuth, webhooks with signature verification, reconciliation) and meaningful AI use (tool-calling agent with conversation memory).
- A codebase whose architecture makes the safety claims checkable: tests prove the AI cannot bypass policy.

## 6. Non-goals

- Fully autonomous payment without the buyer's PayPal approval. Ordinary PayPal Orders API checkout requires the buyer to approve each order in PayPal. Guardrail AI does **not** claim otherwise.
- Real merchant integrations, scraping, or live inventory.
- Multi-seller payouts (would require PayPal multiparty/platform onboarding).
- Production money movement. Sandbox only.

## 7. Long-term direction (post-hackathon, not committed)

- Saved payment methods / vaulting so pre-authorized low-risk purchases need fewer taps (subject to PayPal product eligibility — unverified for this use case).
- Household accounts with multiple approvers.
- Merchant provider plug-ins for real catalogues.
- Recurring baskets ("same as last week, within budget").
