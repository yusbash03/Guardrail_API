# PayPal Integration

**Environment:** PayPal Sandbox only (`https://api-m.sandbox.paypal.com`).
**APIs:** OAuth 2.0 client credentials, Orders v2 (create, get, authorize), Payments v2 (capture authorization, void authorization, get), Webhooks (verify signature).
**Authority:** official PayPal developer documentation. Items marked **[VERIFY]** are reasonable expectations that must be confirmed against the docs or Sandbox responses during implementation; they are not to be treated as PayPal specifications.

References used:
- Orders v2 — Create order: `POST /v2/checkout/orders` (docs.paypal.ai/reference/api/rest/orders/create-order; github.com/paypal/paypal-rest-api-specifications `checkout_orders_v2.json`)
- Standard integration use case (create → approve → authorize/capture): developer.paypal.com/api/rest/integration/orders-api/api-use-cases/standard/
- Delayed capture (authorize, capture authorization; 29-day validity, 3-day honor period): docs.paypal.ai/payments/methods/paypal/delayed-capture
- Authorize payment for order: docs.paypal.ai/reference/api/rest/orders/authorize-payment-for-order
- Void authorization: developer.paypal.com/beta/payments/void-authorization
- Verify webhook signature: `POST /v1/notifications/verify-webhook-signature` (developer.paypal.com/api/webhooks/v1/)
- Webhook event names: developer.paypal.com/api/rest/webhooks/event-names
- Webhooks overview (delivery and retries): developer.paypal.com/api/rest/webhooks/

## 1. What this integration is — and is not

- It **is** standard PayPal Checkout via Orders v2: the backend creates an order, the **buyer approves it in PayPal**, the backend captures it.
- It **is not** autonomous access to a buyer's PayPal account. Every order requires the buyer's approval in PayPal's UI. The AI never touches PayPal.
- The Guardrail Sandbox **business** account is the payee for every order (ADR-012). Seeded merchants are fictional catalogue sellers, shown in item/description metadata.

## 2. Configuration

| Key | Example | Secret |
|---|---|---|
| `PayPal:BaseUrl` | `https://api-m.sandbox.paypal.com` | no |
| `PayPal:ClientId` | Sandbox REST app client id | yes |
| `PayPal:ClientSecret` | Sandbox REST app secret | yes |
| `PayPal:WebhookId` | id of the webhook registered on the app | yes (treat as secret) |
| `PayPal:Intent` | `AUTHORIZE` (fallback `CAPTURE`) | no |
| `PayPal:BrandName` | `Guardrail AI` | no |
| `PayPal:ReturnUrl` | `https://<frontend>/checkout/return` | no |
| `PayPal:CancelUrl` | `https://<frontend>/checkout/cancel` | no |

Startup validation fails fast if `BaseUrl` is not the Sandbox host while `ASPNETCORE_ENVIRONMENT != Production-Live` (a value we never use) — i.e. Live is impossible to enable by accident.

## 3. OAuth 2.0

```
POST /v1/oauth2/token
Authorization: Basic base64(client_id:client_secret)
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
```
Response includes `access_token` and `expires_in` (seconds). `PayPalTokenProvider` caches the token in memory until `expires_in − 120 s`, guarded by a `SemaphoreSlim` to avoid stampedes. On a `401` from any PayPal call, invalidate and retry once.

## 4. Create order (checkout)

Called by `CheckoutUseCase` **after** approval verification (PURCHASE_AUTHORIZATION.md).

```
POST /v2/checkout/orders
Authorization: Bearer <token>
Content-Type: application/json
PayPal-Request-Id: <payments.paypal_create_request_id>   (UUID, stable per payment row)
Prefer: return=representation
```
```json
{
  "intent": "AUTHORIZE",
  "purchase_units": [{
    "reference_id": "<purchaseId>",
    "custom_id": "<purchaseId>",
    "invoice_id": "GR-<purchaseId>",
    "description": "Guardrail AI purchase — FreshMart",
    "amount": {
      "currency_code": "USD",
      "value": "43.00",
      "breakdown": { "item_total": { "currency_code": "USD", "value": "43.00" } }
    },
    "items": [
      { "name": "Whole Milk 1 gal", "sku": "FM-MLK-1G", "quantity": "1",
        "unit_amount": { "currency_code": "USD", "value": "4.29" } }
    ]
  }],
  "payment_source": {
    "paypal": {
      "experience_context": {
        "brand_name": "Guardrail AI",
        "user_action": "PAY_NOW",
        "shipping_preference": "NO_SHIPPING",
        "return_url": "https://<frontend>/checkout/return?purchaseId=<purchaseId>",
        "cancel_url": "https://<frontend>/checkout/cancel?purchaseId=<purchaseId>"
      }
    }
  }
}
```

