using Guardrail.Domain.Common;
using Guardrail.Domain.Shopping;

namespace Guardrail.Domain.Authorization;

public static class InvalidationReasons
{
    public const string CartChanged = "CART_CHANGED";
    public const string PolicyVersionChanged = "POLICY_VERSION_CHANGED";
    public const string PurchaseCancelled = "PURCHASE_CANCELLED";
    public const string ProductUnavailable = "PRODUCT_UNAVAILABLE";
}

/// <summary>A narrowly-scoped, single-use, expiring permission for one exact cart (APPROVAL_WORKFLOW.md).</summary>
public sealed class ApprovalRequest
{
    private ApprovalRequest()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid PurchaseId { get; private set; }

    public Guid CartId { get; private set; }

    public int CartVersion { get; private set; }

    public string CartSnapshotHash { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = MoneyFormat.DefaultCurrency;

    public Guid MerchantId { get; private set; }

    public Guid PolicyVersionId { get; private set; }

    public Guid PolicyEvaluationId { get; private set; }

    public string Reasons { get; private set; } = "[]";

    public ApprovalStatus Status { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? RejectionReason { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    public string? InvalidatedReason { get; private set; }

    public ActorType RequestedByActor { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>BR-50: binds user, purchase, cart id+version, hash, amount, currency, merchant, policy version, evaluation, expiry.</summary>
    public static ApprovalRequest CreateFor(Purchase purchase, PolicyEvaluation evaluation, ActorType requestedBy, DateTimeOffset now, TimeSpan ttl) =>
        new()
        {
            Id = Ids.New(),
            UserId = purchase.UserId,
            PurchaseId = purchase.Id,
            CartId = purchase.CartId,
            CartVersion = purchase.CartVersion,
            CartSnapshotHash = purchase.CartSnapshotHash,
            Amount = purchase.Amount,
            Currency = purchase.Currency,
            MerchantId = purchase.MerchantId,
            PolicyVersionId = purchase.PolicyVersionId,
            PolicyEvaluationId = evaluation.Id,
            Reasons = evaluation.Reasons,
            Status = ApprovalStatus.Pending,
            ExpiresAt = now.Add(ttl),
            RequestedByActor = requestedBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsOpen => Status is ApprovalStatus.Pending or ApprovalStatus.Approved;

    /// <summary>
    /// Caller must already have passed password step-up (ADR-015). Returns an error code for each failed binding check;
    /// state-changing side effects of failures (expire/invalidate) are applied by the caller.
    /// </summary>
    public DomainError? CheckCanApprove(Guid userId, string presentedHash, DateTimeOffset now, Guid currentPolicyVersionId, string recomputedCartHash)
    {
        if (userId != UserId)
        {
            return DomainError.NotFound("Approval");
        }

        if (Status != ApprovalStatus.Pending)
        {
            return new DomainError(ErrorCodes.ApprovalNotPending, $"Approval is {EnumText.ToText(Status)}.",
                new Dictionary<string, object?> { ["status"] = EnumText.ToText(Status) });
        }

        if (IsExpiredAt(now))
        {
            return new DomainError(ErrorCodes.ApprovalExpired, "This approval has expired.",
                new Dictionary<string, object?> { ["expiredAt"] = ExpiresAt });
        }

        if (!string.Equals(presentedHash, CartSnapshotHash, StringComparison.Ordinal))
        {
            return new DomainError(ErrorCodes.ApprovalSnapshotMismatch, "The cart you approved differs from the cart on file.");
        }

        if (!string.Equals(recomputedCartHash, CartSnapshotHash, StringComparison.Ordinal))
        {
            return Invalidated(InvalidationReasons.CartChanged);
        }

        if (currentPolicyVersionId != PolicyVersionId)
        {
            return Invalidated(InvalidationReasons.PolicyVersionChanged);
        }

        return null;
    }

    public void Approve(DateTimeOffset now)
    {
        Ensure(ApprovalStatus.Pending, ApprovalStatus.Approved);
        Status = ApprovalStatus.Approved;
        DecidedAt = now;
        UpdatedAt = now;
    }

    public void Reject(string? reason, DateTimeOffset now)
    {
        Ensure(ApprovalStatus.Pending, ApprovalStatus.Rejected);
        Status = ApprovalStatus.Rejected;
        RejectionReason = reason;
        DecidedAt = now;
        UpdatedAt = now;
    }

    public void Expire(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw DomainException.InvalidTransition("Approval", EnumText.ToText(Status), "EXPIRED");
        }

        Status = ApprovalStatus.Expired;
        UpdatedAt = now;
    }

    public void Invalidate(string reason, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw DomainException.InvalidTransition("Approval", EnumText.ToText(Status), "INVALIDATED");
        }

        Status = ApprovalStatus.Invalidated;
        InvalidatedReason = reason;
        UpdatedAt = now;
    }

    /// <summary>In-memory mirror of the atomic SQL consume (PAYMENT_STATE_MACHINE.md §2).</summary>
    public bool TryConsume(DateTimeOffset now, string currentCartHash, decimal purchaseAmount)
    {
        if (Status != ApprovalStatus.Approved || IsExpiredAt(now) || ConsumedAt is not null
            || currentCartHash != CartSnapshotHash || purchaseAmount != Amount)
        {
            return false;
        }

        Status = ApprovalStatus.Consumed;
        ConsumedAt = now;
        UpdatedAt = now;
        return true;
    }

    private static DomainError Invalidated(string reason) =>
        new(ErrorCodes.ApprovalInvalidated, "This approval is no longer valid.", new Dictionary<string, object?> { ["reason"] = reason });

    private void Ensure(ApprovalStatus expected, ApprovalStatus target)
    {
        if (Status != expected)
        {
            throw DomainException.InvalidTransition("Approval", EnumText.ToText(Status), EnumText.ToText(target));
        }
    }
}
