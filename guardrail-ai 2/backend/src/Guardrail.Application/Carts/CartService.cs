using FluentValidation;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Catalogue;
using Guardrail.Domain.Common;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Carts;

public sealed record CartLine(Guid ProductId, int Quantity);

public sealed record CreateCartCommand(Guid MerchantId, IReadOnlyList<CartLine>? Items, Guid? SupersedesCartId = null);

public sealed class CreateCartValidator : AbstractValidator<CreateCartCommand>
{
    public CreateCartValidator()
    {
        RuleFor(c => c.MerchantId).NotEmpty();
        RuleFor(c => c.Items).Must(i => i is null || i.Count <= ShoppingCart.MaxDistinctItems)
            .WithMessage($"At most {ShoppingCart.MaxDistinctItems} items.");
        RuleForEach(c => c.Items).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, ShoppingCart.MaxQuantity);
        });
    }
}

public sealed class CartService(IAppDbContext db, CreateCartValidator validator, AuditWriter audit, TimeProvider clock)
{
    public async Task<Result<CartDto>> CreateAsync(
        Guid userId, CreateCartCommand command, ActorType actor, Guid? sessionId, CancellationToken cancellationToken)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var merchant = await db.Merchants.AsNoTracking().SingleOrDefaultAsync(m => m.Id == command.MerchantId && m.IsActive, cancellationToken);
        if (merchant is null)
        {
            return DomainError.NotFound("Merchant");
        }

        // Duplicate productIds are merged (AI_TOOL_CONTRACTS.md §7).
        var lines = (command.Items ?? [])
            .GroupBy(l => l.ProductId)
            .Select(g => new CartLine(g.Key, g.Sum(l => l.Quantity)))
            .ToList();
        if (lines.Count > ShoppingCart.MaxDistinctItems)
        {
            return new DomainError(ErrorCodes.CartLimitExceeded, $"A cart can contain at most {ShoppingCart.MaxDistinctItems} distinct items.");
        }

