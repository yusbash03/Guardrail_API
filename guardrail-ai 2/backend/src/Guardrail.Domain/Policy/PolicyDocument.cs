namespace Guardrail.Domain.Policy;

// Wire shape of a policy (SPENDING_POLICY_ENGINE.md §1.1, API_CONTRACT.md PUT /policies).
// Amounts are 2-dp strings and enums are UPPER_SNAKE strings, exactly as on the wire.

public sealed record AmountDocument(string Amount);

public sealed record SpendingLimitDocument(string Period, string Limit);

public sealed record CategoryBudgetDocument(string CategoryCode, string Period, string Limit);

public sealed record MerchantRulesDocument(
    IReadOnlyList<Guid> ApprovedMerchantIds,
    IReadOnlyList<Guid> BlockedMerchantIds,
    string NewMerchantAction);

public sealed record ProductMatchDocument(string Type, string Value);

public sealed record ProductRestrictionDocument(string? Id, ProductMatchDocument Match, string Action, string? Note);

public sealed record DuplicatePurchaseDocument(bool Enabled, int WindowHours, string Scope, string Action);

public sealed record TimeRestrictionDocument(
    string? Id,
    IReadOnlyList<string> DaysOfWeek,
    string StartLocalTime,
    string EndLocalTime,
    string Action);

public sealed record PolicyDocument
{
    public Guid? PolicyVersionId { get; init; }

    public int? VersionNumber { get; init; }

    public string? Currency { get; init; }

    public string? TimeZone { get; init; }

    public AmountDocument? ApprovalThreshold { get; init; }

    public AmountDocument? MaxTransactionAmount { get; init; }

    public IReadOnlyList<SpendingLimitDocument> SpendingLimits { get; init; } = [];

    public IReadOnlyList<CategoryBudgetDocument> CategoryBudgets { get; init; } = [];

    public MerchantRulesDocument MerchantRules { get; init; } = new([], [], "BLOCK");

    public IReadOnlyList<ProductRestrictionDocument> ProductRestrictions { get; init; } = [];

    public DuplicatePurchaseDocument? DuplicatePurchase { get; init; }

    public IReadOnlyList<TimeRestrictionDocument> TimeRestrictions { get; init; } = [];

    public string UnbudgetedCategoryAction { get; init; } = "REQUIRE_APPROVAL";
}
