using Guardrail.Domain.Catalogue;
using Guardrail.Domain.Spending;
using Guardrail.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Guardrail.Infrastructure.Persistence.Configurations;

internal static class Money
{
    public static PropertyBuilder<decimal> AsMoney(this PropertyBuilder<decimal> p) => p.HasColumnType("numeric(14,2)");

    public static PropertyBuilder<decimal?> AsMoney(this PropertyBuilder<decimal?> p) => p.HasColumnType("numeric(14,2)");

    public static PropertyBuilder<string> AsCurrency(this PropertyBuilder<string> p) => p.HasColumnType("char(3)").IsRequired();

    public static PropertyBuilder<string> AsHash(this PropertyBuilder<string> p) => p.HasColumnType("char(64)").IsRequired();

    /// <summary>jsonb column; nullability follows the CLR property (NRT).</summary>
    public static PropertyBuilder<T> AsJson<T>(this PropertyBuilder<T> p) => p.HasColumnType("jsonb");

    /// <summary>Optimistic concurrency via PostgreSQL xmin (ADR-008).</summary>
    public static void HasXmin<T>(this EntityTypeBuilder<T> builder)
        where T : class =>
        builder.Property<uint>("RowVersion").IsRowVersion();
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users", t => t.HasCheckConstraint("ck_users_display_name", "length(display_name) between 1 and 80"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).HasColumnType("citext").IsRequired();
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.PasswordHash).IsRequired();
        b.Property(x => x.DisplayName).IsRequired();
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.AccessFailedCount).HasDefaultValue(0);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens");
        b.HasKey(x => x.Id);
        b.Property(x => x.TokenHash).AsHash();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.UserId);
        b.HasIndex(x => x.FamilyId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<RefreshToken>().WithMany().HasForeignKey(x => x.ReplacedById).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("categories");
        b.HasKey(x => x.Code);
        b.Property(x => x.Name).IsRequired();
    }
}

internal sealed class SpendingProfileConfiguration : IEntityTypeConfiguration<SpendingProfile>
{
    public void Configure(EntityTypeBuilder<SpendingProfile> b)
    {
        b.ToTable("spending_profiles");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.UserId).IsUnique();
        b.Property(x => x.Currency).AsCurrency().HasDefaultValue("USD");
        b.Property(x => x.TimeZone).IsRequired();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PolicyVersion>().WithMany().HasForeignKey(x => x.CurrentPolicyVersionId)
            .HasConstraintName("fk_profile_current_version").OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PolicyVersionConfiguration : IEntityTypeConfiguration<PolicyVersion>
{
    public void Configure(EntityTypeBuilder<PolicyVersion> b)
    {
        b.ToTable("policy_versions", t => t.HasCheckConstraint("ck_policy_versions_version_number", "version_number >= 1"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.SpendingProfileId, x.VersionNumber }).IsUnique();
        b.HasIndex(x => new { x.UserId, x.VersionNumber });
        b.Property(x => x.SnapshotJson).AsJson();
        b.Property(x => x.SnapshotHash).AsHash();
        b.Property(x => x.ChangeSummary).IsRequired();
        b.HasOne<SpendingProfile>().WithMany().HasForeignKey(x => x.SpendingProfileId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Rules).WithOne().HasForeignKey(r => r.PolicyVersionId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Rules).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class SpendingPolicyConfiguration : IEntityTypeConfiguration<SpendingPolicy>
{
    public void Configure(EntityTypeBuilder<SpendingPolicy> b)
    {
        b.ToTable("spending_policies");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.PolicyVersionId);
        b.Property(x => x.Parameters).AsJson().HasDefaultValueSql("'{}'::jsonb");
        b.Property(x => x.IsEnabled).HasDefaultValue(true);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryCode).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MerchantConfiguration : IEntityTypeConfiguration<Merchant>
{
    public void Configure(EntityTypeBuilder<Merchant> b)
    {
        b.ToTable("merchants");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.CategoryCodes).HasColumnType("text[]").HasDefaultValueSql("'{}'");
        b.Property(x => x.ProviderKey).HasDefaultValue("SEED");
        b.Property(x => x.IsActive).HasDefaultValue(true);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("products", t =>
        {
            t.HasCheckConstraint("ck_products_unit_price", "unit_price > 0");
            t.HasCheckConstraint("ck_products_stock_quantity", "stock_quantity >= 0");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.MerchantId, x.Sku }).IsUnique();
        b.HasIndex(x => new { x.CategoryCode, x.UnitPrice });
        b.HasIndex(x => x.MerchantId);
        b.Property(x => x.UnitPrice).AsMoney();
        b.Property(x => x.Currency).AsCurrency();
        b.Property(x => x.Tags).HasColumnType("text[]").HasDefaultValueSql("'{}'");
        b.Property(x => x.IsAvailable).HasDefaultValue(true);
        b.Property(x => x.StockQuantity).HasDefaultValue(0);
        b.HasOne(x => x.Merchant).WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryCode).OnDelete(DeleteBehavior.Restrict);

        // Full-text search (DATABASE_SCHEMA.md): generated tsvector + GIN index; shadow property so Domain stays provider-free.
        b.Property<NpgsqlTypes.NpgsqlTsVector>("SearchVector")
            .HasComputedColumnSql(
                "to_tsvector('english', coalesce(name,'') || ' ' || coalesce(brand,'') || ' ' || coalesce(description,''))", stored: true);
        b.HasIndex("SearchVector").HasMethod("GIN");
    }
}