Notes grounded in the docs:
- `intent` is `CAPTURE` or `AUTHORIZE`; we use `AUTHORIZE` (ADR-005), configurable via `PayPal:Intent` for the fallback.
- With `amount.breakdown`, `item_total` must equal Σ `unit_amount × quantity`. Our cart line totals already satisfy this; a unit test asserts it before calling PayPal.
- `invoice_id` must be unique per merchant account by default — `GR-<purchaseId>` is unique.
- `custom_id` is for reconciliation and is not shown to the payer.
- `application_context` is deprecated; experience fields go in `payment_source.paypal.experience_context`.
- When `payment_source` is supplied, the documented response status is `PAYER_ACTION_REQUIRED` with a link `rel: "payer-action"`; without it, `CREATED` with `rel: "approve"`. **The client accepts either rel** and stores it as `approve_url`.
- `PayPal-Request-Id` keys are retained for 6 hours by default; a repeated request with the same key returns `200` with the original result.
- The payer must be redirected within 6 hours of creation and approve within 6 hours of redirect (per the create-order reference); an order stays `CREATED` for 3 hours by default (per the standard use case). We apply the stricter **3-hour** checkout expiry (BR-66).
- `shipping_preference: NO_SHIPPING` is a **demo assumption** (no real fulfilment). **[VERIFY]** enum value in current schema.

Stored on `payments`: `paypal_order_id`, `status` (mapped from order status), `approve_url`, `last_paypal_debug_id`.

## 5. Buyer approval

1. Frontend receives `approveUrl` from `POST /purchases/{id}/checkout` and does `window.location.assign(approveUrl)`.
2. Buyer logs in with a **Sandbox personal account** and approves.
3. PayPal redirects to `return_url` (or `cancel_url`). PayPal appends query parameters (commonly `token` = order id and `PayerID`) **[VERIFY]**. The backend does **not** rely on them; it uses the stored order id for the purchase.

## 6. Complete payment: verify → authorize → final check → capture

`POST /api/v1/purchases/{id}/capture` (user JWT, `Idempotency-Key`) — called by the return page. Each step is idempotent and resumable from the purchase's current status; no DB transaction is held across a PayPal call.

1. **Load** purchase + active payment `FOR UPDATE` (short tx). Allowed statuses: `CHECKOUT_PENDING`, `PAYER_APPROVED`, `PAYMENT_AUTHORIZED`, `CAPTURE_PENDING`, `COMPLETED` (→ return current state).
2. **Verify buyer approval** (if `CHECKOUT_PENDING`): `GET /v2/checkout/orders/{orderId}`. Require `status == "APPROVED"`; `purchase_units[0].amount.value == purchase.amount`; `currency_code == purchase.currency`; `custom_id == purchaseId`. Else `409 PAYMENT_NOT_APPROVED` / `422 AMOUNT_MISMATCH`. Record payment `APPROVED`, purchase `PAYER_APPROVED`.
3. **Authorize** (if `PAYER_APPROVED`):
   ```
   POST /v2/checkout/orders/{orderId}/authorize
   Authorization: Bearer <token>
   Content-Type: application/json
   PayPal-Request-Id: <payments.paypal_authorize_request_id>
   Prefer: return=representation
   ```
   Body `{}`. Read `purchase_units[0].payments.authorizations[0]` (`id`, `status`, `amount`, expiration) — path per PayPal's delayed-capture guide. Authorization `CREATED` → payment `AUTHORIZED`, `paypal_authorization_id`, ledger `AUTHORIZE`, purchase `PAYMENT_AUTHORIZED`. Denied → payment `DECLINED`, purchase `FAILED`, ledger `RELEASE`. `422` instrument declined → `PAYMENT_INSTRUMENT_DECLINED` with `retryUrl = approve_url`.
4. **Final guardrail check** (if `PAYMENT_AUTHORIZED`): PAYMENT_STATE_MACHINE.md §1. Store result in `payments.final_check_result`.
   - Fail → **void**:
     ```
     POST /v2/payments/authorizations/{authorizationId}/void
     PayPal-Request-Id: <payments.paypal_void_request_id>
     ```
     → payment `VOIDED`, ledger `VOID`, purchase `FAILED` (`FINAL_CHECK_FAILED`), response `422 FINAL_CHECK_FAILED` with reasons. The buyer is not charged.
5. **Capture authorization**:
   ```
   POST /v2/payments/authorizations/{authorizationId}/capture
   Authorization: Bearer <token>
   Content-Type: application/json
   PayPal-Request-Id: <payments.paypal_capture_request_id>
   Prefer: return=representation
   ```
   Body `{}` (full capture; partial capture is supported by PayPal but not used). Response is the capture resource:
   - `COMPLETED` → payment `COMPLETED`, `paypal_capture_id`, `captured_amount`; ledger `CAPTURE`; purchase `COMPLETED`; cart `CHECKED_OUT`.
   - `PENDING` → payment/purchase `CAPTURE_PENDING`; wait for webhook / reconciler.
   - `DECLINED` / `FAILED` → ledger `FAIL`, purchase `FAILED`; then void the authorization (best effort; reconciler retries).
