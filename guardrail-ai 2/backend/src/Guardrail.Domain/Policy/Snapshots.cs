using System.Text.Json.Nodes;
using Guardrail.Domain.Common;

namespace Guardrail.Domain.Policy;

// ---------------------------------------------------------------- PolicySnapshot (SPENDING_POLICY_ENGINE.md §1.1)

public sealed record AmountRule(Guid RuleId, decimal Amount);

public sealed record SpendingLimitRule(Guid RuleId, BudgetPeriodType Period, decimal Limit);

public sealed record CategoryBudgetRule(Guid RuleId, string CategoryCode, BudgetPeriodType Period, decimal Limit);

public sealed record MerchantRule(Guid RuleId, Guid MerchantId);

public sealed record ActionRule(Guid RuleId, RuleAction Action);

public sealed record ProductRestrictionRule(Guid RuleId, ProductMatchType MatchType, string MatchValue, RuleAction Action, string? Note);

public sealed record DuplicatePurchaseRule(Guid RuleId, int WindowHours, DuplicateScope Scope, RuleAction Action);

public sealed record TimeRestrictionRule(
    Guid RuleId,
    IReadOnlyList<DayOfWeek> DaysOfWeek,
    TimeOnly StartLocalTime,
    TimeOnly EndLocalTime,
    RuleAction Action);

public sealed record PolicySnapshot(
    Guid PolicyVersionId,
    int VersionNumber,
    string Currency,
    string TimeZone,
    AmountRule? ApprovalThreshold,
    AmountRule? MaxTransactionAmount,
    IReadOnlyList<SpendingLimitRule> SpendingLimits,
    IReadOnlyList<CategoryBudgetRule> CategoryBudgets,
    IReadOnlyList<MerchantRule> ApprovedMerchants,
    IReadOnlyList<MerchantRule> BlockedMerchants,
    ActionRule? NewMerchantPolicy,
    IReadOnlyList<ProductRestrictionRule> ProductRestrictions,
    DuplicatePurchaseRule? DuplicatePurchase,
    IReadOnlyList<TimeRestrictionRule> TimeRestrictions,
    ActionRule? UnbudgetedCategoryPolicy)
{
    public MerchantStatus MerchantStatusOf(Guid merchantId) =>
        BlockedMerchants.Any(m => m.MerchantId == merchantId) ? MerchantStatus.Blocked
        : ApprovedMerchants.Any(m => m.MerchantId == merchantId) ? MerchantStatus.Approved
        : MerchantStatus.New; // BR-20
}

// ---------------------------------------------------------------- CartSnapshot (§1.3)

public sealed record CartSnapshotItem(
    Guid ProductId,
    string Sku,
    string Name,
    string CategoryCode,
    IReadOnlyList<string> Tags,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal,
    bool IsAvailable,
    int StockQuantity);

public sealed record CartSnapshot(
    Guid CartId,
    int CartVersion,
    Guid MerchantId,
    string MerchantName,
    string Currency,
    IReadOnlyList<CartSnapshotItem> Items,
    decimal Total)
{
    /// <summary>Canonical hash input (APPROVAL_WORKFLOW.md §2, ADR-010).</summary>
    public string ToCanonicalJson()
    {
        var items = new JsonArray();
        foreach (var item in Items.OrderBy(i => i.ProductId.ToString("D"), StringComparer.Ordinal))
        {
            items.Add(new JsonObject
            {
                ["categoryCode"] = item.CategoryCode,
                ["lineTotal"] = MoneyFormat.Format(item.LineTotal),
                ["productId"] = item.ProductId.ToString("D"),
                ["quantity"] = item.Quantity,
                ["sku"] = item.Sku,
                ["unitPrice"] = MoneyFormat.Format(item.UnitPrice),
            });
        }

        var root = new JsonObject
        {
            ["cartId"] = CartId.ToString("D"),
            ["cartVersion"] = CartVersion,
            ["currency"] = Currency,
            ["items"] = items,
            ["merchantId"] = MerchantId.ToString("D"),
            ["total"] = MoneyFormat.Format(Total),
        };
        return CanonicalJson.Serialize(root);
    }

    public string ComputeHash() => CanonicalJson.Sha256Hex(ToCanonicalJson());

    /// <summary>Line totals grouped by category (used for budgets and ledger allocation).</summary>
    public IReadOnlyDictionary<string, decimal> AmountByCategory() =>
        Items.GroupBy(i => i.CategoryCode, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.LineTotal), StringComparer.Ordinal);
}

// ---------------------------------------------------------------- SpendingSnapshot (§1.4)

public sealed record RecentPurchase(
    Guid PurchaseId,
    DateTimeOffset CreatedAt,
    Guid MerchantId,
    IReadOnlyList<Guid> ProductIds,
    string CartSnapshotHash);

public sealed record SpendingSnapshot(
    DateOnly WeekStart,
    DateOnly MonthStart,
    IReadOnlyDictionary<string, decimal> CommittedWeeklyByCategory,
    IReadOnlyDictionary<string, decimal> CommittedMonthlyByCategory,
    decimal CommittedWeeklyTotal,
    decimal CommittedMonthlyTotal,
    IReadOnlyList<RecentPurchase> RecentPurchases)
{
    public static SpendingSnapshot Empty(DateOnly weekStart, DateOnly monthStart) =>
        new(weekStart, monthStart, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(), 0m, 0m, []);

    public decimal CommittedFor(string categoryCode, BudgetPeriodType period)
    {
        var source = period == BudgetPeriodType.Weekly ? CommittedWeeklyByCategory : CommittedMonthlyByCategory;
        return source.TryGetValue(categoryCode, out var value) ? value : 0m;
    }

    public decimal CommittedTotal(BudgetPeriodType period) =>
        period == BudgetPeriodType.Weekly ? CommittedWeeklyTotal : CommittedMonthlyTotal;

    public DateOnly PeriodStart(BudgetPeriodType period) => period == BudgetPeriodType.Weekly ? WeekStart : MonthStart;
}
