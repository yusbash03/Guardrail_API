using Guardrail.Domain.Common;

namespace Guardrail.Application.Dtos;

// Wire contracts (API_CONTRACT.md). Money = 2-dp strings; enums serialize as UPPER_SNAKE.

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role);

public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, UserDto User);

public sealed record SpendingProfileDto(Guid Id, string Currency, string TimeZone, int CurrentPolicyVersionNumber);

public sealed record PolicyVersionSummaryDto(int VersionNumber, Guid PolicyVersionId, string ChangeSummary, DateTimeOffset CreatedAt, string SnapshotHash);

public sealed record CategoryDto(string Code, string Name);

public sealed record MerchantDto(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    string? LogoUrl,
    IReadOnlyList<string> CategoryCodes,
    MerchantStatus Status,
    int ProductCount);

public sealed record MerchantChangeDto(MerchantDto Merchant, int PolicyVersionNumber);

public sealed record ProductDto(
    Guid Id,
    Guid MerchantId,
    string MerchantName,
    MerchantStatus MerchantStatus,
    string Sku,
    string Name,
    string? Brand,
    string CategoryCode,
    IReadOnlyList<string> Tags,
    string UnitPrice,
    string Currency,
    string? UnitLabel,
    string? ImageUrl,
    bool IsAvailable,
    int StockQuantity,
    string? Description = null);

public sealed record CartItemDto(Guid ProductId, string Name, string Sku, string CategoryCode, string UnitPrice, int Quantity, string LineTotal);

public sealed record CartDto(
    Guid Id,
    int Version,
    CartStatus Status,
    Guid MerchantId,
    string MerchantName,
    string Currency,
    IReadOnlyList<CartItemDto> Items,
    string Subtotal,
    string Total,
    ActorType CreatedBy,
    string SnapshotHash,
    DateTimeOffset UpdatedAt);

public sealed record PolicyReasonDto(
    string Code,
    ReasonSeverity Severity,
    string Message,
    PolicyRuleType? RuleType,
    string? RuleId,
    IReadOnlyDictionary<string, string?> Details);

public sealed record BudgetImpactDto(
    BudgetScope Scope,
    string? CategoryCode,
    BudgetPeriodType Period,
    DateOnly PeriodStart,
    string Limit,
    string CommittedBefore,
    string PurchaseAmount,
    string RemainingBefore,
    string RemainingAfter,
    bool Exceeded);

/// <summary>POLICY_EVALUATION_CONTRACT.md — the single wire contract for a policy decision.</summary>
public sealed record PolicyDecisionDto(
    Guid EvaluationId,
    PolicyDecisionType Decision,
    bool IsAllowed,
    bool RequiresApproval,
    string Reason,
    string ReasonCode,
    string PurchaseAmount,
    string? RemainingBudget,
    string Currency,
    Guid PolicyVersionId,
    int PolicyVersionNumber,
    Guid CartId,
    int CartVersion,
    string CartSnapshotHash,
    IReadOnlyList<PolicyReasonDto> Reasons,
    IReadOnlyList<BudgetImpactDto> BudgetImpacts,
    DateTimeOffset EvaluatedAt);

public sealed record ApprovalSummaryDto(Guid Id, ApprovalStatus Status, DateTimeOffset ExpiresAt);

public sealed record FinalCheckReasonDto(string Code, string Message);

public sealed record FinalCheckDto(bool Passed, IReadOnlyList<FinalCheckReasonDto> Reasons, DateTimeOffset CheckedAt);

public sealed record PaymentSummaryDto(
    PaymentStatus Status,
    string? PaypalOrderId,
    string? PaypalAuthorizationId,
    string? PaypalCaptureId,
    FinalCheckDto? FinalCheck);

public sealed record PurchaseDto(
    Guid Id,
    PurchaseStatus Status,
    string Amount,
    string Currency,
    Guid MerchantId,
    string MerchantName,
    Guid CartId,
    int CartVersion,
    string CartSnapshotHash,
    int PolicyVersionNumber,
    PolicyDecisionType Decision,
    ApprovalSummaryDto? Approval,
    PaymentSummaryDto? Payment,
    IReadOnlyList<CartItemDto> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode);

public sealed record ApprovalDto(
    Guid Id,
    ApprovalStatus Status,
    Guid PurchaseId,
    string Amount,
    string Currency,
    Guid MerchantId,
    string MerchantName,
    string CartSnapshotHash,
    int PolicyVersionNumber,
    IReadOnlyList<PolicyReasonDto> Reasons,
    IReadOnlyList<BudgetImpactDto> BudgetImpacts,
    IReadOnlyList<CartItemDto> Items,
    ActorType RequestedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DecidedAt,
    string? InvalidatedReason);

public sealed record ApprovalDecisionResponse(ApprovalDto Approval, PurchaseDto Purchase);

public sealed record CheckoutResponse(Guid PurchaseId, PurchaseStatus PurchaseStatus, string? PaypalOrderId, string? ApproveUrl);

public sealed record LedgerTotalsDto(string Limit, string Spent, string Pending, string Remaining);

public sealed record LedgerCategorySummaryDto(string CategoryCode, BudgetPeriodType Period, string? Limit, string Spent, string Pending, string? Remaining);

public sealed record LedgerSummaryDto(
    BudgetPeriodType Period,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Currency,
    IReadOnlyList<LedgerCategorySummaryDto> Categories,
    LedgerPeriodTotalsDto Overall);

public sealed record LedgerPeriodTotalsDto(string? Limit, string Spent, string Pending, string? Remaining);

public sealed record LedgerEntryDto(
    Guid Id,
    Guid PurchaseId,
    Guid? PaymentId,
    string CategoryCode,
    string Amount,
    string Currency,
    LedgerTransition Transition,
    LedgerBucket FromBucket,
    LedgerBucket ToBucket,
    DateOnly BudgetWeekStart,
    DateOnly BudgetMonthStart,
    LedgerSource Source,
    string ExternalRef,
    DateTimeOffset OccurredAt);

public sealed record TransactionDto(
    Guid PurchaseId,
    PurchaseStatus Status,
    string Amount,
    string Currency,
    Guid MerchantId,
    string MerchantName,
    ApprovalStatus? ApprovalStatus,
    PaymentStatus? PaymentStatus,
    string? PaypalOrderId,
    string? PaypalAuthorizationId,
    string? PaypalCaptureId,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record AuditLogDto(
    Guid Id,
    ActorType ActorType,
    string Action,
    string EntityType,
    Guid? EntityId,
    string? BeforeHash,
    string? AfterHash,
    System.Text.Json.JsonElement Data,
    DateTimeOffset CreatedAt);
