using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Evaluation;
using Guardrail.Application.Policies;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Common;
using Guardrail.Domain.Payments;
using Guardrail.Domain.Policy;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Guardrail.Application.Payments;

/// <summary>
/// verify order → authorize → final guardrail check → capture (or void) (PAYPAL_INTEGRATION.md §6, BR-62/63/67).
/// Each step is idempotent and resumes from the purchase's current status. No DB transaction spans a PayPal call.
/// Used by POST /purchases/{id}/capture (user trigger) and by the reconciler (system).
/// </summary>
public sealed class PaymentCompletionService(
    IAppDbContext db,
    IPayPalClient payPal,
    PaymentStateApplier applier,
    EvaluationService evaluations,
    PolicyLoader policies,
    PurchaseQueries queries,
    AuditWriter audit,
    TimeProvider clock,
    ILogger<PaymentCompletionService> log)
{
    private const int MaxSteps = 6;

    private static readonly DomainError InProgress = new(ErrorCodes.CaptureInProgress, "Payment is being confirmed with PayPal. Check back shortly.");

    public async Task<Result<PurchaseDto>> CompleteAsync(Guid purchaseId, Guid? userId, LedgerSource source, CancellationToken cancellationToken)
    {
        for (var step = 0; step < MaxSteps; step++)
        {
            db.ClearTracking();
            var purchase = await db.Purchases.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == purchaseId && (userId == null || p.UserId == userId), cancellationToken);
            if (purchase is null)
            {
                return DomainError.NotFound("Purchase");
            }

            var payment = await db.Payments.AsNoTracking()
                .Where(p => p.PurchaseId == purchaseId)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(p => p.Status != PaymentStatus.Failed || p.PaypalOrderId != null, cancellationToken);

            DomainError? error;
            switch (purchase.Status)
            {
                case PurchaseStatus.CheckoutPending:
                    error = await VerifyBuyerApprovalAsync(purchase, payment, source, cancellationToken);
                    break;
                case PurchaseStatus.PayerApproved when payment!.Intent == PaymentIntent.Authorize:
                    error = await AuthorizeAsync(purchase, payment, source, cancellationToken);
                    break;
                case PurchaseStatus.PayerApproved:
                case PurchaseStatus.PaymentAuthorized:
                    error = await FinalCheckThenCaptureAsync(purchase, payment!, source, cancellationToken);
                    break;
                case PurchaseStatus.CapturePending:
                    await RefreshPendingCaptureAsync(purchase, payment!, source, cancellationToken);
                    return await DtoAsync(purchase, cancellationToken);
                case PurchaseStatus.AwaitingApproval:
                case PurchaseStatus.ReadyForCheckout:
                    return new DomainError(ErrorCodes.PaymentNotApproved, "Checkout has not started for this purchase.");
                default:
                    return await DtoAsync(purchase, cancellationToken); // COMPLETED or terminal: current state
            }

            if (error is not null)
            {
                return error;
            }
        }

        return InProgress;
    }

    // ------------------------------------------------------------------ Gate 6: verify buyer approval (BR-62)

    private async Task<DomainError?> VerifyBuyerApprovalAsync(Purchase purchase, Payment? payment, LedgerSource source, CancellationToken cancellationToken)
    {
        if (payment?.PaypalOrderId is null)
        {
            return new DomainError(ErrorCodes.PaymentNotApproved, "No PayPal order exists for this purchase yet.");
        }

        PayPalOrder order;
        try
        {
            order = await payPal.GetOrderAsync(payment.PaypalOrderId, cancellationToken);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            log.LogWarning(ex, "GET order failed for purchase {PurchaseId}", purchase.Id);
            return InProgress;
        }

        var status = order.Status.ToUpperInvariant();
        if (status is not ("APPROVED" or "COMPLETED"))
        {
            return new DomainError(ErrorCodes.PaymentNotApproved, "The buyer has not approved this PayPal order yet.",
                new Dictionary<string, object?> { ["orderStatus"] = order.Status, ["approveUrl"] = payment.ApproveUrl });
        }

        if (order.Amount != purchase.Amount || order.Currency != purchase.Currency || order.CustomId != purchase.Id.ToString())
        {
            return await AmountMismatchAsync(purchase, payment, order.Amount, order.Currency, cancellationToken);
        }

        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var locked = await db.LockPurchaseAsync(purchase.Id, null, cancellationToken);
        var tracked = await db.Payments.SingleAsync(p => p.Id == payment.Id, cancellationToken);
        if (locked!.Status == PurchaseStatus.CheckoutPending)
        {
            await applier.ApplyPayerApprovedAsync(locked, tracked, order.PayerId, order.Amount, source, cancellationToken);
        }

        // The order may already carry an authorization (e.g. retried after a lost response).
        if (order.Authorization is { } authorization && tracked.Intent == PaymentIntent.Authorize)
        {
            await applier.ApplyAuthorizationAsync(locked, tracked, authorization, source, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return null;
    }

    // ------------------------------------------------------------------ authorize (ADR-005)

    private async Task<DomainError?> AuthorizeAsync(Purchase purchase, Payment payment, LedgerSource source, CancellationToken cancellationToken)
    {
        PayPalOrder order;
        try
        {
            order = await payPal.AuthorizeOrderAsync(payment.PaypalOrderId!, payment.PaypalAuthorizeRequestId!.Value, cancellationToken);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            log.LogWarning(ex, "Authorize failed transiently for purchase {PurchaseId}", purchase.Id);
            return InProgress;
        }
        catch (PayPalApiException ex) when (ex.HasIssue("INSTRUMENT_DECLINED"))
        {
            return new DomainError(ErrorCodes.PaymentInstrumentDeclined, "PayPal declined that funding source. Choose another one in PayPal.",
                new Dictionary<string, object?> { ["retryUrl"] = payment.ApproveUrl });
        }
        catch (PayPalApiException ex) when (ex.HasIssue("ORDER_ALREADY_AUTHORIZED"))
        {
            order = await payPal.GetOrderAsync(payment.PaypalOrderId!, cancellationToken);
        }
        catch (PayPalApiException ex)
        {
            log.LogError("Authorize rejected for purchase {PurchaseId}: {Name} debug_id={DebugId}", purchase.Id, ex.Name, ex.DebugId);
            order = new PayPalOrder(payment.PaypalOrderId!, "DENIED", null, null, null, null, null,
                new PayPalAuthorization(payment.PaypalOrderId!, "DENIED", null, null, null, ex.DebugId), null, ex.DebugId);
        }

        if (order.Authorization is not { } authorization)
        {
            log.LogError("Authorize response for purchase {PurchaseId} had no authorization; reconciler will retry", purchase.Id);
            return InProgress;
        }

        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var locked = await db.LockPurchaseAsync(purchase.Id, null, cancellationToken);
        var tracked = await db.Payments.SingleAsync(p => p.Id == payment.Id, cancellationToken);
        if (locked!.Status == PurchaseStatus.PayerApproved)
        {
            await applier.ApplyAuthorizationAsync(locked, tracked, authorization, source, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return authorization.Status.Equals("PENDING", StringComparison.OrdinalIgnoreCase) ? InProgress : null;
    }

    // ------------------------------------------------------------------ Gate 7 + 8: final check, capture or void

    private async Task<DomainError?> FinalCheckThenCaptureAsync(Purchase purchase, Payment payment, LedgerSource source, CancellationToken cancellationToken)
    {
        var check = await RunFinalCheckAsync(purchase, payment, cancellationToken);
        if (!check.Passed)
        {
            return await VoidAfterFailedCheckAsync(purchase, payment, check, source, cancellationToken);
        }

        PayPalCapture? capture;
        try
        {
            capture = payment.Intent == PaymentIntent.Authorize
                ? await payPal.CaptureAuthorizationAsync(payment.PaypalAuthorizationId!, payment.PaypalCaptureRequestId!.Value, cancellationToken)
                : (await payPal.CaptureOrderAsync(payment.PaypalOrderId!, payment.PaypalCaptureRequestId!.Value, cancellationToken)).Capture;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            log.LogWarning(ex, "Capture failed transiently for purchase {PurchaseId}; same PayPal-Request-Id will be reused", purchase.Id);
            return InProgress;
        }
        catch (PayPalApiException ex) when (ex.HasIssue("AUTHORIZATION_ALREADY_CAPTURED") || ex.HasIssue("ORDER_ALREADY_CAPTURED"))
        {
            capture = (await payPal.GetOrderAsync(payment.PaypalOrderId!, cancellationToken)).Capture;
        }
        catch (PayPalApiException ex) when (ex.HasIssue("AUTHORIZATION_EXPIRED") || ex.HasIssue("AUTHORIZATION_VOIDED"))
        {
            log.LogError("Authorization unusable for purchase {PurchaseId}: {Name} debug_id={DebugId}", purchase.Id, ex.Name, ex.DebugId);
            await WithLockAsync(purchase, payment, (p, pay) =>
                applier.ApplyVoidAsync(p, pay, PaymentStateApplier.FailureAuthorizationVoided, source, cancellationToken), cancellationToken);
            return null;
        }
        catch (PayPalApiException ex)
        {
            log.LogError("Capture rejected for purchase {PurchaseId}: {Name} debug_id={DebugId}", purchase.Id, ex.Name, ex.DebugId);
            capture = new PayPalCapture(string.Empty, "DECLINED", null, null, ex.DebugId);
        }

        if (capture is null)
        {
            log.LogError("Capture response for purchase {PurchaseId} had no capture; reconciler will retry", purchase.Id);
            return InProgress;
        }

        var accepted = true;
        await WithLockAsync(purchase, payment, async (p, pay) =>
        {
            if (p.Status is PurchaseStatus.PaymentAuthorized or PurchaseStatus.PayerApproved or PurchaseStatus.CapturePending)
            {
                accepted = await applier.ApplyCaptureAsync(p, pay, capture, source, cancellationToken);
            }
        }, cancellationToken);

        if (!accepted)
        {
            return new DomainError(ErrorCodes.AmountMismatch, "PayPal reported a different amount; the payment was flagged for review.");
        }

        if (capture.Status.Is("DECLINED") || capture.Status.Is("FAILED"))
        {
            await TryVoidAsync(payment, cancellationToken); // best effort; reconciler retries
        }

        return null; // the next loop iteration returns COMPLETED / CAPTURE_PENDING / FAILED state
    }

    /// <summary>Pure check over DB state under the purchase lock; result stored in payments.final_check_result (invariant 5).</summary>
    private async Task<FinalCheckResult> RunFinalCheckAsync(Purchase purchase, Payment payment, CancellationToken cancellationToken)
    {
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var locked = await db.LockPurchaseAsync(purchase.Id, null, cancellationToken);
        var tracked = await db.Payments.SingleAsync(p => p.Id == payment.Id, cancellationToken);
        var cart = await db.Carts.AsNoTracking().Include(c => c.Items).SingleAsync(c => c.Id == locked!.CartId, cancellationToken);
        var snapshot = await evaluations.BuildCartSnapshotAsync(cart, cancellationToken);
        var current = await policies.LoadCurrentAsync(locked!.UserId, cancellationToken);
        var now = clock.GetUtcNow();

        var result = current is null
            ? new FinalCheckResult(false, [new FinalCheckReason(ErrorCodes.NoActivePolicy, "No active policy")], now)
            : FinalGuardrailCheck.Run(current.Snapshot, snapshot, locked.CartSnapshotHash, locked.Amount, locked.Currency,
                tracked.AuthorizedAmount, tracked.AuthorizedAmount is null ? null : tracked.Currency, now);

        var dto = new FinalCheckDto(result.Passed, result.Reasons.Select(r => new FinalCheckReasonDto(r.Code, r.Message)).ToList(), result.CheckedAt);
        tracked.RecordFinalCheck(JsonDefaults.Serialize(dto), now);
        audit.Write(result.Passed ? AuditActions.FinalCheckPassed : AuditActions.FinalCheckFailed, "Payment", tracked.Id, locked.UserId,
            ActorType.System, dto);
        if (result.Reasons.Any(r => r.Code == ErrorCodes.AmountMismatch))
        {
            audit.Write(AuditActions.AmountMismatch, "Payment", tracked.Id, locked.UserId, ActorType.System,
                new { authorized = MoneyFormat.Format(tracked.AuthorizedAmount), expected = MoneyFormat.Format(locked.Amount) });
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<DomainError?> VoidAfterFailedCheckAsync(
        Purchase purchase, Payment payment, FinalCheckResult check, LedgerSource source, CancellationToken cancellationToken)
    {
        if (payment.Intent == PaymentIntent.Authorize)
        {
            try
            {
                await payPal.VoidAuthorizationAsync(payment.PaypalAuthorizationId!, payment.PaypalVoidRequestId!.Value, cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                log.LogWarning(ex, "Void failed transiently for purchase {PurchaseId}; will retry", purchase.Id);
                return InProgress;
            }
            catch (PayPalApiException ex) when (ex.HasIssue("AUTHORIZATION_ALREADY_VOIDED") || ex.HasIssue("AUTHORIZATION_VOIDED"))
            {
                // already voided
            }
        }

        // CAPTURE fallback: the order is simply never captured (PAYMENT_STATE_MACHINE.md §5).
        await WithLockAsync(purchase, payment, (p, pay) =>
            applier.ApplyVoidAsync(p, pay, ErrorCodes.FinalCheckFailed, source, cancellationToken), cancellationToken);

        return new DomainError(ErrorCodes.FinalCheckFailed, "Payment stopped by your rules — the authorization was voided and you were not charged.",
            new Dictionary<string, object?> { ["reasons"] = check.Reasons.Select(r => new FinalCheckReasonDto(r.Code, r.Message)).ToList() });
    }

    private async Task RefreshPendingCaptureAsync(Purchase purchase, Payment payment, LedgerSource source, CancellationToken cancellationToken)
    {
        if (payment.PaypalCaptureId is not { Length: > 0 } captureId)
        {
            return;
        }

        try
        {
            var capture = await payPal.GetCaptureAsync(captureId, cancellationToken);
            await WithLockAsync(purchase, payment, async (p, pay) => await applier.ApplyCaptureAsync(p, pay, capture, source, cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (IsTransient(ex) || ex is PayPalApiException)
        {
            log.LogWarning(ex, "Could not refresh pending capture {CaptureId}", captureId);
        }
    }

    /// <summary>BR-68: authorizations are never left open; the reconciler voids ones older than the allowed window.</summary>
    public async Task VoidStaleAuthorizationAsync(Guid purchaseId, CancellationToken cancellationToken)
    {
        db.ClearTracking();
        var payment = await db.Payments.AsNoTracking()
            .Where(p => p.PurchaseId == purchaseId && p.Status == PaymentStatus.Authorized)
            .FirstOrDefaultAsync(cancellationToken);
        var purchase = await db.Purchases.AsNoTracking().SingleAsync(p => p.Id == purchaseId, cancellationToken);
        if (payment is null || purchase.Status != PurchaseStatus.PaymentAuthorized)
        {
            return;
        }

        if (await TryVoidAsync(payment, cancellationToken))
        {
            await WithLockAsync(purchase, payment, (p, pay) =>
                applier.ApplyVoidAsync(p, pay, "AUTHORIZATION_STALE", LedgerSource.Reconciler, cancellationToken), cancellationToken);
        }
    }

    /// <summary>Voids a still-open authorization (best effort). Used after a declined capture and by the reconciler.</summary>
    public async Task<bool> TryVoidAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (payment.Intent != PaymentIntent.Authorize || payment.PaypalAuthorizationId is null || payment.PaypalVoidRequestId is null)
        {
            return false;
        }

        try
        {
            await payPal.VoidAuthorizationAsync(payment.PaypalAuthorizationId, payment.PaypalVoidRequestId.Value, cancellationToken);
            return true;
        }
        catch (Exception ex) when (IsTransient(ex) || ex is PayPalApiException)
        {
            log.LogWarning(ex, "Best-effort void failed for authorization {AuthorizationId}", payment.PaypalAuthorizationId);
            return false;
        }
    }

    private async Task<DomainError> AmountMismatchAsync(
        Purchase purchase, Payment payment, decimal? amount, string? currency, CancellationToken cancellationToken)
    {
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var locked = await db.LockPurchaseAsync(purchase.Id, null, cancellationToken);
        locked!.Flag(ErrorCodes.AmountMismatch, clock.GetUtcNow());
        audit.Write(AuditActions.AmountMismatch, "Payment", payment.Id, purchase.UserId, ActorType.Paypal,
            new { reported = MoneyFormat.Format(amount), currency, expected = MoneyFormat.Format(purchase.Amount) });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        log.LogError("AMOUNT_MISMATCH for purchase {PurchaseId}: PayPal {Amount} {Currency}", purchase.Id, amount, currency);
        return new DomainError(ErrorCodes.AmountMismatch, "PayPal reported a different amount; the payment was flagged for review.");
    }

    private async Task WithLockAsync(Purchase purchase, Payment payment, Func<Purchase, Payment, Task> apply, CancellationToken cancellationToken)
    {
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var locked = await db.LockPurchaseAsync(purchase.Id, null, cancellationToken);
        var tracked = await db.Payments.SingleAsync(p => p.Id == payment.Id, cancellationToken);
        await apply(locked!, tracked);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task<Result<PurchaseDto>> DtoAsync(Purchase purchase, CancellationToken cancellationToken) =>
        await queries.GetAsync(purchase.UserId, purchase.Id, cancellationToken);

    private static bool IsTransient(Exception ex) => ex is PayPalUnavailableException || ex is PayPalApiException { IsTransient: true };
}

internal static class StatusText
{
    public static bool Is(this string status, string expected) => string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);
}
