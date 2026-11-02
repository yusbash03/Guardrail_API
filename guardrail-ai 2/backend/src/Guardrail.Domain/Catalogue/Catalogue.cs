using Guardrail.Domain.Common;

namespace Guardrail.Domain.Catalogue;

public sealed class Category
{
    private Category()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public int SortOrder { get; private set; }

    public static Category Create(string code, string name, string? description, int sortOrder) =>
        new() { Code = code, Name = name, Description = description, SortOrder = sortOrder };
}

public sealed class Merchant
{
    private Merchant()
    {
    }

    public Guid Id { get; private set; }

    public string Slug { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string? LogoUrl { get; private set; }

    public string[] CategoryCodes { get; private set; } = [];

    public string ProviderKey { get; private set; } = "SEED";

    public string? ProviderMerchantRef { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Merchant Create(Guid id, string slug, string name, string? description, string? logoUrl, string[] categoryCodes, DateTimeOffset now) =>
        new()
        {
            Id = id,
            Slug = slug,
            Name = name,
            Description = description,
            LogoUrl = logoUrl,
            CategoryCodes = categoryCodes,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
}

public sealed class Product
{
    private Product()
    {
    }

    public Guid Id { get; private set; }

    public Guid MerchantId { get; private set; }

    public Merchant? Merchant { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string? Brand { get; private set; }

    public string CategoryCode { get; private set; } = string.Empty;

    public string[] Tags { get; private set; } = [];

    public decimal UnitPrice { get; private set; }

    public string Currency { get; private set; } = MoneyFormat.DefaultCurrency;

    public string? UnitLabel { get; private set; }

    public string? ImageUrl { get; private set; }

    public bool IsAvailable { get; private set; }

    public int StockQuantity { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Product Create(
        Guid id,
        Guid merchantId,
        string sku,
        string name,
        string? description,
        string? brand,
        string categoryCode,
        string[] tags,
        decimal unitPrice,
        string? unitLabel,
        int stockQuantity,
        DateTimeOffset now,
        bool isAvailable = true) => new()
    {
        Id = id,
        MerchantId = merchantId,
        Sku = sku,
        Name = name,
        Description = description,
        Brand = brand,
        CategoryCode = categoryCode,
        Tags = tags,
        UnitPrice = unitPrice,
        Currency = MoneyFormat.DefaultCurrency,
        UnitLabel = unitLabel,
        ImageUrl = $"/images/products/{sku.ToLowerInvariant()}.webp",
        IsAvailable = isAvailable,
        StockQuantity = stockQuantity,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public bool CanFulfil(int quantity) => IsAvailable && StockQuantity >= quantity;

    public void SetAvailability(bool isAvailable, int stockQuantity, DateTimeOffset now)
    {
        IsAvailable = isAvailable;
        StockQuantity = stockQuantity;
        UpdatedAt = now;
    }
}
