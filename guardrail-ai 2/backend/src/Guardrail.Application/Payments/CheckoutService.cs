using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Evaluation;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Authorization;
using Guardrail.Domain.Common;
using Guardrail.Domain.Payments;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Guardrail.Application.Payments;

/// <summary>
/// Gate 4 + PayPal order creation (PURCHASE_AUTHORIZATION.md §3). Two DB transactions around one PayPal call:
/// a DB transaction is never held across network I/O. Only reachable from a user REST endpoint (BR-80).
/// </summary>
public sealed class CheckoutService(
    IAppDbContext db,
    IPayPalClient payPal,
    EvaluationService evaluations,
    PurchaseLifecycle lifecycle,
    AuditWriter audit,
    IOptions<CheckoutOptions> options,
    TimeProvider clock,
    ILogger<CheckoutService> log)
{
    public async Task<Result<CheckoutResponse>> CheckoutAsync(Guid userId, Guid purchaseId, CancellationToken cancellationToken)
    {
        var prepared = await AuthorizeCheckoutAsync(userId, purchaseId, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        if (prepared.Value!.Existing is { } existing)
        {
            return existing;
        }

        return await CreateOrderAsync(userId, purchaseId, prepared.Value.PaymentId!.Value, cancellationToken);
    }

    private sealed record Prepared(CheckoutResponse? Existing, Guid? PaymentId);

    /// <summary>Tx 1: verify the purchase, consume the approval, insert the payment row (invariant 4).</summary>
    private async Task<Result<Prepared>> AuthorizeCheckoutAsync(Guid userId, Guid purchaseId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var settings = options.Value;
        await using var tx = await db.BeginTransactionAsync(cancellationToken);

        var purchase = await db.LockPurchaseAsync(purchaseId, userId, cancellationToken);
        if (purchase is null)
        {
            return DomainError.NotFound("Purchase");
        }

        var payments = await db.Payments.Where(p => p.PurchaseId == purchase.Id).ToListAsync(cancellationToken);
        var active = payments.FirstOrDefault(p => p.IsActive);

        if (purchase.Status == PurchaseStatus.CheckoutPending)
        {
            // Idempotent re-checkout: same PayPal order, no second approval consumption.
            if (active?.ApproveUrl is not null)
            {
                return new Prepared(new CheckoutResponse(purchase.Id, purchase.Status, active.PaypalOrderId, active.ApproveUrl), null);
            }

            if (active is { Status: PaymentStatus.Initiated })
            {
                return new Prepared(null, active.Id); // retry create with the same PayPal-Request-Id
            }

            if (active is not null)
            {
                return new Prepared(new CheckoutResponse(purchase.Id, purchase.Status, active.PaypalOrderId, active.ApproveUrl), null);
            }

            // Previous create failed with 4xx: new payment row, new request id; the approval is already bound to this purchase.
            var retry = Payment.Initiate(purchase, Intent, now);
            db.Payments.Add(retry);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new Prepared(null, retry.Id);
        }

        if (purchase.Status != PurchaseStatus.ReadyForCheckout)
        {
            return DomainError.InvalidState($"A {EnumText.ToText(purchase.Status)} purchase cannot be checked out.");
        }

        // Recompute the cart hash from the locked cart.
        var cart = await db.Carts.Include(c => c.Items).SingleAsync(c => c.Id == purchase.CartId, cancellationToken);
        var snapshot = await evaluations.BuildCartSnapshotAsync(cart, cancellationToken);
        if (snapshot.ComputeHash() != purchase.CartSnapshotHash)
        {
            return new DomainError(ErrorCodes.CartChanged, "The cart no longer matches this purchase.");
        }

        if (snapshot.Items.Any(i => !i.IsAvailable || i.Quantity > i.StockQuantity))
        {
            await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Cancelled, InvalidationReasons.ProductUnavailable,
                LedgerSource.User, ActorType.User, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new DomainError(ErrorCodes.ProductUnavailable, "A product in this purchase is no longer available.");
        }

        var approval = await db.ApprovalRequests.AsNoTracking().SingleOrDefaultAsync(a => a.PurchaseId == purchase.Id, cancellationToken);
        if (approval is not null)
        {
            // Atomic single-use consume (BR-53).
            var consumed = await db.TryConsumeApprovalAsync(approval.Id, userId, snapshot.ComputeHash(), purchase.Amount, now, cancellationToken);
            if (!consumed)
            {
                var current = await db.ApprovalRequests.AsNoTracking().SingleAsync(a => a.Id == approval.Id, cancellationToken);
                if (current.Status == ApprovalStatus.Expired || (current.IsOpen && current.IsExpiredAt(now)))
                {
                    await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Expired, null, LedgerSource.User, ActorType.User, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return new DomainError(ErrorCodes.ApprovalExpired, "The approval for this purchase expired.");
                }

                return current.Status == ApprovalStatus.Invalidated
                    ? new DomainError(ErrorCodes.ApprovalInvalidated, "The approval is no longer valid.",
                        new Dictionary<string, object?> { ["reason"] = current.InvalidatedReason })
                    : new DomainError(ErrorCodes.ApprovalNotApproved, "This purchase has not been approved.");
            }

            audit.Write(AuditActions.ApprovalConsumed, "ApprovalRequest", approval.Id, userId, ActorType.User, new { purchaseId = purchase.Id });
        }
        else
        {
            var currentVersion = await db.SpendingProfiles.Where(p => p.UserId == userId).Select(p => p.CurrentPolicyVersionId).SingleAsync(cancellationToken);
            if (purchase.CheckoutExpiresAt is not { } expiresAt || now >= expiresAt || purchase.PolicyVersionId != currentVersion)
            {
                await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Expired, null, LedgerSource.User, ActorType.User, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return new DomainError(ErrorCodes.EvaluationStale, "This purchase expired or your rules changed. Evaluate the cart again.");
            }
        }

        var payment = Payment.Initiate(purchase, Intent, now);
        db.Payments.Add(payment);
        purchase.StartCheckout(now, TimeSpan.FromHours(settings.PayPalApprovalTimeoutHours));
        audit.Write(AuditActions.CheckoutStarted, "Purchase", purchase.Id, userId, ActorType.User, new { paymentId = payment.Id });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new Prepared(null, payment.Id);
    }

    /// <summary>PayPal call + Tx 2 (record).</summary>
    private async Task<Result<CheckoutResponse>> CreateOrderAsync(Guid userId, Guid purchaseId, Guid paymentId, CancellationToken cancellationToken)
    {
        db.ClearTracking();
        var purchase = await db.Purchases.AsNoTracking().SingleAsync(p => p.Id == purchaseId, cancellationToken);
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId, cancellationToken);
        var request = await BuildOrderRequestAsync(purchase, payment, cancellationToken);

        PayPalOrder order;
        try
        {
            order = await payPal.CreateOrderAsync(request, payment.PaypalCreateRequestId, cancellationToken);
        }
        catch (Exception ex) when (ex is PayPalUnavailableException || (ex is PayPalApiException { IsTransient: true }))
        {
            log.LogWarning(ex, "PayPal unavailable creating order for purchase {PurchaseId}", purchaseId);
            return Unavailable();
        }
        catch (PayPalApiException ex)
        {
            log.LogError("PayPal rejected create order for purchase {PurchaseId}: {Name} debug_id={DebugId}", purchaseId, ex.Name, ex.DebugId);
            await RecordCreateFailureAsync(purchaseId, paymentId, ex.DebugId, cancellationToken);
            return Unavailable();
        }

        // Tx 2
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var lockedPurchase = await db.LockPurchaseAsync(purchaseId, userId, cancellationToken);
        var tracked = await db.Payments.SingleAsync(p => p.Id == paymentId, cancellationToken);
        var status = string.Equals(order.Status, "PAYER_ACTION_REQUIRED", StringComparison.OrdinalIgnoreCase)
            ? PaymentStatus.PayerActionRequired
            : PaymentStatus.Created;
        tracked.RecordOrderCreated(order.Id, order.ApproveUrl, status, order.DebugId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new CheckoutResponse(purchaseId, lockedPurchase!.Status, order.Id, order.ApproveUrl);
    }

    private async Task RecordCreateFailureAsync(Guid purchaseId, Guid paymentId, string? debugId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var purchase = await db.LockPurchaseAsync(purchaseId, null, cancellationToken);
        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId, cancellationToken);
        payment.RecordProviderResponse(debugId, null, now);
        payment.ForceStatus(PaymentStatus.Failed, now);

        var failures = await db.Payments.CountAsync(p => p.PurchaseId == purchaseId && p.Status == PaymentStatus.Failed && p.PaypalOrderId == null,
            cancellationToken) + 1;
        if (purchase is not null && failures >= options.Value.MaxFailedOrderCreates)
        {
            await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Failed, null, LedgerSource.System, ActorType.System, cancellationToken,
                PaymentStateApplier.FailureOrderCreateFailed);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>Amounts come from the purchase's frozen cart snapshot (BR-61), never from a request.</summary>
    private async Task<CreateOrderRequest> BuildOrderRequestAsync(Purchase purchase, Payment payment, CancellationToken cancellationToken)
    {
        var snapshotJson = await db.PolicyEvaluations.Where(e => e.Id == purchase.PolicyEvaluationId).Select(e => e.CartSnapshotJson)
            .SingleAsync(cancellationToken);
        var productIds = PurchaseQueries.SnapshotItems(snapshotJson, new Dictionary<Guid, string>()).Select(i => i.ProductId).ToList();
        var names = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var items = PurchaseQueries.SnapshotItems(snapshotJson, names)
            .Select(i => new PayPalLineItem(Truncate(i.Name, 127), i.Sku, i.Quantity, PurchaseQueries.ParseMoney(i.UnitPrice)))
            .ToList();

        // item_total must equal Σ unit_amount × quantity (PAYPAL_INTEGRATION.md §4).
        var itemTotal = items.Sum(i => i.UnitAmount * i.Quantity);
        if (itemTotal != purchase.Amount)
        {
            throw new InvalidOperationException($"Item total {itemTotal} does not equal purchase amount {purchase.Amount}.");
        }

        var merchantName = await db.Merchants.Where(m => m.Id == purchase.MerchantId).Select(m => m.Name).SingleAsync(cancellationToken);
        var settings = options.Value;
        return new CreateOrderRequest(
            purchase.Id,
            payment.Intent == PaymentIntent.Authorize ? "AUTHORIZE" : "CAPTURE",
            purchase.Amount,
            purchase.Currency,
            Truncate($"Guardrail AI purchase — {merchantName}", 127),
            items,
            $"{settings.ReturnUrl}?purchaseId={purchase.Id}",
            $"{settings.CancelUrl}?purchaseId={purchase.Id}",
            settings.BrandName);
    }

    private PaymentIntent Intent =>
        string.Equals(options.Value.Intent, "CAPTURE", StringComparison.OrdinalIgnoreCase) ? PaymentIntent.Capture : PaymentIntent.Authorize;

    private static DomainError Unavailable() =>
        new(ErrorCodes.PaymentProviderUnavailable, "PayPal is unavailable right now. It is safe to retry.");

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
