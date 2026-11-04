using System.Globalization;
using System.Text.Json.Nodes;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Domain.Authorization;
using Guardrail.Domain.Common;
using Guardrail.Domain.Payments;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Purchases;

/// <summary>Builds PurchaseDto / ApprovalDto / TransactionDto from database rows (owner-scoped).</summary>
public sealed class PurchaseQueries(IAppDbContext db)
{
    public async Task<Result<PurchaseDto>> GetAsync(Guid userId, Guid purchaseId, CancellationToken cancellationToken)
    {
        var list = await BuildAsync(userId, db.Purchases.AsNoTracking().Where(p => p.Id == purchaseId && p.UserId == userId), cancellationToken);
        return list.Count == 0 ? DomainError.NotFound("Purchase") : list[0];
    }

    public async Task<PagedResult<PurchaseDto>> ListAsync(Guid userId, PurchaseStatus? status, PageRequest page, CancellationToken cancellationToken)
    {
        var query = db.Purchases.AsNoTracking().Where(p => p.UserId == userId);
        if (status is { } s)
        {
            query = query.Where(p => p.Status == s);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await BuildAsync(userId, query.OrderByDescending(p => p.CreatedAt).Skip(page.Skip).Take(page.SafePageSize), cancellationToken);
        return new PagedResult<PurchaseDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<IReadOnlyList<PurchaseDto>> RecentAsync(Guid userId, int count, CancellationToken cancellationToken) =>
        await BuildAsync(userId, db.Purchases.AsNoTracking().Where(p => p.UserId == userId).OrderByDescending(p => p.CreatedAt).Take(count),
            cancellationToken);

    public async Task<Result<ApprovalDto>> GetApprovalAsync(Guid userId, Guid approvalId, CancellationToken cancellationToken)
    {
        var list = await BuildApprovalsAsync(db.ApprovalRequests.AsNoTracking().Where(a => a.Id == approvalId && a.UserId == userId), cancellationToken);
        return list.Count == 0 ? DomainError.NotFound("Approval") : list[0];
    }

    public async Task<IReadOnlyList<ApprovalDto>> ListApprovalsAsync(Guid userId, ApprovalStatus? status, CancellationToken cancellationToken)
    {
        var query = db.ApprovalRequests.AsNoTracking().Where(a => a.UserId == userId);
        if (status is { } s)
        {
            query = query.Where(a => a.Status == s);
        }

        return await BuildApprovalsAsync(query.OrderByDescending(a => a.CreatedAt).Take(100), cancellationToken);
    }

    public async Task<PagedResult<TransactionDto>> TransactionsAsync(
        Guid userId, PurchaseStatus? status, DateTimeOffset? from, DateTimeOffset? to, string? sort, PageRequest page,
        CancellationToken cancellationToken)
    {
        var query = db.Purchases.AsNoTracking().Where(p => p.UserId == userId);
        if (status is { } s)
        {
            query = query.Where(p => p.Status == s);
        }

        if (from is { } f)
        {
            query = query.Where(p => p.CreatedAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(p => p.CreatedAt <= t);
        }

        // Server-side sort for AG Grid: field[:asc|desc]
        var (field, descending) = ParseSort(sort);
        query = (field, descending) switch
        {
            ("amount", false) => query.OrderBy(p => p.Amount),
            ("amount", true) => query.OrderByDescending(p => p.Amount),
            ("status", false) => query.OrderBy(p => p.Status),
            ("status", true) => query.OrderByDescending(p => p.Status),
            ("completedAt", false) => query.OrderBy(p => p.CompletedAt),
            ("completedAt", true) => query.OrderByDescending(p => p.CompletedAt),
            (_, false) => query.OrderBy(p => p.CreatedAt),
            _ => query.OrderByDescending(p => p.CreatedAt),
        };

        var total = await query.CountAsync(cancellationToken);
        var purchases = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        var ids = purchases.Select(p => p.Id).ToList();
        var merchantIds = purchases.Select(p => p.MerchantId).Distinct().ToList();
        var merchants = await db.Merchants.AsNoTracking().Where(m => merchantIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);
        var approvals = await db.ApprovalRequests.AsNoTracking().Where(a => ids.Contains(a.PurchaseId))
            .ToDictionaryAsync(a => a.PurchaseId, a => a.Status, cancellationToken);
        var payments = (await db.Payments.AsNoTracking().Where(p => ids.Contains(p.PurchaseId)).ToListAsync(cancellationToken))
            .GroupBy(p => p.PurchaseId).ToDictionary(g => g.Key, PickPayment);

        var items = purchases.Select(p =>
        {
            var payment = payments.GetValueOrDefault(p.Id);
            return new TransactionDto(p.Id, p.Status, MoneyFormat.Format(p.Amount), p.Currency, p.MerchantId, merchants.GetValueOrDefault(p.MerchantId, "?"),
                approvals.TryGetValue(p.Id, out var a) ? a : null, payment?.Status, payment?.PaypalOrderId, payment?.PaypalAuthorizationId,
                payment?.PaypalCaptureId, p.FailureCode, p.CreatedAt, p.CompletedAt);
        }).ToList();
        return new PagedResult<TransactionDto>(items, page.SafePage, page.SafePageSize, total);
    }

    private async Task<List<PurchaseDto>> BuildAsync(Guid userId, IQueryable<Purchase> query, CancellationToken cancellationToken)
    {
        var purchases = await query.ToListAsync(cancellationToken);
        if (purchases.Count == 0)
        {
            return [];
        }

        var ids = purchases.Select(p => p.Id).ToList();
        var evaluationIds = purchases.Select(p => p.PolicyEvaluationId).ToList();
        var versionIds = purchases.Select(p => p.PolicyVersionId).Distinct().ToList();
        var merchantIds = purchases.Select(p => p.MerchantId).Distinct().ToList();

        var evaluations = await db.PolicyEvaluations.AsNoTracking().Where(e => evaluationIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        var versions = await db.PolicyVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.VersionNumber, cancellationToken);
        var merchants = await db.Merchants.AsNoTracking().Where(m => merchantIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);
        var approvals = await db.ApprovalRequests.AsNoTracking().Where(a => ids.Contains(a.PurchaseId) && a.UserId == userId)
            .ToDictionaryAsync(a => a.PurchaseId, cancellationToken);
        var payments = (await db.Payments.AsNoTracking().Where(p => ids.Contains(p.PurchaseId)).ToListAsync(cancellationToken))
            .GroupBy(p => p.PurchaseId).ToDictionary(g => g.Key, PickPayment);
        var names = await ProductNamesAsync(evaluations.Values.Select(e => e.CartSnapshotJson), cancellationToken);

        return purchases.Select(p =>
        {
            var evaluation = evaluations[p.PolicyEvaluationId];
            var approval = approvals.GetValueOrDefault(p.Id);
            var payment = payments.GetValueOrDefault(p.Id);
            return new PurchaseDto(
                p.Id, p.Status, MoneyFormat.Format(p.Amount), p.Currency, p.MerchantId, merchants.GetValueOrDefault(p.MerchantId, "?"),
                p.CartId, p.CartVersion, p.CartSnapshotHash, versions.GetValueOrDefault(p.PolicyVersionId), evaluation.Decision,
                approval is null ? null : new ApprovalSummaryDto(approval.Id, approval.Status, approval.ExpiresAt),
                payment is null ? null : ToSummary(payment),
                SnapshotItems(evaluation.CartSnapshotJson, names),
                p.CreatedAt, p.CompletedAt, p.FailureCode);
        }).ToList();
    }

    private async Task<List<ApprovalDto>> BuildApprovalsAsync(IQueryable<ApprovalRequest> query, CancellationToken cancellationToken)
    {
        var approvals = await query.ToListAsync(cancellationToken);
        if (approvals.Count == 0)
        {
            return [];
        }

        var evaluationIds = approvals.Select(a => a.PolicyEvaluationId).ToList();
        var versionIds = approvals.Select(a => a.PolicyVersionId).Distinct().ToList();
        var merchantIds = approvals.Select(a => a.MerchantId).Distinct().ToList();
        var evaluations = await db.PolicyEvaluations.AsNoTracking().Where(e => evaluationIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        var versions = await db.PolicyVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.VersionNumber, cancellationToken);
        var merchants = await db.Merchants.AsNoTracking().Where(m => merchantIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);
        var names = await ProductNamesAsync(evaluations.Values.Select(e => e.CartSnapshotJson), cancellationToken);

        return approvals.Select(a =>
        {
            var evaluation = evaluations[a.PolicyEvaluationId];
            return new ApprovalDto(a.Id, a.Status, a.PurchaseId, MoneyFormat.Format(a.Amount), a.Currency, a.MerchantId,
                merchants.GetValueOrDefault(a.MerchantId, "?"), a.CartSnapshotHash, versions.GetValueOrDefault(a.PolicyVersionId),
                DtoMapper.ReasonsOf(a.Reasons), DtoMapper.ImpactsOf(evaluation.BudgetImpacts), SnapshotItems(evaluation.CartSnapshotJson, names),
                a.RequestedByActor, a.CreatedAt, a.ExpiresAt, a.DecidedAt, a.InvalidatedReason);
        }).ToList();
    }

    public static PaymentSummaryDto ToSummary(Payment payment)
    {
        FinalCheckDto? finalCheck = null;
        if (payment.FinalCheckResult is { } json)
        {
            finalCheck = JsonDefaults.Deserialize<FinalCheckDto>(json);
        }

        return new PaymentSummaryDto(payment.Status, payment.PaypalOrderId, payment.PaypalAuthorizationId, payment.PaypalCaptureId, finalCheck);
    }

    /// <summary>The active payment, else the most recent one.</summary>
    internal static Payment PickPayment(IEnumerable<Payment> payments)
    {
        var list = payments.ToList();
        return list.Where(p => p.IsActive).OrderByDescending(p => p.CreatedAt).FirstOrDefault()
               ?? list.OrderByDescending(p => p.CreatedAt).First();
    }

    private async Task<Dictionary<Guid, string>> ProductNamesAsync(IEnumerable<string> snapshots, CancellationToken cancellationToken)
    {
        var ids = snapshots
            .SelectMany(json => JsonNode.Parse(json)!["items"]!.AsArray().Select(i => Guid.Parse(i!["productId"]!.GetValue<string>())))
            .Distinct().ToList();
        return await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
    }

    /// <summary>Line items as frozen in the canonical cart snapshot.</summary>
    public static IReadOnlyList<CartItemDto> SnapshotItems(string canonicalJson, IReadOnlyDictionary<Guid, string> productNames) =>
        JsonNode.Parse(canonicalJson)!["items"]!.AsArray().Select(i =>
        {
            var productId = Guid.Parse(i!["productId"]!.GetValue<string>());
            return new CartItemDto(productId, productNames.GetValueOrDefault(productId, i["sku"]!.GetValue<string>()), i["sku"]!.GetValue<string>(),
                i["categoryCode"]!.GetValue<string>(), i["unitPrice"]!.GetValue<string>(), i["quantity"]!.GetValue<int>(), i["lineTotal"]!.GetValue<string>());
        }).ToList();

    private static (string Field, bool Descending) ParseSort(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return ("createdAt", true);
        }

        var parts = sort.Split(':', 2);
        var descending = parts.Length < 2 || !string.Equals(parts[1], "asc", StringComparison.OrdinalIgnoreCase);
        return (parts[0].Trim(), descending);
    }

    internal static decimal ParseMoney(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);
}
