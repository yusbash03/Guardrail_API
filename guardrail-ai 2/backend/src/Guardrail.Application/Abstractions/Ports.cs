using System.Net;
using Guardrail.Domain.Catalogue;
using Guardrail.Domain.Users;

namespace Guardrail.Application.Abstractions;

public interface ICurrentUser
{
    Guid? UserId { get; }

    string? CorrelationId { get; }

    IPAddress? IpAddress { get; }

    string? UserAgent { get; }

    Guid RequireUserId() => UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
}

public interface IPasswordService
{
    string Hash(User user, string password);

    bool Verify(User user, string password);
}

public sealed record IssuedTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public interface ITokenService
{
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user);

    /// <summary>256-bit random opaque token (SEC-03).</summary>
    string CreateRefreshToken();

    string HashRefreshToken(string token);

    TimeSpan RefreshTokenLifetime { get; }

    /// <summary>How long a just-rotated refresh token may be presented again as a retry before it counts as reuse (SEC-03).</summary>
    TimeSpan RefreshReuseGrace { get; }
}

public sealed record CatalogueSearch(
    string? Query,
    string? CategoryCode,
    Guid? MerchantId,
    decimal? MaxUnitPrice,
    IReadOnlyCollection<Guid>? OnlyMerchantIds,
    int Skip,
    int Take);

public sealed record CatalogueSearchResult(IReadOnlyList<Product> Products, int TotalCount);

/// <summary>Catalogue search port (seeded catalogue; ADR-019 allows an external reference provider later).</summary>
public interface ICatalogueProvider
{
    Task<CatalogueSearchResult> SearchAsync(CatalogueSearch search, CancellationToken cancellationToken);
}
