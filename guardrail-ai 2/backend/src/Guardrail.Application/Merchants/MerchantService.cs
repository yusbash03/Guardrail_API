using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Policies;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Catalogue;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Merchants;

/// <summary>Merchant approve/block/reset — policy changes, user-only (BR-04). Never referenced by agent tools.</summary>
public sealed class MerchantService(IAppDbContext db, MerchantQueries queries, PolicyService policies)
{
    /// <summary>BR-04: user-only. Approving removes the merchant from the blocked list; each change creates a policy version.</summary>
    public Task<Result<MerchantChangeDto>> ApproveAsync(Guid userId, Guid merchantId, int expectedVersion, CancellationToken cancellationToken) =>
        ChangeAsync(userId, merchantId, expectedVersion, MerchantStatus.Approved, cancellationToken);

    public Task<Result<MerchantChangeDto>> BlockAsync(Guid userId, Guid merchantId, int expectedVersion, CancellationToken cancellationToken) =>
        ChangeAsync(userId, merchantId, expectedVersion, MerchantStatus.Blocked, cancellationToken);

    public Task<Result<MerchantChangeDto>> ResetAsync(Guid userId, Guid merchantId, int expectedVersion, CancellationToken cancellationToken) =>
        ChangeAsync(userId, merchantId, expectedVersion, MerchantStatus.New, cancellationToken);

    private async Task<Result<MerchantChangeDto>> ChangeAsync(
        Guid userId, Guid merchantId, int expectedVersion, MerchantStatus target, CancellationToken cancellationToken)
    {
        var merchant = await db.Merchants.AsNoTracking().SingleOrDefaultAsync(m => m.Id == merchantId, cancellationToken);
        if (merchant is null)
        {
            return DomainError.NotFound("Merchant");
        }

        var (summary, action) = target switch
        {
            MerchantStatus.Approved => ($"Approved merchant {merchant.Name}", AuditActions.MerchantApproved),
            MerchantStatus.Blocked => ($"Blocked merchant {merchant.Name}", AuditActions.MerchantBlocked),
            _ => ($"Reset merchant {merchant.Name} to new", AuditActions.PolicyUpdated),
        };

        var result = await policies.ChangeAsync(userId, expectedVersion, doc =>
        {
            var approved = doc.MerchantRules.ApprovedMerchantIds.Where(id => id != merchantId).ToList();
            var blocked = doc.MerchantRules.BlockedMerchantIds.Where(id => id != merchantId).ToList();
            if (target == MerchantStatus.Approved)
            {
                approved.Add(merchantId);
            }
            else if (target == MerchantStatus.Blocked)
            {
                blocked.Add(merchantId);
            }

            return doc with { MerchantRules = doc.MerchantRules with { ApprovedMerchantIds = approved, BlockedMerchantIds = blocked } };
        }, summary, action, cancellationToken, new { merchantId, merchantName = merchant.Name });

        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        var dto = await queries.GetAsync(userId, merchantId, cancellationToken);
        return new MerchantChangeDto(dto.Value!, result.Value!.VersionNumber!.Value);
    }
}

/// <summary>Read side of merchants (safe for agent tools).</summary>
public sealed class MerchantQueries(IAppDbContext db, PolicyLoader loader)
{
    public async Task<IReadOnlyList<MerchantDto>> ListAsync(
        Guid userId, string? query, string? categoryCode, MerchantStatus? status, CancellationToken cancellationToken)
    {
        var merchants = db.Merchants.AsNoTracking().Where(m => m.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{query.Trim()}%";
            merchants = merchants.Where(m => EF.Functions.Like(m.Name.ToLower(), pattern.ToLower()) || EF.Functions.Like(m.Slug, pattern.ToLower()));
        }

        if (!string.IsNullOrWhiteSpace(categoryCode))
        {
            merchants = merchants.Where(m => m.CategoryCodes.Contains(categoryCode));
        }

        var list = await merchants.OrderBy(m => m.Name).ToListAsync(cancellationToken);
        var counts = await ProductCountsAsync(list.Select(m => m.Id).ToList(), cancellationToken);
        var snapshot = (await loader.LoadCurrentAsync(userId, cancellationToken))?.Snapshot;

        return list
            .Select(m => ToDto(m, snapshot, counts.GetValueOrDefault(m.Id)))
            .Where(m => status is null || m.Status == status)
            .ToList();
    }

    public async Task<Result<MerchantDto>> GetAsync(Guid userId, Guid merchantId, CancellationToken cancellationToken)
    {
        var merchant = await db.Merchants.AsNoTracking().SingleOrDefaultAsync(m => m.Id == merchantId, cancellationToken);
        if (merchant is null)
        {
            return DomainError.NotFound("Merchant");
        }

        var counts = await ProductCountsAsync([merchantId], cancellationToken);
        var snapshot = (await loader.LoadCurrentAsync(userId, cancellationToken))?.Snapshot;
        return ToDto(merchant, snapshot, counts.GetValueOrDefault(merchantId));
    }

    private async Task<Dictionary<Guid, int>> ProductCountsAsync(List<Guid> merchantIds, CancellationToken cancellationToken) =>
        await db.Products.AsNoTracking()
            .Where(p => merchantIds.Contains(p.MerchantId))
            .GroupBy(p => p.MerchantId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

    private static MerchantDto ToDto(Merchant m, PolicySnapshot? policy, int productCount) =>
        new(m.Id, m.Slug, m.Name, m.Description, m.LogoUrl, m.CategoryCodes, policy?.MerchantStatusOf(m.Id) ?? MerchantStatus.New, productCount);
}