        var productIds = lines.Select(l => l.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        if (productIds.FirstOrDefault(id => !products.ContainsKey(id)) is var missing && missing != Guid.Empty)
        {
            return new DomainError(ErrorCodes.ProductNotFound, "A product in the request does not exist.",
                new Dictionary<string, object?> { ["productId"] = missing });
        }

        var now = clock.GetUtcNow();
        await using var tx = await db.BeginTransactionAsync(cancellationToken);

        if (command.SupersedesCartId is { } supersededId)
        {
            var superseded = await db.LockCartAsync(supersededId, userId, cancellationToken);
            if (superseded is null || superseded.Status != CartStatus.Open)
            {
                return DomainError.Validation("supersedesCartId must be an OPEN cart you own.");
            }

            superseded.Abandon(now);
        }

        var cart = ShoppingCart.Create(userId, merchant, actor, sessionId, now);
        foreach (var line in lines)
        {
            if (cart.AddItem(products[line.ProductId], line.Quantity, now, products) is { } error)
            {
                return error;
            }
        }

        await AbandonOverflowAsync(userId, now, cancellationToken);
        db.Carts.Add(cart);
        audit.Write(AuditActions.CartCreated, "ShoppingCart", cart.Id, userId, actor, new { merchantId = merchant.Id, items = lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return cart.ToDto(merchant.Name, HashOf(cart, merchant.Name));
    }

    public async Task<IReadOnlyList<CartDto>> ListAsync(Guid userId, CartStatus? status, CancellationToken cancellationToken)
    {
        var query = db.Carts.AsNoTracking().Include(c => c.Items).Where(c => c.UserId == userId);
        if (status is { } s)
        {
            query = query.Where(c => c.Status == s);
        }

        var carts = await query.OrderByDescending(c => c.UpdatedAt).Take(50).ToListAsync(cancellationToken);
        var names = await MerchantNamesAsync(carts.Select(c => c.MerchantId), cancellationToken);
        return carts.Select(c => c.ToDto(names[c.MerchantId], HashOf(c, names[c.MerchantId]))).ToList();
    }

    public async Task<Result<CartDto>> GetAsync(Guid userId, Guid cartId, CancellationToken cancellationToken)
    {
        var cart = await db.Carts.AsNoTracking().Include(c => c.Items).SingleOrDefaultAsync(c => c.Id == cartId && c.UserId == userId, cancellationToken);
        if (cart is null)
        {
            return DomainError.NotFound("Cart");
        }

        var name = (await MerchantNamesAsync([cart.MerchantId], cancellationToken))[cart.MerchantId];
        return cart.ToDto(name, HashOf(cart, name));
    }

    public Task<Result<CartDto>> AddItemAsync(Guid userId, Guid cartId, CartLine line, int? ifMatch, CancellationToken cancellationToken) =>
        MutateAsync(userId, cartId, ifMatch, line.ProductId, (cart, product, now, prices) =>
            line.Quantity is < 1 or > ShoppingCart.MaxQuantity
                ? DomainError.Validation($"Quantity must be between 1 and {ShoppingCart.MaxQuantity}.")
                : cart.AddItem(product!, line.Quantity, now, prices), cancellationToken);

    public Task<Result<CartDto>> SetQuantityAsync(Guid userId, Guid cartId, Guid productId, int quantity, int? ifMatch, CancellationToken cancellationToken) =>
        MutateAsync(userId, cartId, ifMatch, productId, (cart, product, now, prices) =>
            cart.SetQuantity(product!, quantity, now, prices), cancellationToken);

    public Task<Result<CartDto>> RemoveItemAsync(Guid userId, Guid cartId, Guid productId, int? ifMatch, CancellationToken cancellationToken) =>
        MutateAsync(userId, cartId, ifMatch, null, (cart, _, now, prices) => cart.RemoveItem(productId, now, prices), cancellationToken);

    public async Task<Result<Unit>> AbandonAsync(Guid userId, Guid cartId, CancellationToken cancellationToken)
    {
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var cart = await db.LockCartAsync(cartId, userId, cancellationToken);
        if (cart is null)
        {
            return DomainError.NotFound("Cart");
        }

        if (cart.Status != CartStatus.Open)
        {
            return cart.Status == CartStatus.Locked
                ? new DomainError(ErrorCodes.CartLocked, "This cart is locked by an active purchase.")
                : new DomainError(ErrorCodes.CartNotOpen, "Only OPEN carts can be abandoned.");
        }

        cart.Abandon(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Unit.Value;
    }

    private async Task<Result<CartDto>> MutateAsync(
        Guid userId,
        Guid cartId,
        int? ifMatch,
        Guid? productId,
        Func<ShoppingCart, Product?, DateTimeOffset, IReadOnlyDictionary<Guid, Product>, DomainError?> change,
        CancellationToken cancellationToken)
    {
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var cart = await db.LockCartAsync(cartId, userId, cancellationToken);
        if (cart is null)
        {
            return DomainError.NotFound("Cart");
        }

        if (ifMatch is { } expected && expected != cart.Version)
        {
            return new DomainError(ErrorCodes.CartVersionMismatch, "The cart changed since you loaded it.",
                new Dictionary<string, object?> { ["currentVersion"] = cart.Version });
        }

        var ids = cart.Items.Select(i => i.ProductId).Append(productId ?? Guid.Empty).Distinct().ToList();
        var prices = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        Product? product = null;
        if (productId is { } pid && !prices.TryGetValue(pid, out product))
        {
            return new DomainError(ErrorCodes.ProductNotFound, "Product not found.", new Dictionary<string, object?> { ["productId"] = pid });
        }

        if (change(cart, product, clock.GetUtcNow(), prices) is { } error)
        {
            return error;
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var name = (await MerchantNamesAsync([cart.MerchantId], cancellationToken))[cart.MerchantId];
        return cart.ToDto(name, HashOf(cart, name));
    }

    /// <summary>BR-32: at most 5 OPEN carts; the oldest is auto-ABANDONED on overflow.</summary>
    private async Task AbandonOverflowAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = await db.Carts.Where(c => c.UserId == userId && c.Status == CartStatus.Open)
            .OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken);
        foreach (var cart in open.Take(Math.Max(0, open.Count - (ShoppingCart.MaxOpenCartsPerUser - 1))))
        {
            cart.Abandon(now);
        }
    }

    private async Task<Dictionary<Guid, string>> MerchantNamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return await db.Merchants.AsNoTracking().Where(m => list.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);
    }

    /// <summary>The snapshot hash depends only on frozen cart fields, so availability is not needed here.</summary>
    public static string HashOf(ShoppingCart cart, string merchantName) =>
        cart.ToSnapshot(merchantName, new Dictionary<Guid, Product>()).ComputeHash();
}
