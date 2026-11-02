using Guardrail.Application.Common;
using Guardrail.Domain.Catalogue;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;
using Guardrail.Domain.Shopping;

namespace Guardrail.Application.Dtos;

public static class DtoMapper
{
    public static CartItemDto ToDto(this CartItem item) =>
        new(item.ProductId, item.ProductName, item.Sku, item.CategoryCode, MoneyFormat.Format(item.UnitPrice), item.Quantity,
            MoneyFormat.Format(item.LineTotal));

    public static CartDto ToDto(this ShoppingCart cart, string merchantName, string snapshotHash) =>
        new(cart.Id, cart.Version, cart.Status, cart.MerchantId, merchantName, cart.Currency,
            cart.Items.OrderBy(i => i.CreatedAt).ThenBy(i => i.ProductName).Select(ToDto).ToList(),
            MoneyFormat.Format(cart.Subtotal), MoneyFormat.Format(cart.Total), cart.CreatedByActor, snapshotHash, cart.UpdatedAt);

    public static ProductDto ToDto(this Product product, string merchantName, MerchantStatus merchantStatus, bool includeDescription = false) =>
        new(product.Id, product.MerchantId, merchantName, merchantStatus, product.Sku, product.Name, product.Brand, product.CategoryCode,
            product.Tags, MoneyFormat.Format(product.UnitPrice), product.Currency, product.UnitLabel, product.ImageUrl, product.IsAvailable,
            product.StockQuantity, includeDescription ? product.Description : null);

    public static PolicyReasonDto ToDto(this PolicyReason reason) =>
        new(reason.Code, reason.Severity, reason.Message, reason.RuleType, reason.RuleId?.ToString("D"), reason.Details);

    public static BudgetImpactDto ToDto(this BudgetImpact impact) =>
        new(impact.Scope, impact.CategoryCode, impact.Period, impact.PeriodStart, MoneyFormat.Format(impact.Limit),
            MoneyFormat.Format(impact.CommittedBefore), MoneyFormat.Format(impact.PurchaseAmount), MoneyFormat.Format(impact.RemainingBefore),
            MoneyFormat.Format(impact.RemainingAfter), impact.Exceeded);

    public static IReadOnlyList<PolicyReasonDto> ReasonsOf(string json) => JsonDefaults.Deserialize<List<PolicyReasonDto>>(json);

    public static IReadOnlyList<BudgetImpactDto> ImpactsOf(string json) => JsonDefaults.Deserialize<List<BudgetImpactDto>>(json);

    /// <summary>Rebuilds the full decision contract from a persisted evaluation (invariant I7).</summary>
    public static PolicyDecisionDto ToDecisionDto(this PolicyEvaluation evaluation, int policyVersionNumber)
    {
        var reasons = ReasonsOf(evaluation.Reasons);
        var impacts = ImpactsOf(evaluation.BudgetImpacts);
        var primary = reasons.FirstOrDefault(r => r.Severity == ReasonSeverity.Block)
                      ?? reasons.FirstOrDefault(r => r.Severity == ReasonSeverity.RequireApproval)
                      ?? reasons.FirstOrDefault(r => r.Code == ReasonCodes.AllPoliciesSatisfied)
                      ?? reasons.FirstOrDefault();

        return new PolicyDecisionDto(
            evaluation.Id,
            evaluation.Decision,
            evaluation.Decision == PolicyDecisionType.Allowed,
            evaluation.Decision == PolicyDecisionType.RequiresApproval,
            primary?.Message ?? string.Empty,
            primary?.Code ?? string.Empty,
            MoneyFormat.Format(evaluation.Amount),
            RemainingBudget(impacts),
            evaluation.Currency,
            evaluation.PolicyVersionId,
            policyVersionNumber,
            evaluation.CartId,
            evaluation.CartVersion,
            evaluation.CartSnapshotHash,
            reasons,
            impacts,
            evaluation.EvaluatedAt);
    }

    /// <summary>Smallest remainingAfter across category impacts; else overall; else null (SPENDING_POLICY_ENGINE.md §3).</summary>
    public static string? RemainingBudget(IReadOnlyList<BudgetImpactDto> impacts)
    {
        static decimal Parse(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        var category = impacts.Where(i => i.Scope == BudgetScope.Category).Select(i => Parse(i.RemainingAfter)).ToList();
        if (category.Count > 0)
        {
            return MoneyFormat.Format(category.Min());
        }

        var overall = impacts.Where(i => i.Scope == BudgetScope.Overall).Select(i => Parse(i.RemainingAfter)).ToList();
        return overall.Count > 0 ? MoneyFormat.Format(overall.Min()) : null;
    }
}
