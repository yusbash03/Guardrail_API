using Guardrail.Domain.Catalogue;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;

namespace Guardrail.Domain.Shopping;

public sealed class ShoppingSession
{
    private ShoppingSession()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid? ConversationId { get; private set; }

    public ShoppingSessionStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public static ShoppingSession Start(Guid userId, Guid? conversationId, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        UserId = userId,
        ConversationId = conversationId,
        Status = ShoppingSessionStatus.Active,
        CreatedAt = now,
    };
}

public sealed class ShoppingCart
{
    public const int MaxDistinctItems = 30; // BR-33
    public const int MaxQuantity = 99;
    public const int MaxOpenCartsPerUser = 5; // BR-32

    private readonly List<CartItem> _items = [];

    private ShoppingCart()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid? ShoppingSessionId { get; private set; }

    public Guid MerchantId { get; private set; }

    public string Currency { get; private set; } = MoneyFormat.DefaultCurrency;

    public CartStatus Status { get; private set; }

    public int Version { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal Total { get; private set; }

    public ActorType CreatedByActor { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<CartItem> Items => _items;

    public static ShoppingCart Create(Guid userId, Merchant merchant, ActorType actor, Guid? sessionId, DateTimeOffset now)
    {
        if (actor is not (ActorType.User or ActorType.Agent))
        {
            throw new ArgumentOutOfRangeException(nameof(actor));
        }

        return new ShoppingCart
        {
            Id = Ids.New(),
            UserId = userId,
            ShoppingSessionId = sessionId,
            MerchantId = merchant.Id,
            Currency = MoneyFormat.DefaultCurrency,
            Status = CartStatus.Open,
            Version = 1,
            CreatedByActor = actor,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Adds (or increases) a line. Returns an error instead of throwing for expected business failures.</summary>
    public DomainError? AddItem(Product product, int quantity, DateTimeOffset now, IReadOnlyDictionary<Guid, Product> currentPrices) =>
        Mutate(now, currentPrices, () =>
        {
            var existing = _items.FirstOrDefault(i => i.ProductId == product.Id);
            var newQuantity = (existing?.Quantity ?? 0) + quantity;
            if (ValidateLine(product, newQuantity) is { } error)
            {
                return error;
            }

            if (existing is null)
            {
                if (_items.Count >= MaxDistinctItems)
                {
                    return new DomainError(ErrorCodes.CartLimitExceeded, $"A cart can contain at most {MaxDistinctItems} distinct items.");
                }

                _items.Add(CartItem.Create(Id, product, newQuantity, now));
            }
            else
            {
                existing.SetQuantity(newQuantity);
            }

            return null;
        });

    public DomainError? SetQuantity(Product product, int quantity, DateTimeOffset now, IReadOnlyDictionary<Guid, Product> currentPrices) =>
        Mutate(now, currentPrices, () =>
        {
            var existing = _items.FirstOrDefault(i => i.ProductId == product.Id);
            if (existing is null)
            {
                return DomainError.NotFound("Cart item");
            }

            if (ValidateLine(product, quantity) is { } error)
            {
                return error;
            }

            existing.SetQuantity(quantity);
            return null;
        });

    public DomainError? RemoveItem(Guid productId, DateTimeOffset now, IReadOnlyDictionary<Guid, Product> currentPrices) =>
        Mutate(now, currentPrices, () =>
        {
            var existing = _items.FirstOrDefault(i => i.ProductId == productId);
            if (existing is null)
            {
                return DomainError.NotFound("Cart item");
            }

            _items.Remove(existing);
            return null;
        });

    /// <summary>
    /// Version is NOT incremented (DOMAIN_MODEL.md): the purchase and approval bind to the evaluated
    /// version, and the snapshot hash includes it.
    /// </summary>
    public void Lock(DateTimeOffset now)
    {
        EnsureStatus(CartStatus.Open, CartStatus.Locked);
        Status = CartStatus.Locked;
        UpdatedAt = now;
    }

    /// <summary>BR-31: only via purchase cancellation/rejection/expiry before checkout.</summary>
    public void Unlock(DateTimeOffset now)
    {
        EnsureStatus(CartStatus.Locked, CartStatus.Open);
        Status = CartStatus.Open;
        Version++;
        UpdatedAt = now;
    }

    public void Abandon(DateTimeOffset now)
    {
        if (Status is CartStatus.CheckedOut or CartStatus.Abandoned)
        {
            return;
        }

        Status = CartStatus.Abandoned;
        UpdatedAt = now;
    }

    public void CheckOut(DateTimeOffset now)
    {
        EnsureStatus(CartStatus.Locked, CartStatus.CheckedOut);
        Status = CartStatus.CheckedOut;
        UpdatedAt = now;
    }

    /// <summary>Snapshot from frozen cart line prices plus availability read from the database now.</summary>
    public CartSnapshot ToSnapshot(string merchantName, IReadOnlyDictionary<Guid, Product> products) =>
        new(
            Id,
            Version,
            MerchantId,
            merchantName,
            Currency,
            _items.OrderBy(i => i.ProductId.ToString("D"), StringComparer.Ordinal).Select(i =>
            {
                products.TryGetValue(i.ProductId, out var product);
                return new CartSnapshotItem(
                    i.ProductId, i.Sku, i.ProductName, i.CategoryCode, product?.Tags ?? [], i.UnitPrice, i.Quantity, i.LineTotal,
                    product?.IsAvailable ?? false, product?.StockQuantity ?? 0);
            }).ToList(),
            Total);

    private DomainError? Mutate(DateTimeOffset now, IReadOnlyDictionary<Guid, Product> currentPrices, Func<DomainError?> change)
    {
        if (Status != CartStatus.Open)
        {
            return Status == CartStatus.Locked
                ? new DomainError(ErrorCodes.CartLocked, "This cart is locked by an active purchase.")
                : new DomainError(ErrorCodes.CartNotOpen, $"This cart is {EnumText.ToText(Status)}.");
        }

        var error = change();
        if (error is not null)
        {
            return error;
        }

        // DOMAIN_MODEL.md: each change increments Version and recalculates totals from current product prices (BR-23).
        foreach (var item in _items)
        {
            if (currentPrices.TryGetValue(item.ProductId, out var product))
            {
                item.Reprice(product.UnitPrice);
            }
        }

        Subtotal = MoneyFormat.Round(_items.Sum(i => i.LineTotal));
        Total = Subtotal;
        Version++;
        UpdatedAt = now;
        return null;
    }

    private DomainError? ValidateLine(Product product, int quantity)
    {
        if (product.MerchantId != MerchantId)
        {
            return new DomainError(ErrorCodes.MixedMerchantCart, "A cart can only contain products from one merchant.");
        }

        if (product.Currency != Currency)
        {
            return new DomainError(ErrorCodes.CurrencyMismatch, "Product currency differs from the cart currency.");
        }

        if (quantity is < 1 or > MaxQuantity)
        {
            return DomainError.Validation($"Quantity must be between 1 and {MaxQuantity}.");
        }

        if (!product.CanFulfil(quantity))
        {
            return new DomainError(ErrorCodes.ProductUnavailable, $"{product.Name} is unavailable in that quantity.",
                new Dictionary<string, object?> { ["productId"] = product.Id, ["stockQuantity"] = product.StockQuantity });
        }

        return null;
    }

    private void EnsureStatus(CartStatus expected, CartStatus target)
    {
        if (Status != expected)
        {
            throw DomainException.InvalidTransition("Cart", EnumText.ToText(Status), EnumText.ToText(target));
        }
    }
}

public sealed class CartItem
{
    private CartItem()
    {
    }

    public Guid Id { get; private set; }

    public Guid CartId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string ProductName { get; private set; } = string.Empty;

    public string CategoryCode { get; private set; } = string.Empty;

    public decimal UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    public decimal LineTotal { get; private set; }

    public string Currency { get; private set; } = MoneyFormat.DefaultCurrency;

    public DateTimeOffset CreatedAt { get; private set; }

    internal static CartItem Create(Guid cartId, Product product, int quantity, DateTimeOffset now)
    {
        var item = new CartItem
        {
            Id = Ids.New(),
            CartId = cartId,
            ProductId = product.Id,
            Sku = product.Sku,
            ProductName = product.Name,
            CategoryCode = product.CategoryCode,
            Currency = product.Currency,
            CreatedAt = now,
        };
        item.Quantity = quantity;
        item.Reprice(product.UnitPrice);
        return item;
    }

    internal void SetQuantity(int quantity)
    {
        Quantity = quantity;
        LineTotal = MoneyFormat.Round(UnitPrice * Quantity);
    }

    internal void Reprice(decimal unitPrice)
    {
        UnitPrice = unitPrice;
        LineTotal = MoneyFormat.Round(UnitPrice * Quantity);
    }
}
