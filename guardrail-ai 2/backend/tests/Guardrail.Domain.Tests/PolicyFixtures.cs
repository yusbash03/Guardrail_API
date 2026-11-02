using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;

namespace Guardrail.Domain.Tests;

internal static class PolicyFixtures
{
    public static readonly Guid FreshMart = Guid.Parse("00000000-0000-7000-8000-000000000001");
    public static readonly Guid GreenGrocer = Guid.Parse("00000000-0000-7000-8000-000000000002");
    public static readonly Guid QuickCart = Guid.Parse("00000000-0000-7000-8000-000000000004");
    public static readonly Guid TechNest = Guid.Parse("00000000-0000-7000-8000-000000000005");

    public static readonly DateTimeOffset Now = new(2026, 11, 3, 14, 5, 11, TimeSpan.Zero); // Tue

    /// <summary>Demo policy v5 (SPENDING_POLICY_ENGINE.md §5).</summary>
    public static PolicySnapshot DemoPolicy(
        IReadOnlyList<CategoryBudgetRule>? budgets = null,
        IReadOnlyList<SpendingLimitRule>? limits = null,
        DuplicatePurchaseRule? duplicate = null,
        IReadOnlyList<TimeRestrictionRule>? times = null,
        IReadOnlyList<MerchantRule>? blocked = null,
        AmountRule? max = null,
        RuleAction newMerchantAction = RuleAction.Block,
        string timeZone = "America/New_York") =>
        new(
            Guid.Parse("00000000-0000-7000-8000-0000000000a5"),
            5,
            "USD",
            timeZone,
            new AmountRule(Rule(1), 30.00m),
            max,
            limits ?? [],
            budgets ?? [new CategoryBudgetRule(Rule(2), CategoryCodes.Groceries, BudgetPeriodType.Weekly, 80.00m)],
            [new MerchantRule(Rule(3), FreshMart), new MerchantRule(Rule(4), GreenGrocer)],
            blocked ?? [],
            new ActionRule(Rule(5), newMerchantAction),
            [new ProductRestrictionRule(Rule(6), ProductMatchType.Category, CategoryCodes.Alcohol, RuleAction.Block, "Never buy alcohol")],
            duplicate,
            times ?? [],
            new ActionRule(Rule(7), RuleAction.RequireApproval));

    public static Guid Rule(int n) => Guid.Parse($"00000000-0000-7000-8000-1000000000{n:00}");

    public static CartSnapshot Cart(Guid merchantId, string merchantName, params (string Category, decimal Amount)[] lines)
    {
        var items = lines.Select((l, i) => new CartSnapshotItem(
            Guid.Parse($"00000000-0000-7000-8000-2000000000{i:00}"), $"SKU-{i}", $"Item {i}", l.Category, [], l.Amount, 1, l.Amount, true, 100))
            .ToList();
        return new CartSnapshot(Guid.Parse("00000000-0000-7000-8000-300000000001"), 3, merchantId, merchantName, "USD", items,
            items.Sum(i => i.LineTotal));
    }

    public static SpendingSnapshot SpendingOf(decimal groceriesWeekly = 0m, decimal totalWeekly = 0m, IReadOnlyList<RecentPurchase>? recent = null) =>
        new(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 1),
            new Dictionary<string, decimal> { [CategoryCodes.Groceries] = groceriesWeekly },
            new Dictionary<string, decimal> { [CategoryCodes.Groceries] = groceriesWeekly },
            totalWeekly, totalWeekly, recent ?? []);

    public static IEnumerable<string> Codes(this PolicyDecision decision) => decision.Reasons.Select(r => r.Code);
}
