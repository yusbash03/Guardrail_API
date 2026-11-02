using Guardrail.Domain.Common;

namespace Guardrail.Domain.Spending;

public sealed class SpendingProfile
{
    private SpendingProfile()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Currency { get; private set; } = MoneyFormat.DefaultCurrency;

    public string TimeZone { get; private set; } = "UTC";

    public Guid? CurrentPolicyVersionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static SpendingProfile Create(Guid userId, string timeZone, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        UserId = userId,
        Currency = MoneyFormat.DefaultCurrency,
        TimeZone = timeZone,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void SetTimeZone(string timeZone, DateTimeOffset now)
    {
        TimeZone = timeZone;
        UpdatedAt = now;
    }

    public void ActivateVersion(PolicyVersion version, DateTimeOffset now)
    {
        if (version.SpendingProfileId != Id)
        {
            throw new DomainException(DomainError.InvalidState("Policy version belongs to another profile."));
        }

        CurrentPolicyVersionId = version.Id;
        UpdatedAt = now;
    }
}

/// <summary>Immutable rule set (ADR-004). Editing creates version n+1.</summary>
public sealed class PolicyVersion
{
    private readonly List<SpendingPolicy> _rules = [];

    private PolicyVersion()
    {
    }

    public Guid Id { get; private set; }

    public Guid SpendingProfileId { get; private set; }

    public Guid UserId { get; private set; }

    public int VersionNumber { get; private set; }

    public string SnapshotJson { get; private set; } = "{}";

    public string SnapshotHash { get; private set; } = string.Empty;

    public string ChangeSummary { get; private set; } = string.Empty;

    public ActorType CreatedByActor { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<SpendingPolicy> Rules => _rules;

    public static PolicyVersion Create(
        SpendingProfile profile,
        int versionNumber,
        Func<Guid, IEnumerable<SpendingPolicy>> buildRules,
        string snapshotJson,
        string snapshotHash,
        string changeSummary,
        ActorType actor,
        DateTimeOffset now)
    {
        if (actor is not (ActorType.User or ActorType.System))
        {
            throw new DomainException(DomainError.InvalidState("Only the user or the system can create policy versions (BR-04)."));
        }

        var version = new PolicyVersion
        {
            Id = Ids.New(),
            SpendingProfileId = profile.Id,
            UserId = profile.UserId,
            VersionNumber = versionNumber,
            SnapshotJson = snapshotJson,
            SnapshotHash = snapshotHash,
            ChangeSummary = changeSummary,
            CreatedByActor = actor,
            CreatedAt = now,
        };
        version._rules.AddRange(buildRules(version.Id));
        return version;
    }
}

/// <summary>One rule row (spending_policies). Immutable once its version exists.</summary>
public sealed class SpendingPolicy
{
    private SpendingPolicy()
    {
    }

    public Guid Id { get; private set; }

    public Guid PolicyVersionId { get; private set; }

    public Guid UserId { get; private set; }

    public PolicyRuleType RuleType { get; private set; }

    public string? CategoryCode { get; private set; }

    public Guid? MerchantId { get; private set; }

    public Guid? ProductId { get; private set; }

    /// <summary>jsonb parameters; schema per rule type (SPENDING_POLICY_ENGINE.md §1.1 mapping table).</summary>
    public string Parameters { get; private set; } = "{}";

    public bool IsEnabled { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }

    public static SpendingPolicy Create(
        Guid policyVersionId,
        Guid userId,
        PolicyRuleType ruleType,
        string parameters,
        DateTimeOffset now,
        string? categoryCode = null,
        Guid? merchantId = null,
        Guid? productId = null,
        bool isEnabled = true) => new()
    {
        Id = Ids.New(),
        PolicyVersionId = policyVersionId,
        UserId = userId,
        RuleType = ruleType,
        CategoryCode = categoryCode,
        MerchantId = merchantId,
        ProductId = productId,
        Parameters = parameters,
        IsEnabled = isEnabled,
        CreatedAt = now,
    };
}
