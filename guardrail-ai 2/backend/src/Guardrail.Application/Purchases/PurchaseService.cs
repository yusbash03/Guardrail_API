using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Evaluation;
using Guardrail.Application.Ledger;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Authorization;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Guardrail.Application.Purchases;

public sealed record CreatePurchaseCommand(Guid CartId, Guid EvaluationId);

public sealed record CreatedPurchase(PurchaseDto Purchase, Guid? ApprovalRequestId, DateTimeOffset? ApprovalExpiresAt);

/// <summary>Gate 2 (PURCHASE_AUTHORIZATION.md §2) and cancellation (§5). Shared by REST and the agent tool (BR-81).</summary>
public sealed class PurchaseService(
    IAppDbContext db,
    EvaluationService evaluations,
    LedgerService ledger,
    PurchaseLifecycle lifecycle,
    PurchaseQueries queries,
    AuditWriter audit,
    IOptions<EvaluationOptions> evaluationOptions,
    IOptions<ApprovalOptions> approvalOptions,
    IOptions<CheckoutOptions> checkoutOptions,
    TimeProvider clock)
{
    public async Task<Result<CreatedPurchase>> CreateAsync(Guid userId, CreatePurchaseCommand command, ActorType actor, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.BeginTransactionAsync(cancellationToken);

        // 1. Per-user serialization of budget-increasing writes (SEC-41).
        var profile = await db.LockProfileAsync(userId, cancellationToken);
        if (profile?.CurrentPolicyVersionId is null)
        {
            return new DomainError(ErrorCodes.NoActivePolicy, "You have no active spending policy.");
        }

        // 2. Cart and referenced evaluation.
        var cart = await db.LockCartAsync(command.CartId, userId, cancellationToken);
        if (cart is null)
        {
            return DomainError.NotFound("Cart");
        }

        if (cart.Status != CartStatus.Open)
        {
            return new DomainError(ErrorCodes.CartNotOpen, $"This cart is {EnumText.ToText(cart.Status)}.");
        }

        var evaluation = await db.PolicyEvaluations.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == command.EvaluationId && e.UserId == userId, cancellationToken);
        if (evaluation is null || evaluation.CartId != cart.Id)
        {
            return DomainError.NotFound("Evaluation");
        }

        if (evaluation.Decision == PolicyDecisionType.Blocked)
        {
            return Blocked(evaluation.Id, DtoMapper.ReasonsOf(evaluation.Reasons)); // BR-42
        }

        if (evaluation.CartVersion != cart.Version || evaluation.PolicyVersionId != profile.CurrentPolicyVersionId)
        {
            return new DomainError(ErrorCodes.EvaluationStale, "The cart or your rules changed since this evaluation. Evaluate again.");
        }

        if (now - evaluation.EvaluatedAt >= TimeSpan.FromMinutes(evaluationOptions.Value.MaxAgeMinutes))
        {
            return new DomainError(ErrorCodes.EvaluationExpired, "This evaluation is too old. Evaluate the cart again.");
        }

        // 3. Re-evaluate with a fresh spending snapshot inside the lock (BR-41).
        var fresh = await evaluations.RunAsync(cart, actor, cancellationToken, persist: false);
        if (!fresh.IsSuccess)
        {
            return fresh.Error!;
        }

        var outcome = fresh.Value!;
        if (outcome.Decision.Decision == PolicyDecisionType.Blocked
            || outcome.Decision.Decision != evaluation.Decision
            || outcome.Decision.CartSnapshotHash != evaluation.CartSnapshotHash)
        {
            evaluations.Persist(outcome.Evaluation, outcome.Decision, actor);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return outcome.Decision.Decision == PolicyDecisionType.Blocked
                ? Blocked(outcome.Evaluation.Id, outcome.Decision.Reasons.Select(r => r.ToDto()).ToList())
                : new DomainError(ErrorCodes.EvaluationStale, "Your budget or rules changed. Review the new evaluation.",
                    new Dictionary<string, object?> { ["newEvaluationId"] = outcome.Evaluation.Id });
        }

        // 4–5. Lock the cart, create the purchase (+ approval), reserve budget.
        cart.Lock(now);
        var (weekStart, monthStart) = BudgetPeriods.PeriodStarts(now, profile.TimeZone); // BR-71
        var purchase = Purchase.Create(cart, evaluation, weekStart, monthStart, now,
            TimeSpan.FromMinutes(checkoutOptions.Value.AllowedPurchaseCheckoutMinutes));
        db.Purchases.Add(purchase);

        ApprovalRequest? approval = null;
        if (purchase.Status == PurchaseStatus.AwaitingApproval)
        {
            approval = ApprovalRequest.CreateFor(purchase, evaluation, actor, now, TimeSpan.FromMinutes(approvalOptions.Value.TtlMinutes));
            db.ApprovalRequests.Add(approval);
            await ledger.ApplyAsync(purchase, LedgerTransition.Propose, LedgerSource.System, null, cancellationToken);
        }
        else
        {
            await ledger.ApplyAsync(purchase, LedgerTransition.Reserve, LedgerSource.System, null, cancellationToken);
        }

        // 6. Audit.
        audit.Write(AuditActions.CartLocked, "ShoppingCart", cart.Id, userId, actor, new { cartVersion = cart.Version });
        audit.Write(AuditActions.PurchaseCreated, "Purchase", purchase.Id, userId, actor,
            new { amount = MoneyFormat.Format(purchase.Amount), purchase.Currency, status = EnumText.ToText(purchase.Status) });
        if (approval is not null)
        {
            audit.Write(AuditActions.ApprovalRequested, "ApprovalRequest", approval.Id, userId, actor, new { purchaseId = purchase.Id });
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var dto = await queries.GetAsync(userId, purchase.Id, cancellationToken);
        return new CreatedPurchase(dto.Value!, approval?.Id, approval?.ExpiresAt);
    }

    /// <summary>Allowed before payer approval (PURCHASE_AUTHORIZATION.md §5).</summary>
    public async Task<Result<PurchaseDto>> CancelAsync(Guid userId, Guid purchaseId, CancellationToken cancellationToken)
    {
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var purchase = await db.LockPurchaseAsync(purchaseId, userId, cancellationToken);
        if (purchase is null)
        {
            return DomainError.NotFound("Purchase");
        }

        if (purchase.Status is not (PurchaseStatus.AwaitingApproval or PurchaseStatus.ReadyForCheckout or PurchaseStatus.CheckoutPending))
        {
            return DomainError.InvalidState($"A {EnumText.ToText(purchase.Status)} purchase cannot be cancelled.");
        }

        await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Cancelled, InvalidationReasons.PurchaseCancelled,
            LedgerSource.User, ActorType.User, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return await queries.GetAsync(userId, purchaseId, cancellationToken);
    }

    private static DomainError Blocked(Guid evaluationId, IReadOnlyList<PolicyReasonDto> reasons) =>
        new(ErrorCodes.EvaluationBlocked, "Your spending rules block this purchase.",
            new Dictionary<string, object?>
            {
                ["evaluationId"] = evaluationId,
                ["reasons"] = reasons.Where(r => r.Severity == ReasonSeverity.Block).ToList(),
            });
}
