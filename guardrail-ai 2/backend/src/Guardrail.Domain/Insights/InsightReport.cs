using Guardrail.Domain.Common;

namespace Guardrail.Domain.Insights;

/// <summary>A user's saved AG Studio report (AG_GRID_USAGE.md §2.3): Studio state as returned by api.getState().</summary>
public sealed class InsightReport
{
    private InsightReport()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Opaque Studio state (JSON). Layout and widget configuration only, never money data.</summary>
    public string State { get; private set; } = "{}";

    public string? StateVersion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static InsightReport Create(Guid userId, string name, string state, string? stateVersion, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        UserId = userId,
        Name = name,
        State = state,
        StateVersion = stateVersion,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Update(string state, string? stateVersion, DateTimeOffset now)
    {
        State = state;
        StateVersion = stateVersion;
        UpdatedAt = now;
    }
}
