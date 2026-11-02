# PayPal Webhook Contract

## Endpoint

```
POST /api/v1/webhooks/paypal
Anonymous (no JWT). Rate limit: 120/min per source IP. Max body: 64 KB.
Not routed through Vercel — PayPal calls Render directly.
```

## Required headers (from PayPal)

| Header | Maps to verify field |
|---|---|
| `PAYPAL-AUTH-ALGO` | `auth_algo` |
| `PAYPAL-CERT-URL` | `cert_url` |
| `PAYPAL-TRANSMISSION-ID` | `transmission_id` |
| `PAYPAL-TRANSMISSION-SIG` | `transmission_sig` |
| `PAYPAL-TRANSMISSION-TIME` | `transmission_time` |

Header lookup is case-insensitive.

## Processing pipeline

```
1. Read raw body as UTF-8 string (EnableBuffering; do not let model binding touch it).
2. If any required header missing → store event? NO (no event id trusted yet)
      → log WEBHOOK_HEADERS_MISSING, return 400.
3. Parse JSON minimally to get id and event_type (for logging/idempotency only; not trusted yet).
4. If paypal_event_id already exists with status PROCESSED/IGNORED → return 200 (duplicate delivery).
5. Verify with PayPal (postback):
      POST /v1/notifications/verify-webhook-signature
      {
        "auth_algo": "<header>",
        "cert_url": "<header>",
        "transmission_id": "<header>",
        "transmission_sig": "<header>",
        "transmission_time": "<header>",
        "webhook_id": "<PayPal:WebhookId>",
        "webhook_event": <raw body spliced verbatim>
      }
   Response: { "verification_status": "SUCCESS" | "FAILURE" }
6. FAILURE → insert row (verification_status FAILURE, status REJECTED), audit WEBHOOK_REJECTED, return 400.
   PayPal API error/timeout → do not insert; return 503 so PayPal retries.
7. SUCCESS → INSERT ... ON CONFLICT (paypal_event_id) DO NOTHING with status RECEIVED.
8. Return 200 immediately. WebhookProcessor handles the event asynchronously (≤ 5 s).
```

**Body splicing:** the verify request is built with `Utf8JsonWriter`, writing `webhook_event` via `WriteRawValue(rawBody)` so the event JSON is passed exactly as received. Re-serializing a parsed model can change formatting/number representation; we avoid that.

**Cert URL check (defence in depth):** reject if `PAYPAL-CERT-URL` host is not a `paypal.com` subdomain over HTTPS (relevant if we later switch to self-verification).

## Delivery semantics (from PayPal docs)

- PayPal expects a 2xx to confirm receipt.
- Non-2xx → PayPal retries up to 25 times over 3 days; then marks it failed (resendable from the dashboard).
- Therefore: return 2xx only once the event is durably stored; return 5xx for transient failures; return 4xx for forged/invalid events.
- Duplicates are expected → idempotent on `webhook_event.id`.

## WebhookProcessor

Claims batches: `SELECT … FROM paypal_webhook_events WHERE status='RECEIVED' ORDER BY received_at LIMIT 20 FOR UPDATE SKIP LOCKED`.

For each event, in one transaction:

| event_type | Lookup | Action |
|---|---|---|
| `CHECKOUT.ORDER.APPROVED` | `resource.id` = `paypal_order_id` | If purchase `CHECKOUT_PENDING`: payment `APPROVED`, purchase `PAYER_APPROVED` (no ledger change under `AUTHORIZE` intent; ledger `AUTHORIZE` under `CAPTURE` fallback). Authorization/capture is left to the capture endpoint / reconciler. |
| `PAYMENT.AUTHORIZATION.CREATED` | `resource.id` = `paypal_authorization_id`; else `resource.custom_id` **[VERIFY]** | If payment not yet `AUTHORIZED`: record authorization id, payment `AUTHORIZED`, purchase `PAYMENT_AUTHORIZED`, ledger `AUTHORIZE`. Never triggers capture by itself. |
| `PAYMENT.AUTHORIZATION.VOIDED` | `resource.id` = `paypal_authorization_id` | If we initiated the void: confirm (`PROCESSED`). If not and not captured: payment `VOIDED`, purchase `FAILED` (`AUTHORIZATION_VOIDED`), ledger `VOID`. |
| `PAYMENT.CAPTURE.COMPLETED` | `resource.id` = `paypal_capture_id`; else `resource.custom_id` = purchase id; else `resource.supplementary_data.related_ids.order_id` **[VERIFY]** | Check amount & currency equal purchase **and** that `payments.final_check_result` shows PASS (a capture we didn't request is flagged `UNEXPECTED_CAPTURE`, audited at Error). If not already `COMPLETED`: ensure `AUTHORIZE` exists (write if missing), write `CAPTURE`, payment `COMPLETED`, purchase `COMPLETED`. |
| `PAYMENT.CAPTURE.PENDING` | same | payment/purchase `CAPTURE_PENDING` (no ledger change) |
| `PAYMENT.CAPTURE.DECLINED` | same | payment `DECLINED`, purchase `FAILED`, ledger `FAIL`; void remaining authorization (best effort) |
| `PAYMENT.CAPTURE.REFUNDED` (P2) | refund resource → capture **[VERIFY]** | ledger `REFUND` (external_ref = refund id), update `refunded_amount`, purchase `REFUNDED`/`PARTIALLY_REFUNDED` |
| `PAYMENT.CAPTURE.REVERSED` (P2) | same as refunded | as refund + audit flag |
| `CHECKOUT.PAYMENT-APPROVAL.REVERSED` | order id **[VERIFY]** | if not captured: payment `FAILED`, purchase `FAILED`, ledger `RELEASE` or `FAIL` from current bucket |
| anything else | — | status `IGNORED` |

Outcomes: success → `PROCESSED`, `processed_at`; no matching payment → `IGNORED` with `last_error='NO_MATCH'`; exception → `attempts++`, `last_error`, remain `RECEIVED` until `attempts = 5`, then `FAILED` (alert log).

**Stale/out-of-order events:** transitions are only applied forward (PAYMENT_STATE_MACHINE.md). A `PENDING` event arriving after `COMPLETED` is ignored. A `COMPLETED` event arriving before `APPROVED` writes `AUTHORIZE` then `CAPTURE` (both idempotent).

**Optional confirmation (P1):** for `COMPLETED`/`DECLINED`, the processor `GET /v2/payments/captures/{id}` and uses that status, defending against mis-routed but validly-signed events.

## Example stored row

```json
{
  "paypal_event_id": "WH-2WR32451HC0233532-67976317FL4543714",
  "event_type": "PAYMENT.CAPTURE.COMPLETED",
  "resource_type": "capture",
  "resource_id": "2GG279541U471931P",
  "verification_status": "SUCCESS",
  "status": "PROCESSED",
  "related_payment_id": "0193…"
}
```
(IDs illustrative.)

## Local development

PayPal cannot reach `localhost`. Options: deploy to Render early; or use a tunnel (e.g. `cloudflared`, `ngrok`) and register a second Sandbox webhook. Integration tests use a fake verifier returning `SUCCESS`/`FAILURE`.
