using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Ledger;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Authorization;
using Guardrail.Domain.Common;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Purchases;

/// <summary>
/// Ends a purchase before money moved (REJECTED / CANCELLED / EXPIRED / FAILED before authorization):
/// approval closed, ledger released, cart unlocked (or abandoned if a PayPal order existed), payment voided internally.
/// Caller holds the purchase row lock and saves.
/// </summary>
public sealed class PurchaseLifecycle(IAppDbContext db, LedgerService ledger, AuditWriter audit, TimeProvider clock)
{
    public async Task CloseBeforePaymentAsync(
        Purchase purchase,
        PurchaseStatus target,
        string? invalidationReason,
        LedgerSource source,
        ActorType actor,
        CancellationToken cancellationToken,
        string? failureCode = null)
    {
        var now = clock.GetUtcNow();
        var hadPayPalOrder = purchase.Status == PurchaseStatus.CheckoutPending;

        var approval = await db.ApprovalRequests.SingleOrDefaultAsync(a => a.PurchaseId == purchase.Id, cancellationToken);
        if (approval is { IsOpen: true })
        {
            if (target == PurchaseStatus.Expired)
            {
                approval.Expire(now);
                audit.Write(AuditActions.ApprovalExpired, "ApprovalRequest", approval.Id, purchase.UserId, ActorType.System);
            }
            else if (target != PurchaseStatus.Rejected)
            {
                var reason = invalidationReason ?? InvalidationReasons.PurchaseCancelled;
                approval.Invalidate(reason, now);
                audit.Write(AuditActions.ApprovalInvalidated, "ApprovalRequest", approval.Id, purchase.UserId, actor, new { reason });
            }
        }

        purchase.TransitionTo(target, now, failureCode);

        var payments = await db.Payments.Where(p => p.PurchaseId == purchase.Id).ToListAsync(cancellationToken);
        foreach (var payment in payments.Where(p => p.IsActive && p.PaypalAuthorizationId is null))
        {
            payment.ForceStatus(PaymentStatus.Voided, now); // internal: unapproved order, no PayPal call needed
        }

        await ledger.ReleaseHeldAsync(purchase, source, null, cancellationToken);

        var cart = await db.Carts.Include(c => c.Items).SingleAsync(c => c.Id == purchase.CartId, cancellationToken);
        if (hadPayPalOrder || payments.Any(p => p.PaypalOrderId is not null))
        {
            cart.Abandon(now);
        }
        else if (cart.Status == CartStatus.Locked)
        {
            cart.Unlock(now); // BR-31: OPEN, version++
        }
    }

    /// <summary>Terminal failure after a PayPal order existed (authorize denied, capture declined, approval reversed).</summary>
    public async Task FailAfterPaymentStartedAsync(Purchase purchase, string failureCode, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (Purchase.CanTransition(purchase.Status, PurchaseStatus.Failed))
        {
            purchase.TransitionTo(PurchaseStatus.Failed, now, failureCode);
        }

        var cart = await db.Carts.SingleAsync(c => c.Id == purchase.CartId, cancellationToken);
        cart.Abandon(now);
    }
}
