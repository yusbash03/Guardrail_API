using System.Text;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Policies;
using Guardrail.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Catalogue;

public sealed record ProductQuery(string? Query, string? CategoryCode, Guid? MerchantId, decimal? MaxUnitPrice, bool ApprovedMerchantsOnly = false);

public sealed class CatalogueService(IAppDbContext db, ICatalogueProvider provider, PolicyLoader loader)
{
    public async Task<IReadOnlyList<CategoryDto>> CategoriesAsync(CancellationToken cancellationToken) =>
        await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).Select(c => new CategoryDto(c.Code, c.Name)).ToListAsync(cancellationToken);

    public async Task<PagedResult<ProductDto>> SearchAsync(Guid userId, ProductQuery query, PageRequest page, CancellationToken cancellationToken)
    {
        var policy = (await loader.LoadCurrentAsync(userId, cancellationToken))?.Snapshot;
        IReadOnlyCollection<Guid>? onlyMerchants = query.ApprovedMerchantsOnly
            ? policy?.ApprovedMerchants.Select(m => m.MerchantId).ToList() ?? []
            : null;

        var result = await provider.SearchAsync(
            new CatalogueSearch(query.Query, query.CategoryCode, query.MerchantId, query.MaxUnitPrice, onlyMerchants, page.Skip, page.SafePageSize),
            cancellationToken);

        var merchantNames = await MerchantNamesAsync(result.Products.Select(p => p.MerchantId), cancellationToken);
        var items = result.Products
            .Select(p => p.ToDto(merchantNames.GetValueOrDefault(p.MerchantId, "?"), policy?.MerchantStatusOf(p.MerchantId) ?? MerchantStatus.New))
            .ToList();
        return new PagedResult<ProductDto>(items, page.SafePage, page.SafePageSize, result.TotalCount);
    }

    public async Task<Result<ProductDto>> GetAsync(Guid userId, Guid productId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().Include(p => p.Merchant).SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
        {
            return DomainError.NotFound("Product");
        }

        var policy = (await loader.LoadCurrentAsync(userId, cancellationToken))?.Snapshot;
        return product.ToDto(product.Merchant!.Name, policy?.MerchantStatusOf(product.MerchantId) ?? MerchantStatus.New, includeDescription: true);
    }

    public async Task<Dictionary<Guid, string>> MerchantNamesAsync(IEnumerable<Guid> merchantIds, CancellationToken cancellationToken)
    {
        var ids = merchantIds.Distinct().ToList();
        return await db.Merchants.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);
    }

    /// <summary>AI_GUARDRAILS.md §5: strip control and zero-width characters; cap length (SEC-22).</summary>
    public static string SanitizeUntrusted(string? text, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var c in text)
        {
            if (char.IsControl(c) || c is '​' or '‌' or '‍' or '⁠' or '﻿' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(c);
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return builder.ToString();
    }
}
