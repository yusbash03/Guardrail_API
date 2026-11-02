using System.Net;
using Guardrail.Domain.Common;

namespace Guardrail.Domain.Audit;

/// <summary>BR-90 audited actions.</summary>
public static class AuditActions
{
    public const string UserRegistered = "USER_REGISTERED";
    public const string UserLoggedIn = "USER_LOGGED_IN";
    public const string PolicyUpdated = "POLICY_UPDATED";
    public const string MerchantApproved = "MERCHANT_APPROVED";
    public const string MerchantBlocked = "MERCHANT_BLOCKED";
    public const string CartCreated = "CART_CREATED";
    public const string CartLocked = "CART_LOCKED";
    public const string EvaluationPerformed = "EVALUATION_PERFORMED";
    public const string PurchaseCreated = "PURCHASE_CREATED";
    public const string ApprovalRequested = "APPROVAL_REQUESTED";
    public const string ApprovalGranted = "APPROVAL_GRANTED";
    public const string ApprovalRejected = "APPROVAL_REJECTED";
    public const string ApprovalExpired = "APPROVAL_EXPIRED";
    public const string ApprovalInvalidated = "APPROVAL_INVALIDATED";
    public const string ApprovalConsumed = "APPROVAL_CONSUMED";
    public const string CheckoutStarted = "CHECKOUT_STARTED";
    public const string PaymentAuthorized = "PAYMENT_AUTHORIZED";
    public const string FinalCheckPassed = "FINAL_CHECK_PASSED";
    public const string FinalCheckFailed = "FINAL_CHECK_FAILED";
    public const string PaymentVoided = "PAYMENT_VOIDED";
    public const string PaymentCaptured = "PAYMENT_CAPTURED";
    public const string PaymentFailed = "PAYMENT_FAILED";
    public const string PaymentRefunded = "PAYMENT_REFUNDED";
    public const string ApprovalStepUpFailed = "APPROVAL_STEP_UP_FAILED";
    public const string WebhookRejected = "WEBHOOK_REJECTED";
    public const string AmountMismatch = "AMOUNT_MISMATCH";
    public const string UnexpectedCapture = "UNEXPECTED_CAPTURE";
    public const string AgentToolRejected = "AGENT_TOOL_REJECTED";
}

public sealed class AuditLog
{
    private AuditLog()
    {
    }

    public Guid Id { get; private set; }

    public Guid? UserId { get; private set; }

    public ActorType ActorType { get; private set; }

    public string? ActorId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public Guid? EntityId { get; private set; }

    public string? BeforeHash { get; private set; }

    public string? AfterHash { get; private set; }

    public string Data { get; private set; } = "{}";

    public string? CorrelationId { get; private set; }

    public IPAddress? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditLog Create(
        Guid? userId, ActorType actor, string? actorId, string action, string entityType, Guid? entityId, string dataJson,
        DateTimeOffset now, string? correlationId = null, IPAddress? ip = null, string? userAgent = null,
        string? beforeHash = null, string? afterHash = null) => new()
    {
        Id = Ids.New(),
        UserId = userId,
        ActorType = actor,
        ActorId = actorId,
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        Data = dataJson,
        CorrelationId = correlationId,
        IpAddress = ip,
        UserAgent = userAgent is { Length: > 300 } ? userAgent[..300] : userAgent,
        BeforeHash = beforeHash,
        AfterHash = afterHash,
        CreatedAt = now,
    };
}

public sealed class IdempotencyKey
{
    private IdempotencyKey()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string RequestPath { get; private set; } = string.Empty;

    public string RequestHash { get; private set; } = string.Empty;

    public int? ResponseStatus { get; private set; }

    public string? ResponseBody { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public static IdempotencyKey Begin(Guid userId, string key, string path, string requestHash, DateTimeOffset now, TimeSpan ttl) => new()
    {
        Id = Ids.New(),
        UserId = userId,
        Key = key,
        RequestPath = path,
        RequestHash = requestHash,
        CreatedAt = now,
        ExpiresAt = now.Add(ttl),
    };

    public void Complete(int status, string body)
    {
        ResponseStatus = status;
        ResponseBody = body;
    }
}
