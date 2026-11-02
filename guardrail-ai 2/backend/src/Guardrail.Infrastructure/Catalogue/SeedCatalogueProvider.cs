using Guardrail.Application.Abstractions;
using Guardrail.Domain.Catalogue;
using Guardrail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Guardrail.Infrastructure.Catalogue;

/// <summary>
/// Searches the seeded catalogue with PostgreSQL full-text search (generated tsvector + GIN), falling back to a
/// name substring match. Sorted by relevance (ts_rank), then price ascending (AI_TOOL_CONTRACTS.md §1).
/// </summary>
public sealed class SeedCatalogueProvider(AppDbContext db) : ICatalogueProvider
{
    public async Task<CatalogueSearchResult> SearchAsync(CatalogueSearch search, CancellationToken cancellationToken)
    {
        var query = db.Products.AsNoTracking().Where(p => p.Merchant!.IsActive);

        if (!string.IsNullOrWhiteSpace(search.CategoryCode))
        {
            query = query.Where(p => p.CategoryCode == search.CategoryCode);
        }

        if (search.MerchantId is { } merchantId)
        {
            query = query.Where(p => p.MerchantId == merchantId);
        }

        if (search.MaxUnitPrice is { } max)
        {
            query = query.Where(p => p.UnitPrice <= max);
        }

        if (search.OnlyMerchantIds is { } only)
        {
            var ids = only.ToList();
            query = query.Where(p => ids.Contains(p.MerchantId));
        }

        IOrderedQueryable<Product> ordered;
        if (!string.IsNullOrWhiteSpace(search.Query))
        {
            var text = search.Query.Trim();
            var pattern = $"%{text.Replace("%", string.Empty).Replace("_", string.Empty).ToLowerInvariant()}%";
            query = query.Where(p =>
                EF.Property<NpgsqlTsVector>(p, "SearchVector").Matches(EF.Functions.PlainToTsQuery("english", text))
                || EF.Functions.Like(p.Name.ToLower(), pattern));
            ordered = query
                .OrderByDescending(p => EF.Property<NpgsqlTsVector>(p, "SearchVector").Rank(EF.Functions.PlainToTsQuery("english", text)))
                .ThenBy(p => p.UnitPrice);
        }
        else
        {
            ordered = query.OrderBy(p => p.UnitPrice).ThenBy(p => p.Name);
        }

        var total = await query.CountAsync(cancellationToken);
        var products = await ordered.ThenBy(p => p.Id).Skip(search.Skip).Take(search.Take).ToListAsync(cancellationToken);
        return new CatalogueSearchResult(products, total);
    }
}
