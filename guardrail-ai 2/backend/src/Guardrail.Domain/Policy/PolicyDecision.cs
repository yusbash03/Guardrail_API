using Guardrail.Domain.Common;

namespace Guardrail.Domain.Policy;

public static class ReasonCodes
{
    public const string NoActivePolicy = "NO_ACTIVE_POLICY";
    public const string CartEmpty = "CART_EMPTY";
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";
    public const string ProductUnavailable = "PRODUCT_UNAVAILABLE";
    public const string MerchantBlocked = "MERCHANT_BLOCKED";
    public const string MerchantApproved = "MERCHANT_APPROVED";
    public const string MerchantNotApproved = "MERCHANT_NOT_APPROVED";
    public const string NewMerchantRequiresApproval = "NEW_MERCHANT_REQUIRES_APPROVAL";
    public const string NewMerchantAllowed = "NEW_MERCHANT_ALLOWED";
    public const string ProductRestricted = "PRODUCT_RESTRICTED";
    public const string ProductRequiresApproval = "PRODUCT_REQUIRES_APPROVAL";
    public const string MaxTransactionExceeded = "MAX_TRANSACTION_EXCEEDED";
    public const string CategoryBudgetExceeded = "CATEGORY_BUDGET_EXCEEDED";
    public const string UnbudgetedCategory = "UNBUDGETED_CATEGORY";
    public const string WeeklyLimitExceeded = "WEEKLY_LIMIT_EXCEEDED";
    public const string MonthlyLimitExceeded = "MONTHLY_LIMIT_EXCEEDED";
    public const string DuplicatePurchase = "DUPLICATE_PURCHASE";
    public const string TimeRestricted = "TIME_RESTRICTED";
    public const string ApprovalThresholdExceeded = "APPROVAL_THRESHOLD_EXCEEDED";
    public const string AllPoliciesSatisfied = "ALL_POLICIES_SATISFIED";

    public static readonly IReadOnlyList<string> All =
    [
        NoActivePolicy, CartEmpty, CurrencyMismatch, ProductUnavailable, MerchantBlocked, MerchantApproved,
        MerchantNotApproved, NewMerchantRequiresApproval, NewMerchantAllowed, ProductRestricted, ProductRequiresApproval,
        MaxTransactionExceeded, CategoryBudgetExceeded, UnbudgetedCategory, WeeklyLimitExceeded, MonthlyLimitExceeded,
        DuplicatePurchase, TimeRestricted, ApprovalThresholdExceeded, AllPoliciesSatisfied,
    ];
}

/// <summary>One finding of the engine. <see cref="Check"/> is the ordering key from the algorithm table.</summary>
public sealed record PolicyReason(
    string Code,
    ReasonSeverity Severity,
    string Message,
    PolicyRuleType? RuleType,
    Guid? RuleId,
    IReadOnlyDictionary<string, string?> Details)
{
    internal int Check { get; init; }

    internal string SortCategory { get; init; } = string.Empty;
}

public sealed record BudgetImpact(
    BudgetScope Scope,
    string? CategoryCode,
    BudgetPeriodType Period,
    DateOnly PeriodStart,
    decimal Limit,
    decimal CommittedBefore,
    decimal PurchaseAmount,
    decimal RemainingBefore,
    decimal RemainingAfter,
    bool Exceeded);

/// <summary>Engine output (SPENDING_POLICY_ENGINE.md §3) without persistence fields.</summary>
public sealed record PolicyDecision(
    PolicyDecisionType Decision,
    IReadOnlyList<PolicyReason> Reasons,
    IReadOnlyList<BudgetImpact> BudgetImpacts,
    decimal PurchaseAmount,
    decimal? RemainingBudget,
    string Currency,
    string CartSnapshotHash)
{
    public bool IsAllowed => Decision == PolicyDecisionType.Allowed;

    public bool RequiresApproval => Decision == PolicyDecisionType.RequiresApproval;

    public PolicyReason PrimaryReason => PrimaryOf(Reasons);

    /// <summary>First BLOCK in check order; else first REQUIRE_APPROVAL; else ALL_POLICIES_SATISFIED.</summary>
    public static PolicyReason PrimaryOf(IReadOnlyList<PolicyReason> reasons) =>
        reasons.FirstOrDefault(r => r.Severity == ReasonSeverity.Block)
        ?? reasons.FirstOrDefault(r => r.Severity == ReasonSeverity.RequireApproval)
        ?? reasons.FirstOrDefault(r => r.Code == ReasonCodes.AllPoliciesSatisfied)
        ?? reasons[0];

    public static PolicyDecisionType DecisionOf(IEnumerable<PolicyReason> reasons)
    {
        var list = reasons as IReadOnlyCollection<PolicyReason> ?? reasons.ToList();
        return list.Any(r => r.Severity == ReasonSeverity.Block) ? PolicyDecisionType.Blocked
            : list.Any(r => r.Severity == ReasonSeverity.RequireApproval) ? PolicyDecisionType.RequiresApproval
            : PolicyDecisionType.Allowed;
    }
}
