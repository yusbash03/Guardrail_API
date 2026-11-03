using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Guardrail.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef` only. Pass --connection to target a real database (DEPLOYMENT_GUIDE.md §1).</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                         ?? "Host=localhost;Port=5432;Database=guardrail;Username=guardrail;Password=guardrail";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresConnectionString.Normalize(connection))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }
}
