using Guardrail.Domain.Common;

namespace Guardrail.Domain.Policy;

/// <summary>
/// The final authority on whether a purchase may proceed (BR-01, ADR-002).
/// Pure and deterministic: no I/O, no clock reads, no randomness.
/// </summary>
public static class PolicyEngine
{
    // Check numbers from SPENDING_POLICY_ENGINE.md §2; used as the primary ordering key.
    private const int CheckNoPolicy = 0;
    private const int CheckCartEmpty = 1;
    private const int CheckCurrency = 2;
    private const int CheckAvailability = 3;
    private const int CheckMerchant = 10;
    private const int CheckProductRestriction = 20;
    private const int CheckMaxTransaction = 30;
    private const int CheckCategoryBudget = 40;
    private const int CheckUnbudgeted = 41;
    private const int CheckOverallLimit = 50;
    private const int CheckDuplicate = 60;
    private const int CheckTime = 70;
    private const int CheckThreshold = 80;
    private const int CheckAllClear = 90;

    public static PolicyDecision Evaluate(PolicySnapshot? policy, CartSnapshot cart, SpendingSnapshot spending, DateTimeOffset now)
    {
        var items = cart.Items.OrderBy(i => i.ProductId.ToString("D"), StringComparer.Ordinal).ToList();
        var reasons = new List<PolicyReason>();
        var impacts = new List<BudgetImpact>();
        var currency = cart.Currency;

        // 0a–0d: preconditions
        if (policy is null)
        {
            reasons.Add(Reason(CheckNoPolicy, ReasonCodes.NoActivePolicy, ReasonSeverity.Block, "You have no active spending policy", null, null));
        }

        if (items.Count == 0)
        {
            reasons.Add(Reason(CheckCartEmpty, ReasonCodes.CartEmpty, ReasonSeverity.Block, "The cart is empty", null, null));
        }

        if (policy is not null && !string.Equals(cart.Currency, policy.Currency, StringComparison.Ordinal))
        {
            reasons.Add(Reason(CheckCurrency, ReasonCodes.CurrencyMismatch, ReasonSeverity.Block,
                $"Cart currency {cart.Currency} does not match your policy currency {policy.Currency}", null, null,
                ("cartCurrency", cart.Currency), ("policyCurrency", policy.Currency)));
        }

        foreach (var item in items.Where(i => !i.IsAvailable || i.Quantity > i.StockQuantity))
        {
            reasons.Add(Reason(CheckAvailability, ReasonCodes.ProductUnavailable, ReasonSeverity.Block, $"{item.Name} is unavailable",
                null, null, ("productId", item.ProductId.ToString("D")), ("requestedQuantity", item.Quantity.ToString()),
                ("stockQuantity", item.StockQuantity.ToString())) with { SortCategory = item.CategoryCode });
        }

        if (policy is not null)
        {
            EvaluateMerchant(policy, cart, reasons);
            EvaluateProductRestrictions(policy, items, reasons);
            EvaluateMaxTransaction(policy, cart, reasons);
            EvaluateCategoryBudgets(policy, cart, spending, reasons, impacts);
            EvaluateOverallLimits(policy, cart, spending, reasons, impacts);
            EvaluateDuplicates(policy, cart, items, spending, now, reasons);
            EvaluateTimeRestrictions(policy, now, reasons);
            EvaluateThreshold(policy, cart, reasons);
        }

        if (!reasons.Any(r => r.Severity is ReasonSeverity.Block or ReasonSeverity.RequireApproval))
        {
            reasons.Add(Reason(CheckAllClear, ReasonCodes.AllPoliciesSatisfied, ReasonSeverity.Info, "All your spending rules are satisfied", null, null));
        }

        var ordered = reasons
            .OrderBy(r => r.Check)
            .ThenBy(r => r.SortCategory, StringComparer.Ordinal)
            .ThenBy(r => r.RuleId?.ToString("D") ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        return new PolicyDecision(
            PolicyDecision.DecisionOf(ordered),
            ordered,
            impacts,
            cart.Total,
            RemainingBudget(impacts),
            currency,
            cart.ComputeHash());
    }

    private static void EvaluateMerchant(PolicySnapshot policy, CartSnapshot cart, List<PolicyReason> reasons)
    {
        var merchantId = cart.MerchantId.ToString("D");
        var blocked = policy.BlockedMerchants.FirstOrDefault(m => m.MerchantId == cart.MerchantId);
        if (blocked is not null)
        {
            reasons.Add(Reason(CheckMerchant, ReasonCodes.MerchantBlocked, ReasonSeverity.Block,
                $"{cart.MerchantName} is on your blocked merchant list", PolicyRuleType.MerchantBlock, blocked.RuleId,
                ("merchantId", merchantId)));
            return;
        }

        var approved = policy.ApprovedMerchants.FirstOrDefault(m => m.MerchantId == cart.MerchantId);
        if (approved is not null)
        {
            reasons.Add(Reason(CheckMerchant, ReasonCodes.MerchantApproved, ReasonSeverity.Info,
                $"{cart.MerchantName} is on your approved list", PolicyRuleType.MerchantAllow, approved.RuleId,
                ("merchantId", merchantId)));
            return;
        }

        var action = policy.NewMerchantPolicy?.Action ?? RuleAction.Block;
        var ruleId = policy.NewMerchantPolicy?.RuleId;
        var actionText = EnumText.ToText(action);
        reasons.Add(action switch
        {
            RuleAction.Block => Reason(CheckMerchant, ReasonCodes.MerchantNotApproved, ReasonSeverity.Block,
                $"{cart.MerchantName} is not on your approved merchant list", PolicyRuleType.NewMerchantPolicy, ruleId,
                ("merchantId", merchantId), ("newMerchantAction", actionText)),
            RuleAction.RequireApproval => Reason(CheckMerchant, ReasonCodes.NewMerchantRequiresApproval, ReasonSeverity.RequireApproval,
                $"{cart.MerchantName} is new to you and needs your approval", PolicyRuleType.NewMerchantPolicy, ruleId,
                ("merchantId", merchantId), ("newMerchantAction", actionText)),
            _ => Reason(CheckMerchant, ReasonCodes.NewMerchantAllowed, ReasonSeverity.Info,
                $"{cart.MerchantName} is new to you; your rules allow new merchants", PolicyRuleType.NewMerchantPolicy, ruleId,
                ("merchantId", merchantId), ("newMerchantAction", actionText)),
        });
    }

    private static void EvaluateProductRestrictions(PolicySnapshot policy, List<CartSnapshotItem> items, List<PolicyReason> reasons)
    {
        foreach (var item in items)
        {
            var match = MatchRestriction(policy, item);
            if (match is null || match.Action == RuleAction.Allow)
            {
                continue;
            }

            var block = match.Action == RuleAction.Block;
            reasons.Add(Reason(CheckProductRestriction,
                block ? ReasonCodes.ProductRestricted : ReasonCodes.ProductRequiresApproval,
                block ? ReasonSeverity.Block : ReasonSeverity.RequireApproval,
                block ? $"{item.Name} is restricted by your rules" : $"{item.Name} needs your approval under your rules",
                PolicyRuleType.ProductRestriction, match.RuleId,
                ("productId", item.ProductId.ToString("D")),
                ("matchType", EnumText.ToText(match.MatchType)),
                ("matchValue", match.MatchValue),
                ("note", match.Note)) with { SortCategory = item.CategoryCode });
        }
    }

    /// <summary>Precedence: product → category → tag (most specific wins).</summary>
    public static ProductRestrictionRule? MatchRestriction(PolicySnapshot policy, CartSnapshotItem item)
    {
        var productId = item.ProductId.ToString("D");
        return policy.ProductRestrictions.FirstOrDefault(r =>
                   r.MatchType == ProductMatchType.Product && string.Equals(r.MatchValue, productId, StringComparison.OrdinalIgnoreCase))
               ?? policy.ProductRestrictions.FirstOrDefault(r =>
                   r.MatchType == ProductMatchType.Category && string.Equals(r.MatchValue, item.CategoryCode, StringComparison.Ordinal))
               ?? policy.ProductRestrictions.FirstOrDefault(r =>
                   r.MatchType == ProductMatchType.Tag && item.Tags.Contains(r.MatchValue, StringComparer.OrdinalIgnoreCase));
    }

    private static void EvaluateMaxTransaction(PolicySnapshot policy, CartSnapshot cart, List<PolicyReason> reasons)
    {
        if (policy.MaxTransactionAmount is { } max && cart.Total > max.Amount)
        {
            reasons.Add(Reason(CheckMaxTransaction, ReasonCodes.MaxTransactionExceeded, ReasonSeverity.Block,
                $"Purchase exceeds your maximum transaction amount of {Money(max.Amount, cart.Currency)}",
                PolicyRuleType.MaxTransactionAmount, max.RuleId,
                ("limit", MoneyFormat.Format(max.Amount)), ("purchaseAmount", MoneyFormat.Format(cart.Total))));
        }
    }

    private static void EvaluateCategoryBudgets(
        PolicySnapshot policy, CartSnapshot cart, SpendingSnapshot spending, List<PolicyReason> reasons, List<BudgetImpact> impacts)
    {
        foreach (var (category, amount) in cart.AmountByCategory())
        {
            var rules = policy.CategoryBudgets
                .Where(b => b.CategoryCode == category)
                .OrderBy(b => b.Period)
                .ToList();

            if (rules.Count == 0)
            {
                var action = policy.UnbudgetedCategoryPolicy?.Action ?? RuleAction.Allow;
                if (action != RuleAction.Allow)
                {
                    reasons.Add(Reason(CheckUnbudgeted, ReasonCodes.UnbudgetedCategory, SeverityOf(action),
                        $"You have no budget set for {CategoryCodes.DisplayName(category)}",
                        PolicyRuleType.UnbudgetedCategoryPolicy, policy.UnbudgetedCategoryPolicy?.RuleId,
                        ("categoryCode", category), ("unbudgetedCategoryAction", EnumText.ToText(action))) with { SortCategory = category });
                }

                continue;
            }

            foreach (var rule in rules)
            {
                var committed = spending.CommittedFor(category, rule.Period);
                var impact = Impact(BudgetScope.Category, category, rule.Period, spending.PeriodStart(rule.Period), rule.Limit, committed, amount);
                impacts.Add(impact);

                if (impact.Exceeded)
                {
                    reasons.Add(Reason(CheckCategoryBudget, ReasonCodes.CategoryBudgetExceeded, ReasonSeverity.Block,
                        $"This would exceed your {PeriodText(rule.Period)} {CategoryCodes.DisplayName(category)} budget of " +
                        $"{Money(rule.Limit, cart.Currency)} by {Money(-impact.RemainingAfter, cart.Currency)}",
                        PolicyRuleType.CategoryBudget, rule.RuleId,
                        ("categoryCode", category), ("period", EnumText.ToText(rule.Period)),
                        ("limit", MoneyFormat.Format(rule.Limit)), ("committedBefore", MoneyFormat.Format(committed)),
                        ("purchaseAmount", MoneyFormat.Format(amount)), ("overBy", MoneyFormat.Format(-impact.RemainingAfter)))
                        with { SortCategory = category });
                }
            }
        }
    }

    private static void EvaluateOverallLimits(
        PolicySnapshot policy, CartSnapshot cart, SpendingSnapshot spending, List<PolicyReason> reasons, List<BudgetImpact> impacts)
    {
        foreach (var rule in policy.SpendingLimits.OrderBy(l => l.Period))
        {
            var committed = spending.CommittedTotal(rule.Period);
            var impact = Impact(BudgetScope.Overall, null, rule.Period, spending.PeriodStart(rule.Period), rule.Limit, committed, cart.Total);
            impacts.Add(impact);

            if (impact.Exceeded)
            {
                reasons.Add(Reason(CheckOverallLimit,
                    rule.Period == BudgetPeriodType.Weekly ? ReasonCodes.WeeklyLimitExceeded : ReasonCodes.MonthlyLimitExceeded,
                    ReasonSeverity.Block,
                    $"This would exceed your {PeriodText(rule.Period)} spending limit of {Money(rule.Limit, cart.Currency)}",
                    PolicyRuleType.SpendingLimit, rule.RuleId,
                    ("period", EnumText.ToText(rule.Period)), ("limit", MoneyFormat.Format(rule.Limit)),
                    ("committedBefore", MoneyFormat.Format(committed)), ("purchaseAmount", MoneyFormat.Format(cart.Total))));
            }
        }
    }

    private static void EvaluateDuplicates(
        PolicySnapshot policy, CartSnapshot cart, List<CartSnapshotItem> items, SpendingSnapshot spending, DateTimeOffset now,
        List<PolicyReason> reasons)
    {
        if (policy.DuplicatePurchase is not { } rule || rule.Action == RuleAction.Allow)
        {
            return;
        }

        var since = now.AddHours(-rule.WindowHours);
        var recent = spending.RecentPurchases.Where(p => p.CreatedAt >= since).ToList();
        if (recent.Count == 0)
        {
            return;
        }

        if (rule.Scope == DuplicateScope.SameCart)
        {
            var hash = cart.ComputeHash();
            var match = recent.FirstOrDefault(p => p.CartSnapshotHash == hash);
            if (match is not null)
            {
                reasons.Add(Reason(CheckDuplicate, ReasonCodes.DuplicatePurchase, SeverityOf(rule.Action),
                    $"You bought this cart in the last {rule.WindowHours} hours", PolicyRuleType.DuplicatePurchase, rule.RuleId,
                    ("purchaseId", match.PurchaseId.ToString("D")), ("windowHours", rule.WindowHours.ToString())));
            }

            return;
        }

        foreach (var item in items)
        {
            var match = recent.FirstOrDefault(p => p.ProductIds.Contains(item.ProductId));
            if (match is not null)
            {
                reasons.Add(Reason(CheckDuplicate, ReasonCodes.DuplicatePurchase, SeverityOf(rule.Action),
                    $"You bought {item.Name} in the last {rule.WindowHours} hours", PolicyRuleType.DuplicatePurchase, rule.RuleId,
                    ("productId", item.ProductId.ToString("D")), ("purchaseId", match.PurchaseId.ToString("D")),
                    ("windowHours", rule.WindowHours.ToString())) with { SortCategory = item.CategoryCode });
            }
        }
    }

    private static void EvaluateTimeRestrictions(PolicySnapshot policy, DateTimeOffset now, List<PolicyReason> reasons)
    {
        if (policy.TimeRestrictions.Count == 0)
        {
            return;
        }

        var local = BudgetPeriods.ToLocal(now, policy.TimeZone);
        foreach (var rule in policy.TimeRestrictions.Where(r => r.Action != RuleAction.Allow))
        {
            if (IsInsideWindow(rule, local))
            {
                reasons.Add(Reason(CheckTime, ReasonCodes.TimeRestricted, SeverityOf(rule.Action),
                    $"Purchases are restricted between {rule.StartLocalTime:HH\\:mm} and {rule.EndLocalTime:HH\\:mm}",
                    PolicyRuleType.TimeRestriction, rule.RuleId,
                    ("startLocalTime", rule.StartLocalTime.ToString("HH:mm")), ("endLocalTime", rule.EndLocalTime.ToString("HH:mm"))));
            }
        }
    }

    /// <summary>
    /// start &lt; end: same-day window [start, end). start &gt; end: overnight window; the day refers to the day the window starts.
    /// </summary>
    public static bool IsInsideWindow(TimeRestrictionRule rule, DateTime local)
    {
        var time = TimeOnly.FromDateTime(local);
        var day = local.DayOfWeek;
        if (rule.StartLocalTime < rule.EndLocalTime)
        {
            return rule.DaysOfWeek.Contains(day) && time >= rule.StartLocalTime && time < rule.EndLocalTime;
        }

        var previousDay = (DayOfWeek)(((int)day + 6) % 7);
        return (rule.DaysOfWeek.Contains(day) && time >= rule.StartLocalTime)
               || (rule.DaysOfWeek.Contains(previousDay) && time < rule.EndLocalTime);
    }

    private static void EvaluateThreshold(PolicySnapshot policy, CartSnapshot cart, List<PolicyReason> reasons)
    {
        // Strict: total == threshold does not need approval.
        if (policy.ApprovalThreshold is { } threshold && cart.Total > threshold.Amount)
        {
            reasons.Add(Reason(CheckThreshold, ReasonCodes.ApprovalThresholdExceeded, ReasonSeverity.RequireApproval,
                "Purchase exceeds approval threshold", PolicyRuleType.ApprovalThreshold, threshold.RuleId,
                ("threshold", MoneyFormat.Format(threshold.Amount)), ("purchaseAmount", MoneyFormat.Format(cart.Total))));
        }
    }

    private static BudgetImpact Impact(
        BudgetScope scope, string? category, BudgetPeriodType period, DateOnly periodStart, decimal limit, decimal committed, decimal amount)
    {
        var remainingBefore = limit - committed;
        var remainingAfter = remainingBefore - amount;
        // Inclusive limit: projected == limit is allowed.
        return new BudgetImpact(scope, category, period, periodStart, limit, committed, amount, remainingBefore, remainingAfter, remainingAfter < 0);
    }

    private static decimal? RemainingBudget(List<BudgetImpact> impacts)
    {
        var category = impacts.Where(i => i.Scope == BudgetScope.Category).ToList();
        if (category.Count > 0)
        {
            return category.Min(i => i.RemainingAfter);
        }

        var overall = impacts.Where(i => i.Scope == BudgetScope.Overall).ToList();
        return overall.Count > 0 ? overall.Min(i => i.RemainingAfter) : null;
    }

    private static ReasonSeverity SeverityOf(RuleAction action) => action switch
    {
        RuleAction.Block => ReasonSeverity.Block,
        RuleAction.RequireApproval => ReasonSeverity.RequireApproval,
        _ => ReasonSeverity.Info,
    };

    private static string PeriodText(BudgetPeriodType period) => period == BudgetPeriodType.Weekly ? "weekly" : "monthly";

    public static string Money(decimal amount, string currency) =>
        currency == "USD"
            ? (amount < 0 ? "-$" : "$") + MoneyFormat.Format(Math.Abs(amount))
            : $"{MoneyFormat.Format(amount)} {currency}";

    private static PolicyReason Reason(
        int check, string code, ReasonSeverity severity, string message, PolicyRuleType? ruleType, Guid? ruleId,
        params (string Key, string? Value)[] details) =>
        new(code, severity, message, ruleType, ruleId, details.ToDictionary(d => d.Key, d => d.Value))
        {
            Check = check,
        };
}
