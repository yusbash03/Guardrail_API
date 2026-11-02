using Guardrail.Application.Abstractions;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Common;

namespace Guardrail.Application.Common;

/// <summary>Adds audit rows to the current unit of work (BR-90). Caller saves.</summary>
public sealed class AuditWriter(IAppDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public void Write(
        string action,
        string entityType,
        Guid? entityId,
        Guid? userId,
        ActorType actor,
        object? data = null,
        string? beforeHash = null,
        string? afterHash = null)
    {
        var actorId = actor switch
        {
            ActorType.User or ActorType.Agent => userId?.ToString(),
            ActorType.Paypal => "paypal",
            _ => "system",
        };

        db.AuditLogs.Add(AuditLog.Create(
            userId,
            actor,
            actorId,
            action,
            entityType,
            entityId,
            data is null ? "{}" : JsonDefaults.Serialize(data),
            clock.GetUtcNow(),
            currentUser.CorrelationId,
            currentUser.IpAddress,
            currentUser.UserAgent,
            beforeHash,
            afterHash));
    }
}
