using System.Net;
using FluentValidation;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;
using Guardrail.Domain.Spending;
using Guardrail.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Guardrail.Application.Auth;

public sealed record RegisterCommand(string? Email, string? Password, string? DisplayName, string? TimeZone);

public sealed record LoginCommand(string? Email, string? Password);

/// <summary>Auth result plus the opaque refresh token the endpoint puts into the gr_rt cookie.</summary>
public sealed record AuthSession(AuthResponse Response, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(c => c.Password).NotEmpty().MinimumLength(10).MaximumLength(128) // SEC-01
            .Must(p => p is null || !CommonPasswords.Contains(p)).WithMessage("This password is too common.");
        RuleFor(c => c.DisplayName).NotEmpty().MaximumLength(80);
        RuleFor(c => c.TimeZone).Must(BudgetPeriods.IsValidTimeZone).WithMessage("Must be an IANA time zone, e.g. America/New_York.");
    }
}

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(c => c.Email).NotEmpty().MaximumLength(254);
        RuleFor(c => c.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class AuthService(
    IAppDbContext db,
    IPasswordService passwords,
    ITokenService tokens,
    UserProvisioner provisioner,
    RegisterValidator registerValidator,
    LoginValidator loginValidator,
    AuditWriter audit,
    IOptions<AuthOptions> options,
    TimeProvider clock)
{
    private static readonly DomainError InvalidCredentials = new(ErrorCodes.Unauthenticated, "Invalid email or password.");

    public async Task<Result<AuthSession>> RegisterAsync(RegisterCommand command, IPAddress? ip, CancellationToken cancellationToken)
    {
        if (await registerValidator.CheckAsync(command, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var email = command.Email!.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            return DomainError.Validation("Registration failed.", new Dictionary<string, string[]> { ["email"] = ["This email is already registered."] });
        }

        var user = await provisioner.CreateAsync(email, command.Password!, command.DisplayName!, command.TimeZone!, UserRole.User, cancellationToken);
        audit.Write(AuditActions.UserRegistered, "User", user.Id, user.Id, ActorType.User);
        return await IssueAsync(user, Guid.NewGuid(), ip, cancellationToken);
    }

    public async Task<Result<AuthSession>> LoginAsync(LoginCommand command, IPAddress? ip, CancellationToken cancellationToken)
    {
        if (await loginValidator.CheckAsync(command, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        var email = command.Email!.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return InvalidCredentials;
        }

        if (user.IsLockedOut(now))
        {
            return InvalidCredentials; // SEC-05: generic message, no lockout disclosure
        }

        if (!passwords.Verify(user, command.Password!))
        {
            user.RecordAccessFailure(now, options.Value.MaxFailedLogins, TimeSpan.FromMinutes(options.Value.LockoutMinutes));
            await db.SaveChangesAsync(cancellationToken);
            return InvalidCredentials;
        }

        user.RecordLoginSuccess(now);
        audit.Write(AuditActions.UserLoggedIn, "User", user.Id, user.Id, ActorType.User);
        return await IssueAsync(user, Guid.NewGuid(), ip, cancellationToken);
    }

    /// <summary>Rotation on every use; reuse of a rotated token revokes the whole family (SEC-03).</summary>
    public async Task<Result<AuthSession>> RefreshAsync(string? refreshToken, IPAddress? ip, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return new DomainError(ErrorCodes.Unauthenticated, "Missing refresh token.");
        }

        var now = clock.GetUtcNow();
        var hash = tokens.HashRefreshToken(refreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is null)
        {
            return new DomainError(ErrorCodes.Unauthenticated, "Invalid refresh token.");
        }

        if (token.ReplacedById is not null || token.RevokedAt is not null)
        {
            await RevokeFamilyAsync(token.FamilyId, now, cancellationToken);
            return new DomainError(ErrorCodes.Unauthenticated, "Refresh token reuse detected. Please sign in again.");
        }

        if (token.ExpiresAt <= now)
        {
            return new DomainError(ErrorCodes.Unauthenticated, "Session expired. Please sign in again.");
        }

        var user = await db.Users.SingleAsync(u => u.Id == token.UserId, cancellationToken);
        if (!user.IsActive)
        {
            return new DomainError(ErrorCodes.Unauthenticated, "Account disabled.");
        }

        return await IssueAsync(user, token.FamilyId, ip, cancellationToken, token);
    }

    public async Task LogoutAsync(Guid userId, string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var hash = tokens.HashRefreshToken(refreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, cancellationToken);
        if (token is not null)
        {
            await RevokeFamilyAsync(token.FamilyId, clock.GetUtcNow(), cancellationToken);
        }
    }

    public async Task<Result<UserDto>> MeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? DomainError.NotFound("User") : ToDto(user);
    }

    private async Task<Result<AuthSession>> IssueAsync(
        User user, Guid familyId, IPAddress? ip, CancellationToken cancellationToken, RefreshToken? replacing = null)
    {
        var now = clock.GetUtcNow();
        var (access, expiresAt) = tokens.CreateAccessToken(user);
        var raw = tokens.CreateRefreshToken();
        var refreshExpires = now.Add(tokens.RefreshTokenLifetime);
        var next = RefreshToken.Issue(user.Id, familyId, tokens.HashRefreshToken(raw), refreshExpires, ip, now);
        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(cancellationToken);
        if (replacing is not null)
        {
            replacing.ReplaceWith(next, now);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new AuthSession(new AuthResponse(access, expiresAt, ToDto(user)), raw, refreshExpires);
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var family = await db.RefreshTokens.Where(t => t.FamilyId == familyId).ToListAsync(cancellationToken);
        foreach (var t in family)
        {
            t.Revoke(now);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static UserDto ToDto(User user) => new(user.Id, user.Email, user.DisplayName, user.Role);
}

/// <summary>Creates a user with their spending profile and policy v1 (BR-12). Caller's unit of work is saved here.</summary>
public sealed class UserProvisioner(IAppDbContext db, IPasswordService passwords, TimeProvider clock)
{
    public async Task<User> CreateAsync(
        string email, string password, string displayName, string timeZone, UserRole role, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var user = User.Create(email, displayName, role, now);
        user.SetPasswordHash(passwords.Hash(user, password), now);
        db.Users.Add(user);

        var profile = SpendingProfile.Create(user.Id, timeZone, now);
        db.SpendingProfiles.Add(profile);

        var defaults = PolicyMapper.Defaults() with { Currency = profile.Currency };
        var (json, hash) = PolicyMapper.ContentSnapshot(defaults);
        var version = PolicyVersion.Create(profile, 1, id => PolicyMapper.ToRules(defaults, id, user.Id, now), json, hash,
            "Default policy (approval required for everything, new merchants blocked, alcohol blocked)", ActorType.System, now);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);

        profile.ActivateVersion(version, now);
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }
}

public sealed class ProfileService(IAppDbContext db, TimeProvider clock)
{
    public async Task<Result<SpendingProfileDto>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profile = await db.SpendingProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (profile is null)
        {
            return DomainError.NotFound("Spending profile");
        }

        var number = await db.PolicyVersions.Where(v => v.Id == profile.CurrentPolicyVersionId).Select(v => v.VersionNumber)
            .SingleOrDefaultAsync(cancellationToken);
        return new SpendingProfileDto(profile.Id, profile.Currency, profile.TimeZone, number);
    }

    public async Task<Result<SpendingProfileDto>> UpdateTimeZoneAsync(Guid userId, string? timeZone, CancellationToken cancellationToken)
    {
        if (!BudgetPeriods.IsValidTimeZone(timeZone))
        {
            return DomainError.Validation("Invalid time zone.", new Dictionary<string, string[]> { ["timeZone"] = ["Must be an IANA time zone."] });
        }

        var profile = await db.SpendingProfiles.SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (profile is null)
        {
            return DomainError.NotFound("Spending profile");
        }

        profile.SetTimeZone(timeZone!, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(userId, cancellationToken);
    }
}

/// <summary>
/// A short deny-list of very common passwords. SEC-01 asks for the top-10k list; drop a full list into this set
/// (e.g. from a published breach corpus) before going beyond the hackathon.
/// </summary>
internal static class CommonPasswords
{
    private static readonly HashSet<string> Set = new(StringComparer.OrdinalIgnoreCase)
    {
        "1234567890", "12345678910", "123456789a", "0123456789", "1111111111", "0000000000", "1234512345", "qwertyuiop",
        "qwerty1234", "1q2w3e4r5t", "password12", "password123", "password1234", "passw0rd123", "iloveyou12", "abcdefghij",
        "abcd123456", "abc1234567", "letmein123", "welcome123", "admin12345", "administrator", "qwerty123456", "1qaz2wsx3edc",
        "zaq12wsxcde", "football123", "baseball123", "princess123", "sunshine123", "superman123", "trustno1234", "monkey12345",
        "dragon12345", "master12345", "shadow12345", "michael1234", "jennifer123", "computer123", "internet123", "starwars123",
        "whatever123", "changeme123", "guardrail123", "paypal12345",
    };

    public static bool Contains(string password) => Set.Contains(password);
}