6. **Errors**
   - `422 AUTHORIZATION_EXPIRED` / `AUTHORIZATION_ALREADY_CAPTURED` (documented negative-test errors) → GET the authorization and reconcile.
   - `404` invalid authorization id → log Error, reconcile from order.
   - `5xx`/timeout → keep current status, return `202 CAPTURE_IN_PROGRESS`; retries reuse the same `PayPal-Request-Id`; reconciler finishes it.
7. Every PayPal error logs `debug_id` and stores it on the payment.

Timing: we capture immediately after the final check (seconds after authorization), well within PayPal's 3-day honor period and 29-day validity. Authorizations are never left open: the reconciler voids any authorization older than 30 minutes whose purchase is not progressing.

The authorize/capture responses are server-to-server PayPal results and are sufficient to record state. Webhooks are an independent confirmation.

### Fallback: `PayPal:Intent=CAPTURE`
Step 3 is skipped; the final check runs before `POST /v2/checkout/orders/{orderId}/capture` (same headers, `paypal_capture_request_id`). See PAYMENT_STATE_MACHINE.md §5.

## 7. Webhooks

See PAYPAL_WEBHOOK_CONTRACT.md. Subscribed events:

| Event | Use |
|---|---|
| `CHECKOUT.ORDER.APPROVED` | Buyer approved → purchase `PAYER_APPROVED` if the return trip never happened (e.g. buyer closed tab). The reconciler then runs steps 3–5 (P1). |
| `PAYMENT.AUTHORIZATION.CREATED` | Confirm authorization (ledger `AUTHORIZE` if missing) |
| `PAYMENT.AUTHORIZATION.VOIDED` | Confirm void; if unexpected (not initiated by us), purchase `FAILED`, ledger `VOID` |
| `PAYMENT.CAPTURE.COMPLETED` | Confirm/complete capture |
| `PAYMENT.CAPTURE.PENDING` | Mark pending |
| `PAYMENT.CAPTURE.DECLINED` | Fail |
| `PAYMENT.CAPTURE.REFUNDED` | Refund (P2) |
| `PAYMENT.CAPTURE.REVERSED` | Reversal (P2: treat like refund + flag) |
| `CHECKOUT.PAYMENT-APPROVAL.REVERSED` | Approval reversed before capture → fail purchase, release |

Note: v2 uses `PAYMENT.CAPTURE.DECLINED` (v1 used `PAYMENT.CAPTURE.DENIED`). `CHECKOUT.ORDER.COMPLETED` is documented for marketplaces/platforms and is not subscribed.

## 8. Reconciliation (P1)

`PaymentReconciler` every 10 min (plus a Render Cron Job backstop, ADR-020), for payments in `CREATED`, `PAYER_ACTION_REQUIRED`, `APPROVED`, `AUTHORIZED`, `CAPTURE_PENDING` older than 10 min:
- `GET /v2/checkout/orders/{id}`; if an authorization id exists, `GET /v2/payments/authorizations/{id}`; if a capture id exists, `GET /v2/payments/captures/{id}`.
- `APPROVED` with no authorization → continue steps 3–5 (the user already consented; the final check still applies).
- `AUTHORIZED` older than 30 min without capture progress → void.
- Apply the same state transitions as §6/webhooks, source `RECONCILER`.
- Orders still not approved after 3 h → purchase `EXPIRED`, `RELEASE`, payment `VOIDED` (internal state; no PayPal call needed for an unapproved order).

## 9. Sandbox setup checklist

1. developer.paypal.com → Apps & Credentials → **Sandbox** → create REST app "Guardrail AI".
2. Copy client id/secret → Render env vars.
3. Add webhook: URL `https://<render-host>/api/v1/webhooks/paypal`, events from §7 → copy Webhook ID → `PayPal:WebhookId`.
4. Sandbox accounts: one Business (payee, attached to app), one Personal (buyer) with balance. Keep buyer credentials for the demo (not in repo).
5. Smoke test with the `scripts/paypal-smoke` command (create → manual approve → capture) before UI work.

## 10. Unresolved / to verify

- Authorization resource status enum and expiration field name **[VERIFY]**.
- Capture-authorization response fields (`id`, `status`, `amount`) **[VERIFY]**.
- Sandbox business account eligibility for `AUTHORIZE` (Day-1 spike; fallback `CAPTURE`) **[VERIFY]**.
- Exact 422 issue code for a declined instrument on authorize (e.g. `INSTRUMENT_DECLINED`) **[VERIFY]**.
- Resource shape of `PAYMENT.CAPTURE.*` events (expected: capture object with `id`, `status`, `amount`, `custom_id`, `supplementary_data.related_ids.order_id`) **[VERIFY]**.
- Resource shape of `PAYMENT.CAPTURE.REFUNDED` (expected: refund object linking to capture) **[VERIFY]**.
- PayPal Agent Toolkit / MCP server: evaluated and **deliberately not connected to the agent** (ADR-016).
