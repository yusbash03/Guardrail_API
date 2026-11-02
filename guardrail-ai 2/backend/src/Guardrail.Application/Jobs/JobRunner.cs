using Guardrail.Application.Abstractions;
using Guardrail.Application.Payments;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Common;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Guardrail.Application.Jobs;

public sealed record JobRunSummary(int ExpiredApprovals, int ExpiredCheckouts, int WebhooksProcessed, int Reconciled);

/// <summary>
/// Background work (ARCHITECTURE.md §6). Run by in-process hosted services and by POST /internal/jobs/run (Render Cron backstop).
/// Correctness does not depend on timing: approve/checkout/consume also check expiry lazily.
/// </summary>
public sealed class JobRunner(
    IAppDbContext db,
    PurchaseLifecycle lifecycle,
    WebhookService webhooks,
    PaymentCompletionService completion,
    IOptions<CheckoutOptions> checkoutOptions,
    TimeProvider clock,
    ILogger<JobRunner> log)
{
    private const int BatchSize = 20;

    /// <summary>ApprovalExpirySweeper: PENDING/APPROVED approvals past expires_at → purchase EXPIRED, RELEASE, cart unlocked.</summary>
    public async Task<int> ExpireApprovalsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            db.ClearTracking();
            await using var tx = await db.BeginTransactionAsync(cancellationToken);
            var ids = await db.ClaimPurchasesWithExpiredApprovalsAsync(clock.GetUtcNow(), 1, cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            var purchase = await db.LockPurchaseAsync(ids[0], null, cancellationToken);
            if (purchase is { Status: PurchaseStatus.AwaitingApproval or PurchaseStatus.ReadyForCheckout })
            {
                await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Expired, null, LedgerSource.Sweeper, ActorType.System, cancellationToken);
            }
            else
            {
                // Approval still open on a purchase that moved on: close the approval only.
                var approval = await db.ApprovalRequests.SingleAsync(a => a.PurchaseId == ids[0], cancellationToken);
                approval.Expire(clock.GetUtcNow());
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            count++;
        }

        return count;
    }

    /// <summary>CheckoutExpirySweeper: allowed purchases never checked out, and PayPal orders not approved within 3 h (BR-66).</summary>
    public async Task<int> ExpireCheckoutsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            db.ClearTracking();
            await using var tx = await db.BeginTransactionAsync(cancellationToken);
            var ids = await db.ClaimExpiredCheckoutPurchaseIdsAsync(clock.GetUtcNow(), 1, cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            var purchase = await db.LockPurchaseAsync(ids[0], null, cancellationToken);
            if (purchase is { Status: PurchaseStatus.ReadyForCheckout or PurchaseStatus.CheckoutPending })
            {
                await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Expired, null, LedgerSource.Sweeper, ActorType.System, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            count++;
        }

        return count;
    }

    public Task<int> ProcessWebhooksAsync(CancellationToken cancellationToken) => webhooks.ProcessPendingAsync(BatchSize, cancellationToken);

    /// <summary>PaymentReconciler (P1): finish approved orders, refresh pending captures, void stale authorizations.</summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var ids = await db.FindPurchasesToReconcileAsync(now.AddMinutes(-10), BatchSize, cancellationToken);
        var staleAfter = TimeSpan.FromMinutes(checkoutOptions.Value.AuthorizationMaxOpenMinutes);
        var count = 0;

        foreach (var id in ids)
        {
            try
            {
                db.ClearTracking();
                var status = await db.Purchases.AsNoTracking().Where(p => p.Id == id).Select(p => p.Status).SingleAsync(cancellationToken);
                var authorizedAt = await db.Payments.AsNoTracking()
                    .Where(p => p.PurchaseId == id && p.Status == PaymentStatus.Authorized)
                    .Select(p => (DateTimeOffset?)p.UpdatedAt).FirstOrDefaultAsync(cancellationToken);

                if (status == PurchaseStatus.PaymentAuthorized && authorizedAt is { } at && now - at >= staleAfter)
                {
                    await completion.VoidStaleAuthorizationAsync(id, cancellationToken);
                }
                else
                {
                    await completion.CompleteAsync(id, null, LedgerSource.Reconciler, cancellationToken);
                }

                count++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Reconciliation failed for purchase {PurchaseId}", id);
            }
        }

        return count;
    }

    public async Task<JobRunSummary> RunAllAsync(CancellationToken cancellationToken) =>
        new(await ExpireApprovalsAsync(cancellationToken),
            await ExpireCheckoutsAsync(cancellationToken),
            await ProcessWebhooksAsync(cancellationToken),
            await ReconcileAsync(cancellationToken));
}
