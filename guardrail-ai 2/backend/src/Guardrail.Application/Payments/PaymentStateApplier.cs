using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Ledger;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Common;
using Guardrail.Domain.Payments;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Guardrail.Application.Payments;

/// <summary>
/// Records verified PayPal results (API responses, verified webhooks, reconciler reads) on purchase, payment and ledger.
/// Forward-only and idempotent (PAYMENT_STATE_MACHINE.md §4). Caller holds the purchase row lock and saves.
/// </summary>
public sealed class PaymentStateApplier(
    IAppDbContext db,
    LedgerService ledger,
    PurchaseLifecycle lifecycle,
    AuditWriter audit,
    TimeProvider clock,
    ILogger<PaymentStateApplier> log)
{
    public const string FailureAuthorizationDenied = "AUTHORIZATION_DENIED";
    public const string FailureAuthorizationVoided = "AUTHORIZATION_VOIDED";
    public const string FailureCaptureDeclined = "CAPTURE_DECLINED";
    public const string FailureApprovalReversed = "APPROVAL_REVERSED";
    public const string FailureOrderCreateFailed = "ORDER_CREATE_FAILED";

    /// <summary>Buyer approval verified (GET order = APPROVED, or verified CHECKOUT.ORDER.APPROVED).</summary>
    public async Task ApplyPayerApprovedAsync(
        Purchase purchase, Payment payment, string? payerId, decimal? orderAmount, LedgerSource source, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        payment.RecordPayer(payerId, now);
        payment.Advance(PaymentStatus.Approved, now);
        if (purchase.Status != PurchaseStatus.CheckoutPending)
        {
            return;
        }

        purchase.TransitionTo(PurchaseStatus.PayerApproved, now);
        if (payment.Intent == PaymentIntent.Capture)
        {
            // CAPTURE fallback: buyer approval = authorized (PAYMENT_STATE_MACHINE.md §5).
            payment.RecordAuthorization(payment.PaypalOrderId!, orderAmount, null, now);
            await ledger.ApplyAsync(purchase, LedgerTransition.Authorize, source, payment.Id, cancellationToken);
        }
    }

    public async Task ApplyAuthorizationAsync(
        Purchase purchase, Payment payment, PayPalAuthorization authorization, LedgerSource source, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        payment.RecordProviderResponse(authorization.DebugId, null, now);
        switch (authorization.Status.ToUpperInvariant())
        {
            case "CREATED":
            case "CAPTURED":
            case "PARTIALLY_CAPTURED":
                if (Payment.Rank(payment.Status) >= Payment.Rank(PaymentStatus.Authorized) && payment.PaypalAuthorizationId is not null)
                {
                    return; // already recorded
                }

                payment.RecordAuthorization(authorization.Id, authorization.Amount, authorization.ExpiresAt, now);
                payment.Advance(PaymentStatus.Authorized, now);
                if (purchase.Status is PurchaseStatus.CheckoutPending or PurchaseStatus.PayerApproved)
                {
                    purchase.TransitionTo(PurchaseStatus.PaymentAuthorized, now);
                }

                await ledger.ApplyAsync(purchase, LedgerTransition.Authorize, source, payment.Id, cancellationToken);
                audit.Write(AuditActions.PaymentAuthorized, "Payment", payment.Id, purchase.UserId, ActorOf(source),
                    new { authorizationId = authorization.Id, amount = MoneyFormat.Format(authorization.Amount) });
                break;

            case "DENIED":
            case "VOIDED":
            case "EXPIRED":
                payment.RecordAuthorization(authorization.Id, authorization.Amount, authorization.ExpiresAt, now);
                payment.Advance(PaymentStatus.Declined, now);
                await ledger.ReleaseHeldAsync(purchase, source, payment.Id, cancellationToken);
                await lifecycle.FailAfterPaymentStartedAsync(purchase, FailureAuthorizationDenied, cancellationToken);
                audit.Write(AuditActions.PaymentFailed, "Payment", payment.Id, purchase.UserId, ActorOf(source),
                    new { authorizationId = authorization.Id, status = authorization.Status });
                break;

            default:
                log.LogInformation("Authorization {AuthorizationId} for purchase {PurchaseId} is {Status}; waiting",
                    authorization.Id, purchase.Id, authorization.Status);
                break;
        }
    }

    /// <summary>Applies a capture result. Returns false when the capture was refused for an amount mismatch.</summary>
    public async Task<bool> ApplyCaptureAsync(
        Purchase purchase, Payment payment, PayPalCapture capture, LedgerSource source, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        payment.RecordProviderResponse(capture.DebugId, null, now);
        var status = capture.Status.ToUpperInvariant();

        if (status == "COMPLETED")
        {
            if (capture.Amount is { } amount && (amount != purchase.Amount || capture.Currency != purchase.Currency))
            {
                purchase.Flag(ErrorCodes.AmountMismatch, now);
                audit.Write(AuditActions.AmountMismatch, "Payment", payment.Id, purchase.UserId, ActorOf(source),
                    new { captureId = capture.Id, amount = MoneyFormat.Format(amount), capture.Currency });
                log.LogError("AMOUNT_MISMATCH on capture {CaptureId} for purchase {PurchaseId}", capture.Id, purchase.Id);
                return false;
            }

            if (payment.Status == PaymentStatus.Completed)
            {
                return true;
            }

            if (!FinalCheckPassed(payment))
            {
                purchase.Flag("UNEXPECTED_CAPTURE", now);
                audit.Write(AuditActions.UnexpectedCapture, "Payment", payment.Id, purchase.UserId, ActorType.Paypal, new { captureId = capture.Id });
                log.LogError("UNEXPECTED_CAPTURE {CaptureId} for purchase {PurchaseId} without a passed final check", capture.Id, purchase.Id);
            }

            // A COMPLETED report before AUTHORIZE was recorded writes AUTHORIZE then CAPTURE (both idempotent).
            await ledger.ApplyAsync(purchase, LedgerTransition.Authorize, source, payment.Id, cancellationToken);
            await ledger.ApplyAsync(purchase, LedgerTransition.Capture, source, payment.Id, cancellationToken);
            payment.RecordCapture(capture.Id, capture.Amount ?? purchase.Amount, now);
            payment.Advance(PaymentStatus.Completed, now);
            if (Purchase.CanTransition(purchase.Status, PurchaseStatus.Completed))
            {
                purchase.TransitionTo(PurchaseStatus.Completed, now);
            }

            var cart = await db.Carts.SingleAsync(c => c.Id == purchase.CartId, cancellationToken);
            if (cart.Status == CartStatus.Locked)
            {
                cart.CheckOut(now);
            }

            audit.Write(AuditActions.PaymentCaptured, "Payment", payment.Id, purchase.UserId, ActorOf(source),
                new { captureId = capture.Id, amount = MoneyFormat.Format(capture.Amount ?? purchase.Amount) });
            return true;
        }

        if (status == "PENDING")
        {
            payment.RecordCapture(capture.Id, null, now);
            if (payment.Advance(PaymentStatus.CapturePending, now) && Purchase.CanTransition(purchase.Status, PurchaseStatus.CapturePending))
            {
                purchase.TransitionTo(PurchaseStatus.CapturePending, now);
            }

            return true;
        }

        if (status is "DECLINED" or "FAILED")
        {
            if (payment.Status is PaymentStatus.Completed or PaymentStatus.Declined)
            {
                return true;
            }

            payment.RecordCapture(capture.Id, null, now);
            await ledger.ApplyAsync(purchase, LedgerTransition.Fail, source, payment.Id, cancellationToken);
            payment.Advance(PaymentStatus.Declined, now);
            await lifecycle.FailAfterPaymentStartedAsync(purchase, FailureCaptureDeclined, cancellationToken);
            audit.Write(AuditActions.PaymentFailed, "Payment", payment.Id, purchase.UserId, ActorOf(source), new { captureId = capture.Id, status });
            return true;
        }

        log.LogInformation("Capture {CaptureId} status {Status} not applied", capture.Id, capture.Status);
        return true;
    }

    /// <summary>Authorization voided (by us after a failed final check, by the reconciler, or verified webhook).</summary>
    public async Task ApplyVoidAsync(Purchase purchase, Payment payment, string failureCode, LedgerSource source, CancellationToken cancellationToken)
    {
        if (payment.Status == PaymentStatus.Completed)
        {
            return;
        }

        var now = clock.GetUtcNow();
        payment.ForceStatus(PaymentStatus.Voided, now);
        await ledger.ApplyAsync(purchase, LedgerTransition.Void, source, payment.Id, cancellationToken);
        await lifecycle.FailAfterPaymentStartedAsync(purchase, failureCode, cancellationToken);
        audit.Write(AuditActions.PaymentVoided, "Payment", payment.Id, purchase.UserId, ActorOf(source),
            new { authorizationId = payment.PaypalAuthorizationId, failureCode });
    }

    /// <summary>Not captured and no longer payable (e.g. buyer approval reversed): release or void whatever is held.</summary>
    public async Task FailAndReleaseAsync(Purchase purchase, Payment payment, string failureCode, LedgerSource source, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        payment.ForceStatus(PaymentStatus.Failed, now);
        await ledger.ReleaseHeldAsync(purchase, source, payment.Id, cancellationToken);
        await lifecycle.FailAfterPaymentStartedAsync(purchase, failureCode, cancellationToken);
        audit.Write(AuditActions.PaymentFailed, "Payment", payment.Id, purchase.UserId, ActorOf(source), new { failureCode });
    }

    public async Task ApplyRefundAsync(
        Purchase purchase, Payment payment, string refundId, decimal amount, LedgerSource source, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (payment.Status is not (PaymentStatus.Completed or PaymentStatus.PartiallyRefunded))
        {
            return;
        }

        var allocation = await ledger.AllocationAsync(purchase, cancellationToken);
        var before = db.LedgerEntries.Local.Count;
        await ledger.ApplyAsync(purchase, LedgerTransition.Refund, source, payment.Id, cancellationToken, refundId,
            LedgerService.ProRata(allocation, amount));
        if (db.LedgerEntries.Local.Count == before)
        {
            return; // replay of a refund already recorded
        }

        var refunded = payment.RefundedAmount + amount;
        payment.RecordRefund(refunded, now);
        var full = refunded >= purchase.Amount;
        payment.Advance(full ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded, now);
        var target = full ? PurchaseStatus.Refunded : PurchaseStatus.PartiallyRefunded;
        if (Purchase.CanTransition(purchase.Status, target))
        {
            purchase.TransitionTo(target, now);
        }

        audit.Write(AuditActions.PaymentRefunded, "Payment", payment.Id, purchase.UserId, ActorType.Paypal,
            new { refundId, amount = MoneyFormat.Format(amount) });
    }

    public static bool FinalCheckPassed(Payment payment) =>
        payment.FinalCheckResult is { } json && JsonDefaults.Deserialize<Dtos.FinalCheckDto>(json).Passed;

    private static ActorType ActorOf(LedgerSource source) => source switch
    {
        LedgerSource.PaypalApi or LedgerSource.PaypalWebhook => ActorType.Paypal,
        LedgerSource.User => ActorType.User,
        _ => ActorType.System,
    };
}
