using Guardrail.Application.Abstractions;
using Guardrail.Domain.Policy;
using Guardrail.Domain.Spending;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Policies;

public sealed record CurrentPolicy(SpendingProfile Profile, PolicyVersion Version, PolicySnapshot Snapshot);

public sealed class PolicyLoader(IAppDbContext db)
{
    /// <summary>Loads the profile and its active version. When the profile is already tracked (e.g. locked), that instance is reused.</summary>
    public async Task<CurrentPolicy?> LoadCurrentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profile = db.SpendingProfiles.Local.FirstOrDefault(p => p.UserId == userId)
                      ?? await db.SpendingProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (profile?.CurrentPolicyVersionId is not { } versionId)
        {
            return null;
        }

        var version = await LoadVersionAsync(versionId, cancellationToken);
        return version is null ? null : new CurrentPolicy(profile, version, PolicyMapper.ToSnapshot(version, profile.Currency, profile.TimeZone));
    }

    public Task<PolicyVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        db.PolicyVersions.AsNoTracking().Include(v => v.Rules).SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken);

    public async Task<PolicySnapshot?> LoadSnapshotAsync(Guid userId, int versionNumber, CancellationToken cancellationToken)
    {
        var profile = await db.SpendingProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var version = await db.PolicyVersions.AsNoTracking().Include(v => v.Rules)
            .SingleOrDefaultAsync(v => v.UserId == userId && v.VersionNumber == versionNumber, cancellationToken);
        return version is null ? null : PolicyMapper.ToSnapshot(version, profile.Currency, profile.TimeZone);
    }
}
