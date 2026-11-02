using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Ledger;
using Guardrail.Application.Policies;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Evaluation;

public sealed record EvaluationOutcome(PolicyEvaluation Evaluation, PolicyDecision Decision, CurrentPolicy Policy, CartSnapshot Cart);

/// <summary>Gate 1: builds DB-sourced snapshots and runs the pure engine (ARCHITECTURE.md §5.2).</summary>
public sealed class EvaluationService(
    IAppDbContext db,
    PolicyLoader loader,
    SpendingQueries spending,
    AuditWriter audit,
    TimeProvider clock)
{
    public async Task<Result<PolicyDecisionDto>> EvaluateCartAsync(Guid userId, Guid cartId, ActorType actor, CancellationToken cancellationToken)
    {
        var cart = await db.Carts.AsNoTracking().Include(c => c.Items).SingleOrDefaultAsync(c => c.Id == cartId && c.UserId == userId, cancellationToken);
        if (cart is null)
        {
            return DomainError.NotFound("Cart");
        }

        if (cart.Status != CartStatus.Open)
        {
            return new DomainError(ErrorCodes.CartNotOpen, $"This cart is {EnumText.ToText(cart.Status)}; check the purchase status instead.");
        }

        var outcome = await RunAsync(cart, actor, cancellationToken);
        if (!outcome.IsSuccess)
        {
            return outcome.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return outcome.Value!.Evaluation.ToDecisionDto(outcome.Value.Policy.Version.VersionNumber);
    }

    public async Task<Result<PolicyDecisionDto>> GetAsync(Guid userId, Guid evaluationId, CancellationToken cancellationToken)
    {
        var evaluation = await db.PolicyEvaluations.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == evaluationId && e.UserId == userId, cancellationToken);
        if (evaluation is null)
        {
            return DomainError.NotFound("Evaluation");
        }

        var versionNumber = await db.PolicyVersions.Where(v => v.Id == evaluation.PolicyVersionId).Select(v => v.VersionNumber).SingleAsync(cancellationToken);
        return evaluation.ToDecisionDto(versionNumber);
    }

    /// <summary>Evaluates and adds a PolicyEvaluation to the unit of work (caller saves). Used by REST, agent tool and CreatePurchase.</summary>
    public async Task<Result<EvaluationOutcome>> RunAsync(ShoppingCart cart, ActorType actor, CancellationToken cancellationToken, bool persist = true)
    {
        var policy = await loader.LoadCurrentAsync(cart.UserId, cancellationToken);
        if (policy is null)
        {
            return new DomainError(ErrorCodes.NoActivePolicy, "You have no active spending policy.");
        }

        var now = clock.GetUtcNow();
        var snapshot = await BuildCartSnapshotAsync(cart, cancellationToken);
        var spendingSnapshot = await spending.BuildSnapshotAsync(cart.UserId, policy.Profile.TimeZone, policy.Snapshot, now, cancellationToken);
        var decision = PolicyEngine.Evaluate(policy.Snapshot, snapshot, spendingSnapshot, now);

        var evaluation = PolicyEvaluation.Record(
            cart.UserId,
            cart.Id,
            cart.Version,
            decision.CartSnapshotHash,
            snapshot.ToCanonicalJson(),
            policy.Version.Id,
            decision.Decision,
            JsonDefaults.Serialize(decision.Reasons.Select(r => r.ToDto()).ToList()),
            decision.PurchaseAmount,
            decision.Currency,
            JsonDefaults.Serialize(decision.BudgetImpacts.Select(i => i.ToDto()).ToList()),
            actor,
            now);
        if (persist)
        {
            Persist(evaluation, decision, actor);
        }

        return new EvaluationOutcome(evaluation, decision, policy, snapshot);
    }

    public void Persist(PolicyEvaluation evaluation, PolicyDecision decision, ActorType actor)
    {
        db.PolicyEvaluations.Add(evaluation);
        audit.Write(AuditActions.EvaluationPerformed, "PolicyEvaluation", evaluation.Id, evaluation.UserId, actor,
            new
            {
                cartId = evaluation.CartId,
                cartVersion = evaluation.CartVersion,
                decision = EnumText.ToText(decision.Decision),
                reasonCode = decision.PrimaryReason.Code,
            });
    }

    /// <summary>CartSnapshot with availability/tags read from the database now (never from the client or the AI).</summary>
    public async Task<CartSnapshot> BuildCartSnapshotAsync(ShoppingCart cart, CancellationToken cancellationToken)
    {
        var productIds = cart.Items.Select(i => i.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var merchantName = await db.Merchants.Where(m => m.Id == cart.MerchantId).Select(m => m.Name).SingleAsync(cancellationToken);
        return cart.ToSnapshot(merchantName, products);
    }
}
