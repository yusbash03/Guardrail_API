using System.Globalization;
using FluentValidation;
using Guardrail.Application.Common;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;

namespace Guardrail.Application.Policies;

/// <summary>Structural validation of a PolicyDocument (SPENDING_POLICY_ENGINE.md §1.2 → VALIDATION_FAILED).</summary>
public sealed class PolicyDocumentValidator : AbstractValidator<PolicyDocument>
{
    private const decimal MaxAmount = 100_000m;

    public PolicyDocumentValidator()
    {
        RuleFor(d => d.MerchantRules).NotNull();
        RuleFor(d => d.SpendingLimits).NotNull();
        RuleFor(d => d.CategoryBudgets).NotNull();
        RuleFor(d => d.ProductRestrictions).NotNull();
        RuleFor(d => d.TimeRestrictions).NotNull();

        When(d => d.ApprovalThreshold is not null, () =>
            RuleFor(d => d.ApprovalThreshold!.Amount).MoneyString().Must(a => InRange(a, inclusiveZero: true))
                .WithMessage("Threshold must be between 0.00 and 100000.00."));

        When(d => d.MaxTransactionAmount is not null, () =>
            RuleFor(d => d.MaxTransactionAmount!.Amount).MoneyString().Must(a => InRange(a, inclusiveZero: false))
                .WithMessage("Amount must be greater than 0.00 and at most 100000.00."));

        RuleForEach(d => d.SpendingLimits).ChildRules(l =>
        {
            l.RuleFor(x => x.Period).OneOf<SpendingLimitDocument, BudgetPeriodType>();
            l.RuleFor(x => x.Limit).MoneyString().Must(a => InRange(a, inclusiveZero: false))
                .WithMessage("Limit must be greater than 0.00 and at most 100000.00.");
        });

        RuleForEach(d => d.CategoryBudgets).ChildRules(b =>
        {
            b.RuleFor(x => x.CategoryCode).NotEmpty().MaximumLength(40);
            b.RuleFor(x => x.Period).OneOf<CategoryBudgetDocument, BudgetPeriodType>();
            b.RuleFor(x => x.Limit).MoneyString().Must(a => InRange(a, inclusiveZero: false))
                .WithMessage("Limit must be greater than 0.00 and at most 100000.00.");
        });

        RuleFor(d => d.MerchantRules.NewMerchantAction).OneOf<PolicyDocument, RuleAction>().When(d => d.MerchantRules is not null);
        RuleFor(d => d.MerchantRules.ApprovedMerchantIds).NotNull().Must(l => l.Count <= 200).When(d => d.MerchantRules is not null);
        RuleFor(d => d.MerchantRules.BlockedMerchantIds).NotNull().Must(l => l.Count <= 200).When(d => d.MerchantRules is not null);
        RuleFor(d => d.UnbudgetedCategoryAction).OneOf<PolicyDocument, RuleAction>();

        RuleFor(d => d.ProductRestrictions).Must(l => l.Count <= 100);
        RuleForEach(d => d.ProductRestrictions).ChildRules(r =>
        {
            r.RuleFor(x => x.Match).NotNull();
            r.RuleFor(x => x.Match.Type).OneOf<ProductRestrictionDocument, ProductMatchType>().When(x => x.Match is not null);
            r.RuleFor(x => x.Match.Value).NotEmpty().MaximumLength(100).When(x => x.Match is not null);
            r.RuleFor(x => x.Match.Value).Must(v => Guid.TryParse(v, out _))
                .When(x => x.Match is not null && x.Match.Type == "PRODUCT").WithMessage("Product match value must be a product id.");
            r.RuleFor(x => x.Action).OneOf<ProductRestrictionDocument, RuleAction>();
            r.RuleFor(x => x.Note).MaximumLength(200);
        });

        When(d => d.DuplicatePurchase is not null, () =>
        {
            RuleFor(d => d.DuplicatePurchase!.WindowHours).InclusiveBetween(1, 720);
            RuleFor(d => d.DuplicatePurchase!.Scope).OneOf<PolicyDocument, DuplicateScope>();
            RuleFor(d => d.DuplicatePurchase!.Action).OneOf<PolicyDocument, RuleAction>();
        });

        RuleFor(d => d.TimeRestrictions).Must(l => l.Count <= 20);
        RuleForEach(d => d.TimeRestrictions).ChildRules(t =>
        {
            t.RuleFor(x => x.DaysOfWeek).NotEmpty().Must(d => d.Distinct().Count() == d.Count).WithMessage("Days must be unique.");
            t.RuleForEach(x => x.DaysOfWeek).Must(d => PolicyMapper.TryParseDay(d, out _)).WithMessage("Days must be MON–SUN.");
            t.RuleFor(x => x.StartLocalTime).Must(IsTime).WithMessage("Times must be HH:mm.");
            t.RuleFor(x => x.EndLocalTime).Must(IsTime).WithMessage("Times must be HH:mm.");
            t.RuleFor(x => x).Must(x => x.StartLocalTime != x.EndLocalTime).WithMessage("Start and end must differ.");
            t.RuleFor(x => x.Action).OneOf<TimeRestrictionDocument, RuleAction>();
        });
    }

    private static bool InRange(string? text, bool inclusiveZero) =>
        MoneyFormat.TryParseStrict(text, out var amount) && (inclusiveZero ? amount >= 0 : amount > 0) && amount <= MaxAmount;

    private static bool IsTime(string? text) =>
        text is { Length: 5 } && TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
