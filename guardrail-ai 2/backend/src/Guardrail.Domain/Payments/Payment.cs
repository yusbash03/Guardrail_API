using Guardrail.Domain.Common;
using Guardrail.Domain.Shopping;

namespace Guardrail.Domain.Payments;

/// <summary>Internal mirror of a PayPal order/authorization/capture (PAYMENT_STATE_MACHINE.md §3).</summary>
public sealed class Payment
{
    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid PurchaseId { get; private set; }

    public string Provider { get; private set; } = "PAYPAL";

    public PaymentIntent Intent { get; private set; }

    public PaymentStatus Status { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = MoneyFormat.DefaultCurrency;

    public decimal? AuthorizedAmount { get; private set; }

    public decimal? CapturedAmount { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public string? PaypalOrderId { get; private set; }

    public string? PaypalAuthorizationId { get; private set; }

    public DateTimeOffset? AuthorizationExpiresAt { get; private set; }

    public string? PaypalCaptureId { get; private set; }

    public Guid PaypalCreateRequestId { get; private set; }

    public Guid? PaypalAuthorizeRequestId { get; private set; }

    public Guid? PaypalCaptureRequestId { get; private set; }

    public Guid? PaypalVoidRequestId { get; private set; }

    /// <summary>jsonb FinalCheckResult (invariant 5: no capture without a stored PASS).</summary>
    public string? FinalCheckResult { get; private set; }

    public string? ApproveUrl { get; private set; }

    public string? PaypalPayerId { get; private set; }

    public string? LastPaypalDebugId { get; private set; }

    public string? LastPaypalResponse { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static IReadOnlySet<PaymentStatus> Inactive { get; } =
        new HashSet<PaymentStatus> { PaymentStatus.Declined, PaymentStatus.Failed, PaymentStatus.Voided };

    public bool IsActive => !Inactive.Contains(Status);

    /// <summary>Forward-only ranking for webhook/reconciler updates (PAYMENT_STATE_MACHINE.md §4).</summary>
    public static int Rank(PaymentStatus status) => status switch
    {
        PaymentStatus.Initiated => 0,
        PaymentStatus.Created or PaymentStatus.PayerActionRequired => 1,
        PaymentStatus.Approved => 2,
        PaymentStatus.Authorized => 3,
        PaymentStatus.CapturePending => 4,
        PaymentStatus.Completed or PaymentStatus.Declined or PaymentStatus.Failed or PaymentStatus.Voided => 5,
        PaymentStatus.PartiallyRefunded => 6,
        _ => 7,
    };

    public static Payment Initiate(Purchase purchase, PaymentIntent intent, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        UserId = purchase.UserId,
        PurchaseId = purchase.Id,
        Intent = intent,
        Status = PaymentStatus.Initiated,
        Amount = purchase.Amount,
        Currency = purchase.Currency,
        PaypalCreateRequestId = Guid.NewGuid(),
        PaypalAuthorizeRequestId = intent == PaymentIntent.Authorize ? Guid.NewGuid() : null,
        PaypalCaptureRequestId = Guid.NewGuid(),
        PaypalVoidRequestId = intent == PaymentIntent.Authorize ? Guid.NewGuid() : null,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Moves forward only; returns false (no change) for stale or equal updates.</summary>
    public bool Advance(PaymentStatus target, DateTimeOffset now)
    {
        if (Rank(target) <= Rank(Status) && !(Status == PaymentStatus.PartiallyRefunded && target == PaymentStatus.PartiallyRefunded))
        {
            return false;
        }

        Status = target;
        UpdatedAt = now;
        return true;
    }

    public void RecordOrderCreated(string orderId, string? approveUrl, PaymentStatus status, string? debugId, DateTimeOffset now)
    {
        PaypalOrderId = orderId;
        ApproveUrl = approveUrl;
        LastPaypalDebugId = debugId;
        Advance(status, now);
    }

    public void RecordPayer(string? payerId, DateTimeOffset now)
    {
        PaypalPayerId ??= payerId;
        UpdatedAt = now;
    }

    public void RecordAuthorization(string authorizationId, decimal? amount, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        PaypalAuthorizationId ??= authorizationId;
        AuthorizedAmount ??= amount;
        AuthorizationExpiresAt ??= expiresAt;
        UpdatedAt = now;
    }

    public void RecordCapture(string captureId, decimal? amount, DateTimeOffset now)
    {
        PaypalCaptureId ??= captureId;
        CapturedAmount ??= amount;
        UpdatedAt = now;
    }

    public void RecordFinalCheck(string resultJson, DateTimeOffset now)
    {
        FinalCheckResult = resultJson;
        UpdatedAt = now;
    }

    public void RecordRefund(decimal refundedTotal, DateTimeOffset now)
    {
        RefundedAmount = refundedTotal;
        UpdatedAt = now;
    }

    public void RecordProviderResponse(string? debugId, string? redactedResponse, DateTimeOffset now)
    {
        LastPaypalDebugId = debugId ?? LastPaypalDebugId;
        LastPaypalResponse = redactedResponse ?? LastPaypalResponse;
        UpdatedAt = now;
    }

    /// <summary>Used for internal terminal states that need no PayPal call (e.g. order abandoned before approval).</summary>
    public void ForceStatus(PaymentStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }
}

public sealed class PayPalWebhookEvent
{
    private PayPalWebhookEvent()
    {
    }

    public Guid Id { get; private set; }

    public string PaypalEventId { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public string? ResourceType { get; private set; }

    public string? ResourceId { get; private set; }

    public string? TransmissionId { get; private set; }

    public WebhookVerificationStatus VerificationStatus { get; private set; }

    public WebhookEventStatus Status { get; private set; }

    public string RawBody { get; private set; } = string.Empty;

    public string Headers { get; private set; } = "{}";

    public Guid? RelatedPaymentId { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public static PayPalWebhookEvent Receive(
        string eventId,
        string eventType,
        string? resourceType,
        string? resourceId,
        string? transmissionId,
        WebhookVerificationStatus verification,
        string rawBody,
        string headersJson,
        DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        PaypalEventId = eventId,
        EventType = eventType,
        ResourceType = resourceType,
        ResourceId = resourceId,
        TransmissionId = transmissionId,
        VerificationStatus = verification,
        // BR-64: unverified events are stored REJECTED and never processed.
        Status = verification == WebhookVerificationStatus.Success ? WebhookEventStatus.Received : WebhookEventStatus.Rejected,
        RawBody = rawBody,
        Headers = headersJson,
        ReceivedAt = now,
    };

    public void MarkProcessed(Guid? paymentId, DateTimeOffset now)
    {
        Status = WebhookEventStatus.Processed;
        RelatedPaymentId = paymentId ?? RelatedPaymentId;
        ProcessedAt = now;
    }

    public void MarkIgnored(string? reason, Guid? paymentId, DateTimeOffset now)
    {
        Status = WebhookEventStatus.Ignored;
        LastError = reason;
        RelatedPaymentId = paymentId ?? RelatedPaymentId;
        ProcessedAt = now;
    }

    public void RecordFailure(string error, int maxAttempts)
    {
        Attempts++;
        LastError = error.Length > 1000 ? error[..1000] : error;
        if (Attempts >= maxAttempts)
        {
            Status = WebhookEventStatus.Failed;
        }
    }
}
