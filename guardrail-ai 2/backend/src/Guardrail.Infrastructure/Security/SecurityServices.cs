using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Guardrail.Application.Abstractions;
using Guardrail.Domain.Common;
using Guardrail.Domain.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Guardrail.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    /// <summary>≥ 64 random bytes, base64 (HS256).</summary>
    [Required]
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 7;

    public byte[] KeyBytes()
    {
        try
        {
            return Convert.FromBase64String(SigningKey);
        }
        catch (FormatException)
        {
            return Encoding.UTF8.GetBytes(SigningKey);
        }
    }
}

public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options) =>
        string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience)
            ? ValidateOptionsResult.Fail("Jwt:Issuer and Jwt:Audience are required.")
            : options.KeyBytes().Length < 64
                ? ValidateOptionsResult.Fail("Jwt:SigningKey must be at least 64 bytes (base64).")
                : ValidateOptionsResult.Success;
}

/// <summary>SEC-02 access tokens and SEC-03 opaque refresh tokens.</summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(options.Value.RefreshTokenDays);

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(settings.AccessTokenMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("role", EnumText.ToText(user.Role)),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            claims,
            now.UtcDateTime,
            expires.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(settings.KeyBytes()), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public string CreateRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public string HashRefreshToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>SEC-01: ASP.NET Core Identity PBKDF2 hasher (constant-time verification).</summary>
public sealed class PasswordService : IPasswordService
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    public bool Verify(User user, string password) =>
        !string.IsNullOrEmpty(user.PasswordHash) && _hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
}
