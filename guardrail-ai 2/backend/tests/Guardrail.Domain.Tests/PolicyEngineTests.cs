using System.Text.Json;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;
using static Guardrail.Domain.Tests.PolicyFixtures;

namespace Guardrail.Domain.Tests;

public class PolicyEngineTests
{
    // ----------------------------------------------------------- E1–E9 (SPENDING_POLICY_ENGINE.md §5)

    [Fact]
    public void E1_ApprovedMerchantAboveThreshold_RequiresApproval()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 43.00m)), SpendingOf(), Now);

        decision.Decision.ShouldBe(PolicyDecisionType.RequiresApproval);
        decision.Codes().ShouldBe([ReasonCodes.MerchantApproved, ReasonCodes.ApprovalThresholdExceeded]);
        decision.RemainingBudget.ShouldBe(37.00m);
        decision.PrimaryReason.Code.ShouldBe(ReasonCodes.ApprovalThresholdExceeded);
        decision.PrimaryReason.Message.ShouldBe("Purchase exceeds approval threshold");
        decision.IsAllowed.ShouldBeFalse();
        decision.RequiresApproval.ShouldBeTrue();
    }

    [Fact]
    public void E2_SmallCartAtApprovedMerchant_Allowed()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 24.50m)), SpendingOf(), Now);

        decision.Decision.ShouldBe(PolicyDecisionType.Allowed);
        decision.Codes().ShouldBe([ReasonCodes.MerchantApproved, ReasonCodes.AllPoliciesSatisfied]);
        decision.RemainingBudget.ShouldBe(55.50m);
        decision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void E3_NewMerchantWithBlockAction_Blocked()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(QuickCart, "QuickCart Express", (CategoryCodes.Groceries, 18.00m)), SpendingOf(), Now);

        decision.Decision.ShouldBe(PolicyDecisionType.Blocked);
        decision.Codes().ShouldBe([ReasonCodes.MerchantNotApproved]);
        decision.PrimaryReason.Message.ShouldBe("QuickCart Express is not on your approved merchant list");
        decision.PrimaryReason.Details["newMerchantAction"].ShouldBe("BLOCK");
        decision.RemainingBudget.ShouldBe(62.00m);
    }

    [Fact]
    public void E4_OverCategoryBudget_BlockedWithNegativeRemaining()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 85.00m)), SpendingOf(), Now);

        decision.Decision.ShouldBe(PolicyDecisionType.Blocked);
        decision.Codes().ShouldContain(ReasonCodes.CategoryBudgetExceeded);
        decision.Codes().ShouldContain(ReasonCodes.ApprovalThresholdExceeded);
        decision.PrimaryReason.Code.ShouldBe(ReasonCodes.CategoryBudgetExceeded);
        decision.PrimaryReason.Message.ShouldBe("This would exceed your weekly groceries budget of $80.00 by $5.00");
        decision.RemainingBudget.ShouldBe(-5.00m);
    }

    [Fact]
    public void E5_AfterPreviousCapture_BudgetExceeded()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 40.00m)),
            SpendingOf(groceriesWeekly: 43.00m), Now);

        decision.Decision.ShouldBe(PolicyDecisionType.Blocked);
        decision.PrimaryReason.Code.ShouldBe(ReasonCodes.CategoryBudgetExceeded);
        decision.BudgetImpacts.Single().CommittedBefore.ShouldBe(43.00m);
    }

    [Fact]
    public void E6_AlcoholAtNewMerchant_BlockedForBothReasons()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(QuickCart, "QuickCart Express", (CategoryCodes.Alcohol, 15.00m)), SpendingOf(), Now);

        decision.Decision.ShouldBe(PolicyDecisionType.Blocked);
        decision.Codes().ShouldContain(ReasonCodes.MerchantNotApproved);
        decision.Codes().ShouldContain(ReasonCodes.ProductRestricted);
        decision.PrimaryReason.Code.ShouldBe(ReasonCodes.MerchantNotApproved);
    }

    [Fact]
    public void E7_UnbudgetedCategory_RequiresApproval()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(FreshMart, "FreshMart", (CategoryCodes.Household, 12.00m)), SpendingOf(), Now);

        decision.Decision.ShouldBe(PolicyDecisionType.RequiresApproval);
        decision.PrimaryReason.Code.ShouldBe(ReasonCodes.UnbudgetedCategory);
        decision.PrimaryReason.Message.ShouldBe("You have no budget set for household");
        decision.RemainingBudget.ShouldBeNull();
    }

    [Fact]
    public void E8_ExactlyAtLimit_LimitIsInclusive()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 80.00m)), SpendingOf(), Now);

        decision.Decision.ShouldBe(PolicyDecisionType.RequiresApproval);
        decision.Codes().ShouldNotContain(ReasonCodes.CategoryBudgetExceeded);
        decision.PrimaryReason.Code.ShouldBe(ReasonCodes.ApprovalThresholdExceeded);
        decision.RemainingBudget.ShouldBe(0.00m);
    }

    [Fact]
    public void E9_ExactlyAtThreshold_ThresholdIsStrict()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 30.00m)), SpendingOf(), Now);

        decision.Decision.ShouldBe(PolicyDecisionType.Allowed);
        decision.Codes().ShouldNotContain(ReasonCodes.ApprovalThresholdExceeded);
    }

    [Fact]
    public void Threshold_OneCentAbove_RequiresApproval()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 30.01m)), SpendingOf(), Now);
        decision.Decision.ShouldBe(PolicyDecisionType.RequiresApproval);
    }

    // ----------------------------------------------------------- every reason code is producible

    [Fact]
    public void NoPolicy_BlocksWithNoActivePolicy()
    {
        var decision = PolicyEngine.Evaluate(null, Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m)), SpendingOf(), Now);
        decision.Decision.ShouldBe(PolicyDecisionType.Blocked);
        decision.PrimaryReason.Code.ShouldBe(ReasonCodes.NoActivePolicy);
    }

    [Fact]
    public void EmptyCart_Blocked()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(), Cart(FreshMart, "FreshMart"), SpendingOf(), Now);
        decision.PrimaryReason.Code.ShouldBe(ReasonCodes.CartEmpty);
    }

    [Fact]
    public void CurrencyMismatch_Blocked()
    {
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m)) with { Currency = "EUR" };
        PolicyEngine.Evaluate(DemoPolicy(), cart, SpendingOf(), Now).PrimaryReason.Code.ShouldBe(ReasonCodes.CurrencyMismatch);
    }

    [Fact]
    public void UnavailableProduct_Blocked()
    {
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m));
        cart = cart with { Items = [cart.Items[0] with { IsAvailable = false }] };
        PolicyEngine.Evaluate(DemoPolicy(), cart, SpendingOf(), Now).PrimaryReason.Code.ShouldBe(ReasonCodes.ProductUnavailable);
    }

    [Fact]
    public void QuantityAboveStock_Blocked()
    {
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m));
        cart = cart with { Items = [cart.Items[0] with { StockQuantity = 0 }] };
        PolicyEngine.Evaluate(DemoPolicy(), cart, SpendingOf(), Now).PrimaryReason.Code.ShouldBe(ReasonCodes.ProductUnavailable);
    }

    [Fact]
    public void BlockedMerchant_Blocked()
    {
        var policy = DemoPolicy(blocked: [new MerchantRule(Rule(8), TechNest)]);
        var decision = PolicyEngine.Evaluate(policy, Cart(TechNest, "TechNest", (CategoryCodes.Electronics, 5m)), SpendingOf(), Now);
        decision.PrimaryReason.Code.ShouldBe(ReasonCodes.MerchantBlocked);
        decision.PrimaryReason.Message.ShouldBe("TechNest is on your blocked merchant list");
    }

    [Theory]
    [InlineData(RuleAction.RequireApproval, ReasonCodes.NewMerchantRequiresApproval, PolicyDecisionType.RequiresApproval)]
    [InlineData(RuleAction.Allow, ReasonCodes.NewMerchantAllowed, PolicyDecisionType.Allowed)]
    public void NewMerchantActions(RuleAction action, string code, PolicyDecisionType expected)
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(newMerchantAction: action), Cart(QuickCart, "QuickCart Express", (CategoryCodes.Groceries, 5m)),
            SpendingOf(), Now);
        decision.Codes().ShouldContain(code);
        decision.Decision.ShouldBe(expected);
    }

    [Fact]
    public void ProductRestriction_RequireApproval()
    {
        var policy = DemoPolicy() with
        {
            ProductRestrictions = [new ProductRestrictionRule(Rule(9), ProductMatchType.Tag, "gift-card", RuleAction.RequireApproval, null)],
        };
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m));
        cart = cart with { Items = [cart.Items[0] with { Tags = ["Gift-Card"] }] };
        PolicyEngine.Evaluate(policy, cart, SpendingOf(), Now).PrimaryReason.Code.ShouldBe(ReasonCodes.ProductRequiresApproval);
    }

    [Fact]
    public void ProductRestriction_ProductMatchOverridesCategory()
    {
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Alcohol, 5m));
        var policy = DemoPolicy() with
        {
            ProductRestrictions =
            [
                new ProductRestrictionRule(Rule(9), ProductMatchType.Category, CategoryCodes.Alcohol, RuleAction.Block, null),
                new ProductRestrictionRule(Rule(10), ProductMatchType.Product, cart.Items[0].ProductId.ToString(), RuleAction.Allow, null),
            ],
        };
        PolicyEngine.Evaluate(policy, cart, SpendingOf(), Now).Codes().ShouldNotContain(ReasonCodes.ProductRestricted);
    }

    [Fact]
    public void MaxTransaction_Blocked()
    {
        var decision = PolicyEngine.Evaluate(DemoPolicy(max: new AmountRule(Rule(11), 50m)),
            Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 60m)), SpendingOf(), Now);
        decision.Codes().ShouldContain(ReasonCodes.MaxTransactionExceeded);
    }

    [Theory]
    [InlineData(BudgetPeriodType.Weekly, ReasonCodes.WeeklyLimitExceeded)]
    [InlineData(BudgetPeriodType.Monthly, ReasonCodes.MonthlyLimitExceeded)]
    public void OverallLimits_Blocked(BudgetPeriodType period, string code)
    {
        var policy = DemoPolicy(limits: [new SpendingLimitRule(Rule(12), period, 100m)]);
        var decision = PolicyEngine.Evaluate(policy, Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 20m)), SpendingOf(totalWeekly: 90m), Now);
        decision.Codes().ShouldContain(code);
        decision.BudgetImpacts.ShouldContain(i => i.Scope == BudgetScope.Overall && i.Exceeded);
    }

    [Fact]
    public void DuplicatePurchase_SameProduct()
    {
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m));
        var recent = new RecentPurchase(Guid.NewGuid(), Now.AddHours(-2), FreshMart, [cart.Items[0].ProductId], "x");
        var policy = DemoPolicy(duplicate: new DuplicatePurchaseRule(Rule(13), 24, DuplicateScope.SameProduct, RuleAction.RequireApproval));

        var decision = PolicyEngine.Evaluate(policy, cart, SpendingOf(recent: [recent]), Now);
        decision.PrimaryReason.Code.ShouldBe(ReasonCodes.DuplicatePurchase);
        decision.PrimaryReason.Message.ShouldBe("You bought Item 0 in the last 24 hours");
    }

    [Fact]
    public void DuplicatePurchase_OutsideWindow_Ignored()
    {
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m));
        var recent = new RecentPurchase(Guid.NewGuid(), Now.AddHours(-25), FreshMart, [cart.Items[0].ProductId], "x");
        var policy = DemoPolicy(duplicate: new DuplicatePurchaseRule(Rule(13), 24, DuplicateScope.SameProduct, RuleAction.Block));
        PolicyEngine.Evaluate(policy, cart, SpendingOf(recent: [recent]), Now).Decision.ShouldBe(PolicyDecisionType.Allowed);
    }

    [Fact]
    public void DuplicatePurchase_SameCartHash()
    {
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m));
        var recent = new RecentPurchase(Guid.NewGuid(), Now.AddHours(-1), FreshMart, [], cart.ComputeHash());
        var policy = DemoPolicy(duplicate: new DuplicatePurchaseRule(Rule(13), 24, DuplicateScope.SameCart, RuleAction.Block));
        PolicyEngine.Evaluate(policy, cart, SpendingOf(recent: [recent]), Now).PrimaryReason.Code.ShouldBe(ReasonCodes.DuplicatePurchase);
    }

    // ----------------------------------------------------------- time windows

    private static TimeRestrictionRule Window(string start, string end, params DayOfWeek[] days) =>
        new(Rule(14), days.Length == 0 ? Enum.GetValues<DayOfWeek>() : days, TimeOnly.Parse(start), TimeOnly.Parse(end), RuleAction.Block);

    [Theory]
    [InlineData("2026-11-03T15:00:00Z", true)]   // 10:00 EST, inside 09:00–17:00
    [InlineData("2026-11-03T22:00:00Z", false)]  // 17:00 EST, end exclusive
    [InlineData("2026-11-03T14:00:00Z", true)]   // 09:00 EST, start inclusive
    public void TimeRestriction_SameDayWindow(string instant, bool restricted)
    {
        var policy = DemoPolicy(times: [Window("09:00", "17:00")]);
        var decision = PolicyEngine.Evaluate(policy, Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m)), SpendingOf(),
            DateTimeOffset.Parse(instant));
        decision.Codes().Contains(ReasonCodes.TimeRestricted).ShouldBe(restricted);
    }

    [Theory]
    [InlineData("2026-11-07T04:30:00Z", true)]  // Fri 23:30 EST — window starts Friday
    [InlineData("2026-11-07T10:00:00Z", true)]  // Sat 05:00 EST — continuation of Friday's window
    [InlineData("2026-11-08T10:00:00Z", false)] // Sun 05:00 EST — Saturday's window not listed
    public void TimeRestriction_OvernightWindow_DayIsStartDay(string instant, bool restricted)
    {
        var policy = DemoPolicy(times: [Window("23:00", "06:00", DayOfWeek.Friday)]);
        var decision = PolicyEngine.Evaluate(policy, Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m)), SpendingOf(),
            DateTimeOffset.Parse(instant));
        decision.Codes().Contains(ReasonCodes.TimeRestricted).ShouldBe(restricted);
    }

    [Fact]
    public void TimeRestriction_AcrossDstChange_UsesLocalTime()
    {
        // 1 Nov 2026: America/New_York falls back at 02:00 EDT → 01:00 EST.
        var policy = DemoPolicy(times: [Window("01:00", "02:00", DayOfWeek.Sunday)]);
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 5m));
        PolicyEngine.Evaluate(policy, cart, SpendingOf(), DateTimeOffset.Parse("2026-11-01T05:30:00Z")).Codes()
            .ShouldContain(ReasonCodes.TimeRestricted); // 01:30 EDT
        PolicyEngine.Evaluate(policy, cart, SpendingOf(), DateTimeOffset.Parse("2026-11-01T06:30:00Z")).Codes()
            .ShouldContain(ReasonCodes.TimeRestricted); // 01:30 EST (repeated hour)
        PolicyEngine.Evaluate(policy, cart, SpendingOf(), DateTimeOffset.Parse("2026-11-01T07:30:00Z")).Codes()
            .ShouldNotContain(ReasonCodes.TimeRestricted); // 02:30 EST
    }

    // ----------------------------------------------------------- periods & determinism

    [Fact]
    public void WeekBoundary_SundayVsMonday_LocalTime()
    {
        // Sunday 8 Nov 23:59 EST = Mon 9 Nov 04:59Z
        BudgetPeriods.PeriodStarts(DateTimeOffset.Parse("2026-11-09T04:59:00Z"), "America/New_York").WeekStart
            .ShouldBe(new DateOnly(2026, 11, 2));
        BudgetPeriods.PeriodStarts(DateTimeOffset.Parse("2026-11-09T05:00:00Z"), "America/New_York").WeekStart
            .ShouldBe(new DateOnly(2026, 11, 9));
    }

    [Fact]
    public void Evaluate_SameInputs100Times_IdenticalOutput()
    {
        var cart = Cart(QuickCart, "QuickCart Express", (CategoryCodes.Alcohol, 15m), (CategoryCodes.Groceries, 90m), (CategoryCodes.Household, 3m));
        var first = JsonSerializer.Serialize(PolicyEngine.Evaluate(DemoPolicy(), cart, SpendingOf(), Now));
        for (var i = 0; i < 100; i++)
        {
            JsonSerializer.Serialize(PolicyEngine.Evaluate(DemoPolicy(), cart, SpendingOf(), Now)).ShouldBe(first);
        }
    }

    [Fact]
    public void DecisionInvariants_HoldForMixedReasons()
    {
        var cart = Cart(QuickCart, "QuickCart Express", (CategoryCodes.Alcohol, 15m), (CategoryCodes.Household, 3m));
        var decision = PolicyEngine.Evaluate(DemoPolicy(), cart, SpendingOf(), Now);

        (decision.Decision == PolicyDecisionType.Blocked).ShouldBe(decision.Reasons.Any(r => r.Severity == ReasonSeverity.Block));
        decision.Reasons.ShouldNotContain(r => r.Code == ReasonCodes.AllPoliciesSatisfied);
    }
}
