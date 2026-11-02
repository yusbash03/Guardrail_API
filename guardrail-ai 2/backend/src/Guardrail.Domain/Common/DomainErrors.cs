namespace Guardrail.Domain.Common;

/// <summary>A stable, machine-readable failure (ERROR_HANDLING.md §2).</summary>
public sealed record DomainError(string Code, string Message, IReadOnlyDictionary<string, object?>? Data = null)
{
    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; init; }

    public static DomainError NotFound(string what = "Resource") => new(ErrorCodes.NotFound, $"{what} was not found.");

    public static DomainError Validation(string message, IReadOnlyDictionary<string, string[]>? errors = null) =>
        new(ErrorCodes.ValidationFailed, message) { ValidationErrors = errors };

    public static DomainError InvalidState(string message) => new(ErrorCodes.InvalidState, message);
}

/// <summary>Thrown for illegal state transitions (programmer/concurrency errors) → 409 INVALID_STATE.</summary>
public sealed class DomainException(DomainError error) : Exception(error.Message)
{
    public DomainError Error { get; } = error;

    public static DomainException InvalidTransition(string entity, object from, object to) =>
        new(DomainError.InvalidState($"{entity} cannot move from {from} to {to}."));
}

public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string ReauthenticationFailed = "REAUTHENTICATION_FAILED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string InvalidState = "INVALID_STATE";
    public const string PolicyVersionConflict = "POLICY_VERSION_CONFLICT";
    public const string CartLocked = "CART_LOCKED";
    public const string CartNotOpen = "CART_NOT_OPEN";
    public const string CartChanged = "CART_CHANGED";
    public const string EvaluationStale = "EVALUATION_STALE";
    public const string ApprovalNotPending = "APPROVAL_NOT_PENDING";
    public const string ApprovalNotApproved = "APPROVAL_NOT_APPROVED";
    public const string ApprovalSnapshotMismatch = "APPROVAL_SNAPSHOT_MISMATCH";
    public const string ApprovalInvalidated = "APPROVAL_INVALIDATED";
    public const string PaymentNotApproved = "PAYMENT_NOT_APPROVED";
    public const string IdempotencyInProgress = "IDEMPOTENCY_IN_PROGRESS";
    public const string ApprovalExpired = "APPROVAL_EXPIRED";
    public const string EvaluationExpired = "EVALUATION_EXPIRED";
    public const string CartVersionMismatch = "CART_VERSION_MISMATCH";
    public const string PolicyInvalid = "POLICY_INVALID";
    public const string PolicyMerchantConflict = "POLICY_MERCHANT_CONFLICT";
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";
    public const string EvaluationBlocked = "EVALUATION_BLOCKED";
    public const string NoActivePolicy = "NO_ACTIVE_POLICY";
    public const string MixedMerchantCart = "MIXED_MERCHANT_CART";
    public const string ProductNotFound = "PRODUCT_NOT_FOUND";
    public const string ProductUnavailable = "PRODUCT_UNAVAILABLE";
    public const string CartLimitExceeded = "CART_LIMIT_EXCEEDED";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string PaymentInstrumentDeclined = "PAYMENT_INSTRUMENT_DECLINED";
    public const string AmountMismatch = "AMOUNT_MISMATCH";
    public const string FinalCheckFailed = "FINAL_CHECK_FAILED";
    public const string RateLimited = "RATE_LIMITED";
    public const string ApprovalStepUpLocked = "APPROVAL_STEP_UP_LOCKED";
    public const string CaptureInProgress = "CAPTURE_IN_PROGRESS";
    public const string PaymentProviderUnavailable = "PAYMENT_PROVIDER_UNAVAILABLE";
    public const string AgentUnavailable = "AGENT_UNAVAILABLE";
    public const string AgentStepLimit = "AGENT_STEP_LIMIT";
    public const string InternalError = "INTERNAL_ERROR";
    public const string UnknownTool = "UNKNOWN_TOOL";
    public const string EmailTaken = "EMAIL_TAKEN";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string LedgerInvalidTransition = "LEDGER_INVALID_TRANSITION";
}
