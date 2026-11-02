using Guardrail.Domain.Common;

namespace Guardrail.Domain.Policy;

public sealed record FinalCheckReason(string Code, string Message);

public sealed record FinalCheckResult(bool Passed, IReadOnlyList<FinalCheckReason> Reasons, DateTimeOffset CheckedAt);

/// <summary>
/// Pre-capture "last-mile" check between PayPal authorization and capture (BR-67, PAYMENT_STATE_MACHINE.md §1).
/// Budget is deliberately not re-checked: it was reserved under lock at purchase creation.
/// </summary>
public static class FinalGuardrailCheck
{
    public static FinalCheckResult Run(
        PolicySnapshot currentPolicy,
        CartSnapshot recomputedCart,
        string purchaseSnapshotHash,
        decimal purchaseAmount,
        string purchaseCurrency,
        decimal? authorizedAmount,
        string? authorizedCurrency,
        DateTimeOffset now)
    {
        var reasons = new List<FinalCheckReason>();

        if (recomputedCart.ComputeHash() != purchaseSnapshotHash)
        {
            reasons.Add(new FinalCheckReason("CART_CHANGED", "The cart no longer matches the approved snapshot"));
        }

        if (authorizedAmount is null || authorizedAmount != purchaseAmount || authorizedCurrency != purchaseCurrency)
        {
            reasons.Add(new FinalCheckReason(ErrorCodes.AmountMismatch,
                $"Authorized amount {MoneyFormat.Format(authorizedAmount)} {authorizedCurrency} does not match purchase " +
                $"{MoneyFormat.Format(purchaseAmount)} {purchaseCurrency}"));
        }

        if (currentPolicy.BlockedMerchants.Any(m => m.MerchantId == recomputedCart.MerchantId))
        {
            reasons.Add(new FinalCheckReason(ReasonCodes.MerchantBlocked, $"{recomputedCart.MerchantName} is on your blocked merchant list"));
        }

        foreach (var item in recomputedCart.Items)
        {
            if (PolicyEngine.MatchRestriction(currentPolicy, item) is { Action: RuleAction.Block })
            {
                reasons.Add(new FinalCheckReason(ReasonCodes.ProductRestricted, $"{item.Name} is restricted by your rules"));
            }
        }

        return new FinalCheckResult(reasons.Count == 0, reasons, now);
    }
}
