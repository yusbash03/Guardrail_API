using FluentValidation;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Evaluation;
using Guardrail.Application.Ledger;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Authorization;
using Guardrail.Domain.Common;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Guardrail.Application.Approvals;

public sealed record ApproveCommand(string? CartSnapshotHash, string? Password);

public sealed class ApproveCommandValidator : AbstractValidator<ApproveCommand>
{
    public ApproveCommandValidator()
    {
        RuleFor(c => c.CartSnapshotHash).NotEmpty().Matches("^[0-9a-f]{64}$");
        RuleFor(c => c.Password).NotEmpty().MaximumLength(256);
    }
}

/// <summary>
/// Human approval of one exact cart (APPROVAL_WORKFLOW.md). Only reachable from the authenticated web endpoint (BR-02, BR-80):
/// it requires the user's password, which no agent or token holder has (ADR-015).
/// </summary>
public sealed class ApprovalService(
    IAppDbContext db,
    IPasswordService passwords,
    EvaluationService evaluations,
    LedgerService ledger,
    PurchaseLifecycle lifecycle,
    PurchaseQueries queries,
    AuditWriter audit,
    ApproveCommandValidator validator,
    IOptions<ApprovalOptions> options,
    IOptions<AuthOptions> authOptions,
    TimeProvider clock)
{
    public async Task<Result<ApprovalDecisionResponse>> ApproveAsync(Guid userId, Guid approvalId, ApproveCommand command, CancellationToken cancellationToken)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();

        // 0a. Step-up rate limit: 5 failures / 15 min (SEC-06).
        var window = TimeSpan.FromMinutes(options.Value.StepUpWindowMinutes);
        var since = now - window;
        var failures = await db.AuditLogs.AsNoTracking()
            .Where(a => a.UserId == userId && a.Action == AuditActions.ApprovalStepUpFailed && a.CreatedAt >= since)
            .OrderBy(a => a.CreatedAt)
            .Select(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
        if (failures.Count >= options.Value.StepUpMaxFailures)
        {
            var retryAfter = failures[^options.Value.StepUpMaxFailures] + window - now;
            return new DomainError(ErrorCodes.ApprovalStepUpLocked, "Too many incorrect password attempts. Try again later.",
                new Dictionary<string, object?> { ["retryAfterSeconds"] = (int)Math.Ceiling(Math.Max(retryAfter.TotalSeconds, 1)) });
        }

        // 0b. Password before any other state change. The approval is not touched on failure.
        var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        if (!passwords.Verify(user, command.Password!))
        {
            user.RecordAccessFailure(now, authOptions.Value.MaxFailedLogins, TimeSpan.FromMinutes(authOptions.Value.LockoutMinutes));
            audit.Write(AuditActions.ApprovalStepUpFailed, "ApprovalRequest", approvalId, userId, ActorType.User);
            await db.SaveChangesAsync(cancellationToken);
            return new DomainError(ErrorCodes.ReauthenticationFailed, "Incorrect password.");
        }

        await using var tx = await db.BeginTransactionAsync(cancellationToken);

        // 1. Load by id AND owner; lock purchase then approval (consistent lock order).
        var purchaseId = await db.ApprovalRequests.Where(a => a.Id == approvalId && a.UserId == userId).Select(a => (Guid?)a.PurchaseId)
            .SingleOrDefaultAsync(cancellationToken);
        var purchase = purchaseId is { } pid ? await db.LockPurchaseAsync(pid, userId, cancellationToken) : null;
        var approval = await db.LockApprovalAsync(approvalId, userId, cancellationToken);
        if (purchase is null || approval is null)
        {
            return DomainError.NotFound("Approval");
        }

        // 2–6. Binding checks.
        var cart = await db.Carts.Include(c => c.Items).SingleAsync(c => c.Id == approval.CartId, cancellationToken);
        var recomputed = (await evaluations.BuildCartSnapshotAsync(cart, cancellationToken)).ComputeHash();
        var currentVersionId = await db.SpendingProfiles.Where(p => p.UserId == userId).Select(p => p.CurrentPolicyVersionId).SingleAsync(cancellationToken);
        var error = approval.CheckCanApprove(userId, command.CartSnapshotHash!, now, currentVersionId ?? Guid.Empty, recomputed);

        if (error is not null)
        {
            if (error.Code == ErrorCodes.ApprovalExpired)
            {
                await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Expired, null, LedgerSource.User, ActorType.User, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            else if (error.Code == ErrorCodes.ApprovalInvalidated)
            {
                var reason = (string)error.Data!["reason"]!;
                await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Cancelled, reason, LedgerSource.User, ActorType.User, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }

            return error;
        }

        // 7. Approve.
        approval.Approve(now);
        purchase.TransitionTo(PurchaseStatus.ReadyForCheckout, now);
        await ledger.ApplyAsync(purchase, LedgerTransition.ConfirmReservation, LedgerSource.User, null, cancellationToken);
        audit.Write(AuditActions.ApprovalGranted, "ApprovalRequest", approval.Id, userId, ActorType.User,
            new { purchaseId = purchase.Id, amount = MoneyFormat.Format(approval.Amount), approval.CartSnapshotHash });

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return await ResponseAsync(userId, approvalId, purchase.Id, cancellationToken);
    }

    public async Task<Result<ApprovalDecisionResponse>> RejectAsync(Guid userId, Guid approvalId, string? reason, CancellationToken cancellationToken)
    {
        if (reason is { Length: > 200 })
        {
            return DomainError.Validation("Reason must be at most 200 characters.");
        }

        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var purchaseId = await db.ApprovalRequests.Where(a => a.Id == approvalId && a.UserId == userId).Select(a => (Guid?)a.PurchaseId)
            .SingleOrDefaultAsync(cancellationToken);
        var purchase = purchaseId is { } pid ? await db.LockPurchaseAsync(pid, userId, cancellationToken) : null;
        var approval = await db.LockApprovalAsync(approvalId, userId, cancellationToken);
        if (purchase is null || approval is null)
        {
            return DomainError.NotFound("Approval");
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            return new DomainError(ErrorCodes.ApprovalNotPending, $"Approval is {EnumText.ToText(approval.Status)}.",
                new Dictionary<string, object?> { ["status"] = EnumText.ToText(approval.Status) });
        }

        var now = clock.GetUtcNow();
        approval.Reject(reason, now);
        await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Rejected, null, LedgerSource.User, ActorType.User, cancellationToken);
        audit.Write(AuditActions.ApprovalRejected, "ApprovalRequest", approval.Id, userId, ActorType.User, new { reason });

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return await ResponseAsync(userId, approvalId, purchase.Id, cancellationToken);
    }

    private async Task<Result<ApprovalDecisionResponse>> ResponseAsync(Guid userId, Guid approvalId, Guid purchaseId, CancellationToken cancellationToken)
    {
        var approval = await queries.GetApprovalAsync(userId, approvalId, cancellationToken);
        var purchase = await queries.GetAsync(userId, purchaseId, cancellationToken);
        return new ApprovalDecisionResponse(approval.Value!, purchase.Value!);
    }
}
