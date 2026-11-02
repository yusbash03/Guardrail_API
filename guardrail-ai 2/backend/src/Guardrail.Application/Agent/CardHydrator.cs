using Guardrail.Application.Abstractions;
using Guardrail.Application.Carts;
using Guardrail.Application.Catalogue;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Evaluation;
using Guardrail.Application.Ledger;
using Guardrail.Application.Merchants;
using Guardrail.Application.Policies;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Agent;

public sealed record HydratedCard(string Type, Guid? Id, IReadOnlyList<Guid>? Ids, object? Data);

/// <summary>Turns card references into server-rendered payloads built from database records at read time.</summary>
public sealed class CardHydrator(
    IAppDbContext db,
    CartService carts,
    EvaluationService evaluations,
    PurchaseQueries purchases,
    CatalogueService catalogue,
    MerchantQueries merchants,
    PolicyService policies,
    SpendingQueries spending,
    PolicyLoader loader)
{
    public async Task<IReadOnlyList<HydratedCard>> HydrateAsync(Guid userId, IReadOnlyList<CardRef> cards, CancellationToken cancellationToken)
    {
        var result = new List<HydratedCard>(cards.Count);
        foreach (var card in cards)
        {
            result.Add(new HydratedCard(card.Type, card.Id, card.Ids, await DataAsync(userId, card, cancellationToken)));
        }

        return result;
    }

    private async Task<object?> DataAsync(Guid userId, CardRef card, CancellationToken cancellationToken)
    {
        switch (card.Type)
        {
            case CardTypes.Cart when card.Id is { } id:
                return (await carts.GetAsync(userId, id, cancellationToken)).Value;
            case CardTypes.Evaluation when card.Id is { } id:
                return (await evaluations.GetAsync(userId, id, cancellationToken)).Value;
            case CardTypes.Approval when card.Id is { } id:
                return (await purchases.GetApprovalAsync(userId, id, cancellationToken)).Value;
            case CardTypes.Purchase when card.Id is { } id:
                return (await purchases.GetAsync(userId, id, cancellationToken)).Value;
            case CardTypes.Product when card.Id is { } id:
                return (await catalogue.GetAsync(userId, id, cancellationToken)).Value;
            case CardTypes.ProductList when card.Ids is { } ids:
            {
                var policy = (await loader.LoadCurrentAsync(userId, cancellationToken))?.Snapshot;
                var products = await db.Products.AsNoTracking().Include(p => p.Merchant).Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
                return ids.Select(id => products.FirstOrDefault(p => p.Id == id)).OfType<Domain.Catalogue.Product>()
                    .Select(p => p.ToDto(p.Merchant!.Name, policy?.MerchantStatusOf(p.MerchantId) ?? MerchantStatus.New)).ToList();
            }

            case CardTypes.MerchantList when card.Ids is { } ids:
                return (await merchants.ListAsync(userId, null, null, null, cancellationToken)).Where(m => ids.Contains(m.Id)).ToList();
            case CardTypes.PolicySummary:
                return (await policies.GetCurrentAsync(userId, cancellationToken)).Value;
            case CardTypes.Budget:
                return (await spending.SummaryAsync(userId, BudgetPeriodType.Weekly, null, cancellationToken)).Value;
            default:
                return null;
        }
    }

    public static IReadOnlyList<CardRef> Parse(string json) => JsonDefaults.Deserialize<List<CardRef>>(json);
}
