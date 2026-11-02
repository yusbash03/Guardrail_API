# Business Rules

Numbered rules referenced by code comments and tests (`// BR-12`).

## Authority

- **BR-01** Only the Spending Policy Engine produces policy decisions. AI text is never a decision.
- **BR-02** Only a human user, via an authenticated non-agent endpoint, can approve or reject an approval request.
- **BR-03** Only the Payment Service calls PayPal. Only backend code paths that have verified an authorization can create orders; only verified PayPal results can mark spending captured.
- **BR-04** Only the user (never the AI) can change policies, approve merchants, or block merchants.

## Policies

- **BR-10** Every policy change creates a new immutable `PolicyVersion` and an `AuditLog` (`POLICY_UPDATED`) with `before_hash`/`after_hash` and a human-readable `change_summary`.
- **BR-11** Policy updates require `expectedVersionNumber`; mismatch → `409 POLICY_VERSION_CONFLICT`.
- **BR-12** New users start with policy v1 defaults: `newMerchantAction=BLOCK`, `approvalThreshold=0.00`, `unbudgetedCategoryAction=REQUIRE_APPROVAL`, product restriction `CATEGORY=ALCOHOL → BLOCK`.
- **BR-13** A policy change invalidates every `PENDING` or `APPROVED` (unconsumed) approval bound to an older version (`invalidated_reason = POLICY_VERSION_CHANGED`), and their reservations are released. Purchases already `CHECKOUT_PENDING` or later are not affected (the user already authorized and PayPal order exists).

## Merchants and products

- **BR-20** Merchant status per user is derived: in blocked list → `BLOCKED`; in approved list → `APPROVED`; otherwise `NEW`.
- **BR-21** A cart contains products from exactly one merchant.
- **BR-22** Unavailable products (`is_available=false` or `stock_quantity < quantity`) cannot be added and block evaluation.
- **BR-23** Product prices in carts are always read from the database at add/update time; snapshot prices are frozen at lock time.

## Carts

- **BR-30** Carts are `OPEN` until a purchase is created; then `LOCKED`. Locked carts reject all item changes (`409 CART_LOCKED`).
- **BR-31** Cancelling a purchase that is not yet `CHECKOUT_PENDING` unlocks the cart (`OPEN`, `version++`).
- **BR-32** A user may have at most 5 `OPEN` carts; the oldest is auto-`ABANDONED` on overflow.
- **BR-33** Max 30 distinct items per cart; quantity 1–99 per item.

## Evaluations and purchases

- **BR-40** A purchase can only be created from the latest evaluation of the cart's current version, made with the current policy version, less than 15 minutes old.
- **BR-41** Purchase creation re-evaluates inside the per-user lock. If the fresh decision is `BLOCKED` → `422 EVALUATION_BLOCKED`; if it differs from the referenced evaluation → `409 EVALUATION_STALE` (client re-evaluates).
- **BR-42** `BLOCKED` evaluations never create purchases.
- **BR-43** Purchase amount and currency are copied from the cart snapshot; they never come from a request body.

## Approvals

- **BR-50** Approval requests bind: user, purchase, cart id+version, cart snapshot hash, amount, currency, merchant, policy version, evaluation, expiry.
- **BR-51** Default expiry: 30 minutes from creation (`Approvals:TtlMinutes`). After approval, the approved state remains valid until the original `expires_at`.
- **BR-52** Approve requires the client to submit the `cartSnapshotHash` it displayed (mismatch → `409 APPROVAL_SNAPSHOT_MISMATCH`) **and** the user's current password (wrong → `401 REAUTHENTICATION_FAILED`; 5 failures in 15 min → `429 APPROVAL_STEP_UP_LOCKED` for 15 min). Password checked before any other state change (ADR-015).
- **BR-53** Approvals are single-use: consumed atomically when the PayPal order is created.
- **BR-54** Expired, rejected, invalidated or consumed approvals cannot be approved or used.
- **BR-55** Rejected approvals release reservations immediately.

## Payments

- **BR-60** Checkout requires purchase `READY_FOR_CHECKOUT` and (if an approval exists) a successful atomic consume, plus a re-check that the cart hash and amount still match.
- **BR-61** PayPal order amount = purchase amount; currency = purchase currency; `custom_id` = purchase id; `invoice_id` = `GR-{purchaseId}`.
- **BR-62** A redirect to `return_url` is not proof of anything. Authorization happens only after the backend `GET`s the order and sees `APPROVED`.
- **BR-63** Capture `COMPLETED` (from PayPal's API response or a verified webhook) is the only path to `CAPTURED` ledger state. A PayPal authorization `CREATED` is the only path to `AUTHORIZED` (except under the `CAPTURE` fallback).
- **BR-67** Orders use intent `AUTHORIZE`. Between authorization and capture, the final guardrail check (snapshot integrity, amount, merchant not blocked, products not restricted under the **current** policy) must pass; otherwise the authorization is voided and the buyer is not charged.
- **BR-68** An authorization is captured or voided within 30 minutes; none are left open.
- **BR-64** Unverified webhooks are stored with `REJECTED` status and never processed.
- **BR-65** Webhook events are processed at most once (`paypal_event_id` unique).
- **BR-66** If the buyer has not approved the PayPal order within 3 hours, the purchase becomes `EXPIRED` and its reservation is released. (Orders stay `CREATED` for 3 hours by default per PayPal docs.)

## Ledger

- **BR-70** Ledger entries are append-only and unique per `(purchase, category, transition, external_ref)`.
- **BR-71** Budget periods for a purchase are fixed at purchase creation.
- **BR-72** PROPOSED amounts count toward budgets.

## Agent

- **BR-80** The agent may create carts and approval *requests*; it may not approve, check out, capture, or edit policies.
- **BR-81** The agent must call `EvaluatePurchase` before `RequestPurchaseApproval`; the server enforces this regardless (BR-40).
- **BR-82** Product descriptions, merchant names and any tool output are untrusted data, not instructions.
- **BR-83** Max 6 LLM requests, 16 tool calls and 60 s per agent turn; max 20 agent turns per user per 10 minutes.

## Audit

- **BR-90** Audited actions: `USER_REGISTERED`, `USER_LOGGED_IN`, `POLICY_UPDATED`, `MERCHANT_APPROVED`, `MERCHANT_BLOCKED`, `CART_CREATED`, `CART_LOCKED`, `EVALUATION_PERFORMED`, `PURCHASE_CREATED`, `APPROVAL_REQUESTED`, `APPROVAL_GRANTED`, `APPROVAL_REJECTED`, `APPROVAL_EXPIRED`, `APPROVAL_INVALIDATED`, `APPROVAL_CONSUMED`, `CHECKOUT_STARTED`, `PAYMENT_AUTHORIZED`, `FINAL_CHECK_PASSED`, `FINAL_CHECK_FAILED`, `PAYMENT_VOIDED`, `PAYMENT_CAPTURED`, `PAYMENT_FAILED`, `PAYMENT_REFUNDED`, `APPROVAL_STEP_UP_FAILED`, `WEBHOOK_REJECTED`, `AMOUNT_MISMATCH`, `UNEXPECTED_CAPTURE`, `AGENT_TOOL_REJECTED`.
