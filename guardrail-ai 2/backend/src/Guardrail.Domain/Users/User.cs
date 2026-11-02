using System.Net;
using Guardrail.Domain.Common;

namespace Guardrail.Domain.Users;

public sealed class User
{
    private User()
    {
    }

    public Guid Id { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public UserRole Role { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    /// <summary>SEC-05 lockout state (addition to the documented schema).</summary>
    public int AccessFailedCount { get; private set; }

    public DateTimeOffset? LockoutEnd { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static User Create(string email, string displayName, UserRole role, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        Email = email.Trim().ToLowerInvariant(),
        DisplayName = displayName.Trim(),
        Role = role,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void SetPasswordHash(string hash, DateTimeOffset now)
    {
        PasswordHash = hash;
        UpdatedAt = now;
    }

    public bool IsLockedOut(DateTimeOffset now) => LockoutEnd is { } end && end > now;

    public void RecordLoginSuccess(DateTimeOffset now)
    {
        AccessFailedCount = 0;
        LockoutEnd = null;
        LastLoginAt = now;
        UpdatedAt = now;
    }

    /// <summary>5 failures → 5-minute lockout (SEC-05). Approval step-up failures count too (ADR-015).</summary>
    public void RecordAccessFailure(DateTimeOffset now, int maxFailures, TimeSpan lockoutDuration)
    {
        AccessFailedCount++;
        if (AccessFailedCount >= maxFailures)
        {
            LockoutEnd = now.Add(lockoutDuration);
            AccessFailedCount = 0;
        }

        UpdatedAt = now;
    }
}

public sealed class RefreshToken
{
    private RefreshToken()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid FamilyId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? ReplacedById { get; private set; }

    public IPAddress? CreatedIp { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ReplacedById is null && ExpiresAt > now;

    public static RefreshToken Issue(Guid userId, Guid familyId, string tokenHash, DateTimeOffset expiresAt, IPAddress? ip, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        UserId = userId,
        FamilyId = familyId,
        TokenHash = tokenHash,
        ExpiresAt = expiresAt,
        CreatedIp = ip,
        CreatedAt = now,
    };

    public void ReplaceWith(RefreshToken next, DateTimeOffset now)
    {
        ReplacedById = next.Id;
        RevokedAt ??= now;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
