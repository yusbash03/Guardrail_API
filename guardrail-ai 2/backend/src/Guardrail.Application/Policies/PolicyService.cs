using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Authorization;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;
using Guardrail.Domain.Shopping;
using Guardrail.Domain.Spending;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Policies;

public sealed record UpdatePolicyCommand(int ExpectedVersionNumber, string? ChangeSummary, PolicyDocument Policy);

/// <summary>
/// Policy reads and copy-on-write updates (ADR-004). Only reachable from user endpoints — never from agent tools (BR-04).
/// </summary>
public sealed class PolicyService(
    IAppDbContext db,
    PolicyLoader loader,
    PolicyDocumentValidator validator,
    PurchaseLifecycle lifecycle,
    AuditWriter audit,
    TimeProvider clock)
{
    public async Task<Result<PolicyDocument>> GetCurrentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var current = await loader.LoadCurrentAsync(userId, cancellationToken);
        return current is null
            ? new DomainError(ErrorCodes.NoActivePolicy, "No active policy.")
            : PolicyMapper.ToDocument(current.Snapshot);
    }

    public async Task<Result<PolicyDocument>> GetVersionAsync(Guid userId, int versionNumber, CancellationToken cancellationToken)
    {
        var snapshot = await loader.LoadSnapshotAsync(userId, versionNumber, cancellationToken);
        return snapshot is null ? DomainError.NotFound("Policy version") : PolicyMapper.ToDocument(snapshot);
    }

    public async Task<PagedResult<PolicyVersionSummaryDto>> ListVersionsAsync(Guid userId, PageRequest page, CancellationToken cancellationToken)
    {
        var query = db.PolicyVersions.AsNoTracking().Where(v => v.UserId == userId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(v => v.VersionNumber).Skip(page.Skip).Take(page.SafePageSize)
            .Select(v => new PolicyVersionSummaryDto(v.VersionNumber, v.Id, v.ChangeSummary, v.CreatedAt, v.SnapshotHash))
            .ToListAsync(cancellationToken);
        return new PagedResult<PolicyVersionSummaryDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<Result<PolicyDocument>> UpdateAsync(Guid userId, UpdatePolicyCommand command, CancellationToken cancellationToken)
    {
        if (await validator.CheckAsync(command.Policy, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        return await ChangeAsync(userId, command.ExpectedVersionNumber, _ => command.Policy, command.ChangeSummary,
            AuditActions.PolicyUpdated, cancellationToken);
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the current document and stores version n+1 under the profile lock (BR-10, BR-11, BR-13).
    /// </summary>
    public async Task<Result<PolicyDocument>> ChangeAsync(
        Guid userId,
        int expectedVersionNumber,
        Func<PolicyDocument, PolicyDocument> change,
        string? changeSummary,
        string auditAction,
        CancellationToken cancellationToken,
        object? auditData = null)
    {
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var profile = await db.LockProfileAsync(userId, cancellationToken);
        var current = profile is null ? null : await loader.LoadCurrentAsync(userId, cancellationToken);
        if (profile is null || current is null)
        {
            return new DomainError(ErrorCodes.NoActivePolicy, "No active policy.");
        }

        if (current.Version.VersionNumber != expectedVersionNumber)
        {
            return new DomainError(ErrorCodes.PolicyVersionConflict, "The policy was changed by someone else. Reload and try again.",
                new Dictionary<string, object?> { ["currentVersionNumber"] = current.Version.VersionNumber });
        }

        var before = PolicyMapper.ToDocument(current.Snapshot);
        var after = change(before) with { Currency = before.Currency };

        if (await CheckSemanticsAsync(after, profile, cancellationToken) is { } semanticError)
        {
            return semanticError;
        }

        var (_, beforeHash) = PolicyMapper.ContentSnapshot(before);
        var (afterJson, afterHash) = PolicyMapper.ContentSnapshot(after);
        var summary = string.IsNullOrWhiteSpace(changeSummary) ? Summarize(before, after) : changeSummary.Trim();
        var now = clock.GetUtcNow();

        var version = PolicyVersion.Create(profile, current.Version.VersionNumber + 1,
            versionId => PolicyMapper.ToRules(after, versionId, userId, now), afterJson, afterHash, summary, ActorType.User, now);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken); // version row must exist before the profile FK points to it
        profile.ActivateVersion(version, now);

        audit.Write(AuditActions.PolicyUpdated, "PolicyVersion", version.Id, userId, ActorType.User,
            new { versionNumber = version.VersionNumber, changeSummary = summary }, beforeHash, afterHash);
        if (auditAction != AuditActions.PolicyUpdated)
        {
            audit.Write(auditAction, "PolicyVersion", version.Id, userId, ActorType.User, auditData);
        }

        await InvalidateStaleApprovalsAsync(userId, version.Id, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var reloaded = await loader.LoadCurrentAsync(userId, cancellationToken);
        return PolicyMapper.ToDocument(reloaded!.Snapshot);
    }

    /// <summary>BR-13: a policy change invalidates every unconsumed approval bound to an older version and releases its budget.</summary>
    private async Task InvalidateStaleApprovalsAsync(Guid userId, Guid newVersionId, CancellationToken cancellationToken)
    {
        var stale = await db.ApprovalRequests
            .Where(a => a.UserId == userId && a.PolicyVersionId != newVersionId
                        && (a.Status == ApprovalStatus.Pending || a.Status == ApprovalStatus.Approved))
            .Select(a => a.PurchaseId)
            .ToListAsync(cancellationToken);

        foreach (var purchaseId in stale)
        {
            var purchase = await db.LockPurchaseAsync(purchaseId, userId, cancellationToken);
            if (purchase is { Status: PurchaseStatus.AwaitingApproval or PurchaseStatus.ReadyForCheckout })
            {
                await lifecycle.CloseBeforePaymentAsync(purchase, PurchaseStatus.Cancelled, InvalidationReasons.PolicyVersionChanged,
                    LedgerSource.System, ActorType.System, cancellationToken);
            }
        }
    }

    private async Task<DomainError?> CheckSemanticsAsync(PolicyDocument doc, SpendingProfile profile, CancellationToken cancellationToken)
    {
        if (doc.Currency is { } currency && currency != profile.Currency)
        {
            return new DomainError(ErrorCodes.CurrencyMismatch, $"Policy currency must be {profile.Currency}.");
        }

        if (doc.SpendingLimits.GroupBy(l => l.Period).Any(g => g.Count() > 1))
        {
            return new DomainError(ErrorCodes.PolicyInvalid, "At most one spending limit per period.");
        }

        if (doc.CategoryBudgets.GroupBy(b => (b.CategoryCode, b.Period)).Any(g => g.Count() > 1))
        {
            return new DomainError(ErrorCodes.PolicyInvalid, "At most one category budget per category and period.");
        }

        if (doc.MerchantRules.ApprovedMerchantIds.Intersect(doc.MerchantRules.BlockedMerchantIds).Any())
        {
            return new DomainError(ErrorCodes.PolicyMerchantConflict, "A merchant cannot be both approved and blocked.");
        }

        var categories = doc.CategoryBudgets.Select(b => b.CategoryCode)
            .Concat(doc.ProductRestrictions.Where(r => r.Match.Type == "CATEGORY").Select(r => r.Match.Value))
            .Distinct().ToList();
        var knownCategories = await db.Categories.Where(c => categories.Contains(c.Code)).CountAsync(cancellationToken);
        if (knownCategories != categories.Count)
        {
            return DomainError.Validation("Unknown category code.");
        }

        var merchants = doc.MerchantRules.ApprovedMerchantIds.Concat(doc.MerchantRules.BlockedMerchantIds).Distinct().ToList();
        if (await db.Merchants.CountAsync(m => merchants.Contains(m.Id), cancellationToken) != merchants.Count)
        {
            return DomainError.Validation("Unknown merchant id.");
        }

        var products = doc.ProductRestrictions.Where(r => r.Match.Type == "PRODUCT").Select(r => Guid.Parse(r.Match.Value)).Distinct().ToList();
        if (await db.Products.CountAsync(p => products.Contains(p.Id), cancellationToken) != products.Count)
        {
            return DomainError.Validation("Unknown product id.");
        }

        return null;
    }

    /// <summary>Human-readable change summary when the client doesn't provide one (BR-10).</summary>
    private static string Summarize(PolicyDocument before, PolicyDocument after)
    {
        var parts = new List<string>();
        if (before.ApprovalThreshold?.Amount != after.ApprovalThreshold?.Amount)
        {
            parts.Add(after.ApprovalThreshold is null ? "Approval threshold removed" : $"Approval threshold set to ${after.ApprovalThreshold.Amount}");
        }

        if (before.MaxTransactionAmount?.Amount != after.MaxTransactionAmount?.Amount)
        {
            parts.Add(after.MaxTransactionAmount is null ? "Max transaction removed" : $"Max transaction set to ${after.MaxTransactionAmount.Amount}");
        }

        foreach (var budget in after.CategoryBudgets.Except(before.CategoryBudgets))
        {
            parts.Add($"{CategoryCodes.DisplayName(budget.CategoryCode)} {budget.Period.ToLowerInvariant()} budget ${budget.Limit}");
        }

        foreach (var removed in before.CategoryBudgets.Where(b => !after.CategoryBudgets.Any(a => a.CategoryCode == b.CategoryCode && a.Period == b.Period)))
        {
            parts.Add($"{CategoryCodes.DisplayName(removed.CategoryCode)} {removed.Period.ToLowerInvariant()} budget removed");
        }

        foreach (var limit in after.SpendingLimits.Except(before.SpendingLimits))
        {
            parts.Add($"{limit.Period.ToLowerInvariant()} spending limit ${limit.Limit}");
        }

        if (!before.MerchantRules.ApprovedMerchantIds.ToHashSet().SetEquals(after.MerchantRules.ApprovedMerchantIds)
            || !before.MerchantRules.BlockedMerchantIds.ToHashSet().SetEquals(after.MerchantRules.BlockedMerchantIds))
        {
            parts.Add("Merchant lists updated");
        }

        if (before.MerchantRules.NewMerchantAction != after.MerchantRules.NewMerchantAction)
        {
            parts.Add($"New merchants: {after.MerchantRules.NewMerchantAction}");
        }

        if (before.UnbudgetedCategoryAction != after.UnbudgetedCategoryAction)
        {
            parts.Add($"Unbudgeted categories: {after.UnbudgetedCategoryAction}");
        }

        if (before.ProductRestrictions.Count != after.ProductRestrictions.Count
            || before.TimeRestrictions.Count != after.TimeRestrictions.Count
            || (before.DuplicatePurchase is null) != (after.DuplicatePurchase is null))
        {
            parts.Add("Restrictions updated");
        }

        return parts.Count == 0 ? "Policy saved without changes" : string.Join("; ", parts);
    }
}
