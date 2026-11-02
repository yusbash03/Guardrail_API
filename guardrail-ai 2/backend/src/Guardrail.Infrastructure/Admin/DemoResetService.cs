using Guardrail.Application.Abstractions;
using Guardrail.Application.Auth;
using Guardrail.Domain.Common;
using Guardrail.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Guardrail.Infrastructure.Admin;

/// <summary>
/// POST /admin/demo/reset: deletes the demo user's data and re-provisions policy v1 (DEPLOYMENT_GUIDE.md §6).
/// This is the only code path that deletes from append-only tables, and only for the configured demo account.
/// </summary>
public sealed class DemoResetService(AppDbContext db, UserProvisioner provisioner, IOptions<SeedOptions> options)
{
    public async Task<bool> ResetAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.DemoUserPassword))
        {
            return false;
        }

        var email = settings.DemoUserEmail.Trim().ToLowerInvariant();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(cancellationToken);
        if (userId is { } u)
        {
            string[] statements =
            [
                "update paypal_webhook_events set related_payment_id = null where related_payment_id in (select id from payments where user_id = {0})",
                "delete from spending_ledger_entries where user_id = {0}",
                "delete from payments where user_id = {0}",
                "delete from approval_requests where user_id = {0}",
                "delete from purchases where user_id = {0}",
                "delete from policy_evaluations where user_id = {0}",
                "delete from shopping_carts where user_id = {0}",
                "delete from shopping_sessions where user_id = {0}",
                "delete from agent_executions where user_id = {0}",
                "delete from agent_messages where user_id = {0}",
                "delete from agent_conversations where user_id = {0}",
                "delete from idempotency_keys where user_id = {0}",
                "delete from audit_logs where user_id = {0}",
                "delete from refresh_tokens where user_id = {0}",
                "update spending_profiles set current_policy_version_id = null where user_id = {0}",
                "delete from spending_policies where user_id = {0}",
                "delete from policy_versions where user_id = {0}",
                "delete from spending_profiles where user_id = {0}",
                "delete from users where id = {0}",
            ];
            foreach (var sql in statements)
            {
#pragma warning disable EF1002 // constant SQL with a positional parameter; no user input is concatenated (SEC-51)
                await db.Database.ExecuteSqlRawAsync(sql, [u], cancellationToken);
#pragma warning restore EF1002
            }
        }

        db.ChangeTracker.Clear();
        await provisioner.CreateAsync(email, settings.DemoUserPassword, "Amara", settings.DemoUserTimeZone, UserRole.User, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }
}
