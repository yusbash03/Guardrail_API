using System.Globalization;
using System.Text.Json;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Common;
using Guardrail.Domain.Payments;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Guardrail.Application.Payments;

public enum WebhookReceiveOutcome
{
    Accepted,
    Duplicate,
    Rejected,
    BadRequest,
    VerifierUnavailable,
}

/// <summary>Ingest (verify + store) and asynchronous processing of PayPal webhooks (PAYPAL_WEBHOOK_CONTRACT.md).</summary>
public sealed class WebhookService(
    IAppDbContext db,
    IPayPalWebhookVerifier verifier,
    PaymentStateApplier applier,
    PaymentCompletionService completion,
    AuditWriter audit,
    TimeProvider clock,
    ILogger<WebhookService> log)
{
    public const int MaxAttempts = 5;

    private static readonly string[] RequiredHeaders =
        ["PAYPAL-AUTH-ALGO", "PAYPAL-CERT-URL", "PAYPAL-TRANSMISSION-ID", "PAYPAL-TRANSMISSION-SIG", "PAYPAL-TRANSMISSION-TIME"];

    /// <summary>Steps 1–8 of the processing pipeline. Headers are looked up case-insensitively.</summary>
    public async Task<WebhookReceiveOutcome> ReceiveAsync(IReadOnlyDictionary<string, string> headers, string rawBody, CancellationToken cancellationToken)
    {
        var h = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        if (RequiredHeaders.Any(name => !h.TryGetValue(name, out var v) || string.IsNullOrWhiteSpace(v)))
        {
            log.LogWarning("WEBHOOK_HEADERS_MISSING");
            return WebhookReceiveOutcome.BadRequest;
        }

        string eventId, eventType;
        string? resourceType, resourceId;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            eventId = root.GetProperty("id").GetString() ?? throw new JsonException("id");
            eventType = root.GetProperty("event_type").GetString() ?? throw new JsonException("event_type");
            resourceType = root.TryGetProperty("resource_type", out var rt) ? rt.GetString() : null;
            resourceId = root.TryGetProperty("resource", out var res) && res.TryGetProperty("id", out var rid) ? rid.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            log.LogWarning("Webhook body is not a PayPal event");
            return WebhookReceiveOutcome.BadRequest;
        }

        var existing = await db.WebhookEvents.AsNoTracking().Where(e => e.PaypalEventId == eventId).Select(e => (WebhookEventStatus?)e.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null and not WebhookEventStatus.Rejected)
        {
            return WebhookReceiveOutcome.Duplicate; // BR-65
        }

        var certUrlOk = Uri.TryCreate(h["PAYPAL-CERT-URL"], UriKind.Absolute, out var certUri)
                        && certUri.Scheme == Uri.UriSchemeHttps
                        && (certUri.Host == "paypal.com" || certUri.Host.EndsWith(".paypal.com", StringComparison.OrdinalIgnoreCase));

        bool verified;
        try
        {
            verified = certUrlOk && await verifier.VerifyAsync(new WebhookVerificationRequest(
                h["PAYPAL-AUTH-ALGO"], h["PAYPAL-CERT-URL"], h["PAYPAL-TRANSMISSION-ID"], h["PAYPAL-TRANSMISSION-SIG"],
                h["PAYPAL-TRANSMISSION-TIME"], rawBody), cancellationToken);
        }
        catch (PayPalUnavailableException ex)
        {
            log.LogWarning(ex, "Webhook verification unavailable for {EventId}; PayPal will retry", eventId);
            return WebhookReceiveOutcome.VerifierUnavailable; // nothing stored → 503
        }

        var headersJson = JsonDefaults.Serialize(h.Where(p => p.Key.StartsWith("PAYPAL-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Value));
        var row = PayPalWebhookEvent.Receive(eventId, eventType, resourceType, resourceId, h["PAYPAL-TRANSMISSION-ID"],
            verified ? WebhookVerificationStatus.Success : WebhookVerificationStatus.Failure, rawBody, headersJson, clock.GetUtcNow());

        if (!verified)
        {
            if (existing is null)
            {
                await db.InsertWebhookEventIfNewAsync(row, cancellationToken);
                audit.Write(AuditActions.WebhookRejected, "PayPalWebhookEvent", row.Id, null, ActorType.Paypal, new { eventId, eventType });
                await db.SaveChangesAsync(cancellationToken);
            }

            log.LogWarning("Webhook {EventId} ({EventType}) failed verification", eventId, eventType);
            return WebhookReceiveOutcome.Rejected;
        }

        if (existing is WebhookEventStatus.Rejected)
        {
            log.LogWarning("Webhook {EventId} previously rejected; ignoring redelivery", eventId);
            return WebhookReceiveOutcome.Rejected;
        }

        await db.InsertWebhookEventIfNewAsync(row, cancellationToken);
        return WebhookReceiveOutcome.Accepted;
    }

    /// <summary>Processes up to <paramref name="batchSize"/> RECEIVED events, one transaction each (SKIP LOCKED).</summary>
    public async Task<int> ProcessPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        var processed = 0;
        for (var i = 0; i < batchSize; i++)
        {
            db.ClearTracking();
            await using var tx = await db.BeginTransactionAsync(cancellationToken);
            var ids = await db.ClaimReceivedWebhookIdsAsync(1, cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            var row = await db.WebhookEvents.SingleAsync(e => e.Id == ids[0], cancellationToken);
            try
            {
                var pendingAction = await ProcessAsync(row, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                processed++;
                if (pendingAction is { } purchaseId)
                {
                    // CHECKOUT.ORDER.APPROVED without a return trip: finish authorize → check → capture (P1), outside the tx.
                    await completion.CompleteAsync(purchaseId, null, LedgerSource.Reconciler, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await tx.RollbackAsync(cancellationToken);
                db.ClearTracking();
                await RecordFailureAsync(ids[0], ex, cancellationToken);
            }
        }

        return processed;
    }

    private async Task RecordFailureAsync(Guid id, Exception ex, CancellationToken cancellationToken)
    {
        var row = await db.WebhookEvents.SingleAsync(e => e.Id == id, cancellationToken);
        row.RecordFailure(ex.Message, MaxAttempts);
        await db.SaveChangesAsync(cancellationToken);
        if (row.Status == WebhookEventStatus.Failed)
        {
            log.LogError(ex, "Webhook {EventId} processing FAILED after {Attempts} attempts", row.PaypalEventId, row.Attempts);
        }
        else
        {
            log.LogWarning(ex, "Webhook {EventId} processing failed (attempt {Attempts})", row.PaypalEventId, row.Attempts);
        }
    }

    /// <returns>A purchase id to continue completing after commit (order approved via webhook), else null.</returns>
    private async Task<Guid?> ProcessAsync(PayPalWebhookEvent row, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        using var doc = JsonDocument.Parse(row.RawBody);
        var resource = doc.RootElement.TryGetProperty("resource", out var r) ? r : default;
        string? Str(params string[] path) => TryGetPath(resource, path, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        Payment? payment;
        switch (row.EventType)
        {
            case "CHECKOUT.ORDER.APPROVED":
            {
                payment = await FindPaymentAsync(orderId: Str("id"), cancellationToken: cancellationToken);
                if (payment is null)
                {
                    break;
                }

                var purchase = await db.LockPurchaseAsync(payment.PurchaseId, null, cancellationToken);
                if (purchase!.Status == PurchaseStatus.CheckoutPending)
                {
                    var amount = ParseAmount(resource, "purchase_units", "0", "amount", "value");
                    await applier.ApplyPayerApprovedAsync(purchase, payment, Str("payer", "payer_id"), amount, LedgerSource.PaypalWebhook, cancellationToken);
                    row.MarkProcessed(payment.Id, now);
                    return purchase.Id;
                }

                row.MarkProcessed(payment.Id, now);
                return null;
            }

            case "PAYMENT.AUTHORIZATION.CREATED":
            {
                payment = await FindPaymentAsync(authorizationId: Str("id"), purchaseId: Str("custom_id"), cancellationToken: cancellationToken);
                if (payment is null)
                {
                    break;
                }

                var purchase = await db.LockPurchaseAsync(payment.PurchaseId, null, cancellationToken);
                if (Payment.Rank(payment.Status) < Payment.Rank(PaymentStatus.Authorized))
                {
                    var authorization = new PayPalAuthorization(Str("id")!, "CREATED", ParseAmount(resource, "amount", "value"),
                        Str("amount", "currency_code"), ParseDate(Str("expiration_time")), null);
                    await applier.ApplyAuthorizationAsync(purchase!, payment, authorization, LedgerSource.PaypalWebhook, cancellationToken);
                }

                row.MarkProcessed(payment.Id, now); // never triggers capture by itself
                return null;
            }

            case "PAYMENT.AUTHORIZATION.VOIDED":
            {
                payment = await FindPaymentAsync(authorizationId: Str("id"), cancellationToken: cancellationToken);
                if (payment is null)
                {
                    break;
                }

                var purchase = await db.LockPurchaseAsync(payment.PurchaseId, null, cancellationToken);
                if (payment.Status is not (PaymentStatus.Voided or PaymentStatus.Completed or PaymentStatus.Declined))
                {
                    await applier.ApplyVoidAsync(purchase!, payment, PaymentStateApplier.FailureAuthorizationVoided, LedgerSource.PaypalWebhook,
                        cancellationToken);
                }

                row.MarkProcessed(payment.Id, now);
                return null;
            }

            case "PAYMENT.CAPTURE.COMPLETED":
            case "PAYMENT.CAPTURE.PENDING":
            case "PAYMENT.CAPTURE.DECLINED":
            {
                payment = await FindPaymentAsync(
                    captureId: Str("id"), purchaseId: Str("custom_id"), orderId: Str("supplementary_data", "related_ids", "order_id"),
                    cancellationToken: cancellationToken);
                if (payment is null)
                {
                    break;
                }

                var purchase = await db.LockPurchaseAsync(payment.PurchaseId, null, cancellationToken);
                var status = row.EventType["PAYMENT.CAPTURE.".Length..];
                var capture = new PayPalCapture(Str("id")!, status, ParseAmount(resource, "amount", "value"), Str("amount", "currency_code"), null);
                await applier.ApplyCaptureAsync(purchase!, payment, capture, LedgerSource.PaypalWebhook, cancellationToken);
                row.MarkProcessed(payment.Id, now);
                if (status == "DECLINED")
                {
                    await completion.TryVoidAsync(payment, cancellationToken);
                }

                return null;
            }

            case "PAYMENT.CAPTURE.REFUNDED":
            case "PAYMENT.CAPTURE.REVERSED":
            {
                var captureId = CaptureIdFromLinks(resource) ?? (row.EventType.EndsWith("REVERSED", StringComparison.Ordinal) ? Str("id") : null);
                payment = await FindPaymentAsync(captureId: captureId, purchaseId: Str("custom_id"), cancellationToken: cancellationToken);
                if (payment is null)
                {
                    break;
                }

                var purchase = await db.LockPurchaseAsync(payment.PurchaseId, null, cancellationToken);
                var amount = ParseAmount(resource, "amount", "value") ?? purchase!.Amount;
                await applier.ApplyRefundAsync(purchase!, payment, Str("id") ?? row.PaypalEventId, amount, LedgerSource.PaypalWebhook, cancellationToken);
                if (row.EventType.EndsWith("REVERSED", StringComparison.Ordinal))
                {
                    log.LogError("PAYMENT.CAPTURE.REVERSED for purchase {PurchaseId}", purchase!.Id);
                }

                row.MarkProcessed(payment.Id, now);
                return null;
            }

            case "CHECKOUT.PAYMENT-APPROVAL.REVERSED":
            {
                payment = await FindPaymentAsync(orderId: Str("order_id") ?? Str("id"), cancellationToken: cancellationToken);
                if (payment is null)
                {
                    break;
                }

                var purchase = await db.LockPurchaseAsync(payment.PurchaseId, null, cancellationToken);
                if (payment.Status != PaymentStatus.Completed)
                {
                    await applier.FailAndReleaseAsync(purchase!, payment, PaymentStateApplier.FailureApprovalReversed, LedgerSource.PaypalWebhook,
                        cancellationToken);
                }

                row.MarkProcessed(payment.Id, now);
                return null;
            }

            default:
                row.MarkIgnored(null, null, now);
                return null;
        }

        row.MarkIgnored("NO_MATCH", null, now);
        return null;
    }

    private async Task<Payment?> FindPaymentAsync(
        string? orderId = null, string? authorizationId = null, string? captureId = null, string? purchaseId = null,
        CancellationToken cancellationToken = default)
    {
        if (captureId is not null && await db.Payments.SingleOrDefaultAsync(p => p.PaypalCaptureId == captureId, cancellationToken) is { } byCapture)
        {
            return byCapture;
        }

        if (authorizationId is not null
            && await db.Payments.SingleOrDefaultAsync(p => p.PaypalAuthorizationId == authorizationId, cancellationToken) is { } byAuthorization)
        {
            return byAuthorization;
        }

        if (purchaseId is not null && Guid.TryParse(purchaseId, out var pid))
        {
            var candidates = await db.Payments.Where(p => p.PurchaseId == pid && p.PaypalOrderId != null)
                .OrderByDescending(p => p.CreatedAt).ToListAsync(cancellationToken);
            if (candidates.FirstOrDefault() is { } byPurchase)
            {
                return byPurchase;
            }
        }

        return orderId is null ? null : await db.Payments.SingleOrDefaultAsync(p => p.PaypalOrderId == orderId, cancellationToken);
    }

    private static string? CaptureIdFromLinks(JsonElement resource)
    {
        if (resource.ValueKind != JsonValueKind.Object || !resource.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.TryGetProperty("rel", out var rel) && rel.GetString() == "up" && link.TryGetProperty("href", out var href)
                && href.GetString() is { } url && url.Contains("/captures/", StringComparison.Ordinal))
            {
                return url[(url.LastIndexOf('/') + 1)..];
            }
        }

        return null;
    }

    private static decimal? ParseAmount(JsonElement element, params string[] path) =>
        TryGetPath(element, path, out var v) && v.ValueKind == JsonValueKind.String
        && decimal.TryParse(v.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : null;

    private static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    private static bool TryGetPath(JsonElement element, string[] path, out JsonElement value)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var next))
            {
                value = next;
            }
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index < value.GetArrayLength())
            {
                value = value[index];
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
