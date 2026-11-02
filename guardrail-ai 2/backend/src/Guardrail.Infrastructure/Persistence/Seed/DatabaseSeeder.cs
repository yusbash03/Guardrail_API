using Guardrail.Application.Abstractions;
using Guardrail.Application.Auth;
using Guardrail.Domain.Catalogue;
using Guardrail.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Guardrail.Infrastructure.Persistence.Seed;

/// <summary>Idempotent seeding of the catalogue and optional demo/admin users (DATABASE_SCHEMA.md §Seed data).</summary>
public sealed class DatabaseSeeder(
    AppDbContext db,
    UserProvisioner provisioner,
    IOptions<SeedOptions> options,
    TimeProvider clock,
    ILogger<DatabaseSeeder> log)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.Catalogue)
        {
            await SeedCatalogueAsync(cancellationToken);
        }

        if (settings.CreateDemoUser)
        {
            await EnsureUserAsync(settings.DemoUserEmail, settings.DemoUserPassword, "Amara", settings.DemoUserTimeZone, UserRole.User, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(settings.AdminEmail))
        {
            await EnsureUserAsync(settings.AdminEmail, settings.AdminPassword, "Admin", "UTC", UserRole.Admin, cancellationToken);
        }
    }

    public async Task SeedCatalogueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var categories = await db.Categories.Select(c => c.Code).ToListAsync(cancellationToken);
        var order = 0;
        foreach (var (code, name, description) in CatalogueSeed.Categories)
        {
            order++;
            if (!categories.Contains(code))
            {
                db.Categories.Add(Category.Create(code, name, description, order));
            }
        }

        var merchants = await db.Merchants.Select(m => m.Id).ToListAsync(cancellationToken);
        foreach (var m in CatalogueSeed.Merchants.Where(m => !merchants.Contains(m.Id)))
        {
            db.Merchants.Add(Merchant.Create(m.Id, m.Slug, m.Name, m.Description, $"/images/merchants/{m.Slug}.svg", m.Categories, now));
        }

        await db.SaveChangesAsync(cancellationToken);

        var products = await db.Products.Select(p => p.Id).ToListAsync(cancellationToken);
        foreach (var p in CatalogueSeed.Products.Where(p => !products.Contains(p.Id)))
        {
            db.Products.Add(Product.Create(p.Id, p.MerchantId, p.Sku, p.Name, p.Description ?? $"{p.Brand} {p.Name}, {p.UnitLabel}.",
                p.Brand, p.Category, p.Tags, p.Price, p.UnitLabel, p.Stock, now, p.Available));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureUserAsync(
        string email, string? password, string displayName, string timeZone, UserRole role, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == normalized, cancellationToken))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            log.LogWarning("Seed user {Email} not created: no password configured", normalized);
            return;
        }

        await provisioner.CreateAsync(normalized, password, displayName, timeZone, role, cancellationToken);
        log.LogInformation("Seeded {Role} user", EnumText.ToText(role));
    }
}
