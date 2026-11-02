using Guardrail.Application.Abstractions;
using Guardrail.Domain.Agent;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Authorization;
using Guardrail.Domain.Catalogue;
using Guardrail.Domain.Common;
using Guardrail.Domain.Ledger;
using Guardrail.Domain.Payments;
using Guardrail.Domain.Shopping;
using Guardrail.Domain.Spending;
using Guardrail.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Guardrail.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<SpendingProfile> SpendingProfiles => Set<SpendingProfile>();

    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();

    public DbSet<SpendingPolicy> SpendingPolicies => Set<SpendingPolicy>();

    public DbSet<Merchant> Merchants => Set<Merchant>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ShoppingSession> ShoppingSessions => Set<ShoppingSession>();

    public DbSet<ShoppingCart> Carts => Set<ShoppingCart>();

    public DbSet<CartItem> CartItems => Set<CartItem>();

    public DbSet<PolicyEvaluation> PolicyEvaluations => Set<PolicyEvaluation>();

    public DbSet<Purchase> Purchases => Set<Purchase>();

    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PayPalWebhookEvent> WebhookEvents => Set<PayPalWebhookEvent>();

    public DbSet<SpendingLedgerEntry> LedgerEntries => Set<SpendingLedgerEntry>();

    public DbSet<AgentConversation> Conversations => Set<AgentConversation>();

    public DbSet<AgentMessage> AgentMessages => Set<AgentMessage>();

    public DbSet<AgentExecution> AgentExecutions => Set<AgentExecution>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        Database.BeginTransactionAsync(cancellationToken);

    public void ClearTracking() => ChangeTracker.Clear();

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) => Database.CanConnectAsync(cancellationToken);

    // ------------------------------------------------------------------ row locks (ADR-008)

    public async Task<SpendingProfile?> LockProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        await Database.ExecuteSqlInterpolatedAsync($"select 1 from spending_profiles where user_id = {userId} for update", cancellationToken);
        return await Fresh(SpendingProfiles, p => p.UserId == userId, cancellationToken);
    }

    public async Task<ShoppingCart?> LockCartAsync(Guid cartId, Guid userId, CancellationToken cancellationToken)
    {
        await Database.ExecuteSqlInterpolatedAsync(
            $"select 1 from shopping_carts where id = {cartId} and user_id = {userId} for update", cancellationToken);
        return await Fresh(Carts.Include(c => c.Items), c => c.Id == cartId && c.UserId == userId, cancellationToken);
    }

    public async Task<Purchase?> LockPurchaseAsync(Guid purchaseId, Guid? userId, CancellationToken cancellationToken)
    {
        await Database.ExecuteSqlInterpolatedAsync($"select 1 from purchases where id = {purchaseId} for update", cancellationToken);
        return await Fresh(Purchases, p => p.Id == purchaseId && (userId == null || p.UserId == userId), cancellationToken);
    }

    public async Task<ApprovalRequest?> LockApprovalAsync(Guid approvalId, Guid? userId, CancellationToken cancellationToken)
    {
        await Database.ExecuteSqlInterpolatedAsync($"select 1 from approval_requests where id = {approvalId} for update", cancellationToken);
        return await Fresh(ApprovalRequests, a => a.Id == approvalId && (userId == null || a.UserId == userId), cancellationToken);
    }

    public async Task<bool> TryConsumeApprovalAsync(
        Guid approvalId, Guid userId, string currentCartHash, decimal purchaseAmount, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await Database.ExecuteSqlInterpolatedAsync($"""
            update approval_requests
               set status = 'CONSUMED', consumed_at = {now}, updated_at = {now}
             where id = {approvalId} and user_id = {userId} and status = 'APPROVED'
               and expires_at > {now} and consumed_at is null
               and cart_snapshot_hash = {currentCartHash} and amount = {purchaseAmount}
            """, cancellationToken);
        return rows == 1;
    }

    // ------------------------------------------------------------------ job claiming (FOR UPDATE SKIP LOCKED)

    public async Task<IReadOnlyList<Guid>> ClaimPurchasesWithExpiredApprovalsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await Database.SqlQuery<Guid>($"""
            select p.id as "Value" from purchases p
              join approval_requests a on a.purchase_id = p.id
             where a.status in ('PENDING','APPROVED') and a.expires_at <= {now}
             order by a.expires_at
             limit {limit}
               for update of p skip locked
            """).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ClaimExpiredCheckoutPurchaseIdsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await Database.SqlQuery<Guid>($"""
            select id as "Value" from purchases
             where status in ('READY_FOR_CHECKOUT','CHECKOUT_PENDING')
               and checkout_expires_at is not null and checkout_expires_at <= {now}
             order by checkout_expires_at
             limit {limit}
               for update skip locked
            """).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ClaimReceivedWebhookIdsAsync(int limit, CancellationToken cancellationToken) =>
        await Database.SqlQuery<Guid>($"""
            select id as "Value" from paypal_webhook_events
             where status = 'RECEIVED'
             order by received_at
             limit {limit}
               for update skip locked
            """).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> FindPurchasesToReconcileAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken) =>
        await Database.SqlQuery<Guid>($"""
            select distinct p.id as "Value" from purchases p
              join payments pay on pay.purchase_id = p.id
             where pay.status in ('CREATED','PAYER_ACTION_REQUIRED','APPROVED','AUTHORIZED','CAPTURE_PENDING')
               and p.status in ('CHECKOUT_PENDING','PAYER_APPROVED','PAYMENT_AUTHORIZED','CAPTURE_PENDING')
               and pay.updated_at <= {olderThan}
             limit {limit}
            """).ToListAsync(cancellationToken);

    public async Task<bool> InsertWebhookEventIfNewAsync(PayPalWebhookEvent e, CancellationToken cancellationToken)
    {
        var rows = await Database.ExecuteSqlInterpolatedAsync($"""
            insert into paypal_webhook_events
              (id, paypal_event_id, event_type, resource_type, resource_id, transmission_id, verification_status, status,
               raw_body, headers, attempts, received_at)
            values ({e.Id}, {e.PaypalEventId}, {e.EventType}, {e.ResourceType}, {e.ResourceId}, {e.TransmissionId},
                    {EnumText.ToText(e.VerificationStatus)}, {EnumText.ToText(e.Status)}, {e.RawBody}, cast({e.Headers} as jsonb), 0, {e.ReceivedAt})
            on conflict (paypal_event_id) do nothing
            """, cancellationToken);
        return rows == 1;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplyEnumTextConvention(modelBuilder);

        // UUID v7 keys are generated by the application (CODING_STANDARDS.md). Without this, EF treats a new child entity
        // with a preset key (e.g. a CartItem added to a tracked cart) as existing and issues an UPDATE.
        foreach (var key in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetKeys()).SelectMany(k => k.Properties))
        {
            if (key.ClrType == typeof(Guid))
            {
                key.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
    }

    /// <summary>Enums are stored as UPPER_SNAKE text with a CHECK constraint listing DOMAIN_MODEL.md values.</summary>
    private static void ApplyEnumTextConvention(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties().Where(p => p.ClrType.IsEnum))
            {
                var converterType = typeof(EnumTextConverter<>).MakeGenericType(property.ClrType);
                property.SetValueConverter((ValueConverter)Activator.CreateInstance(converterType)!);
                property.SetColumnType("text");

                var table = entity.GetTableName()!;
                var column = property.GetColumnName();
                var values = (IReadOnlyList<string>)typeof(EnumText).GetMethod(nameof(EnumText.AllTexts))!
                    .MakeGenericMethod(property.ClrType).Invoke(null, null)!;
                entity.AddCheckConstraint($"ck_{table}_{column}", $"{column} in ({string.Join(",", values.Select(v => $"'{v}'"))})");
            }
        }
    }

    /// <summary>
    /// Loads after a row lock. If the entity was already tracked, EF would return the stale instance, so it is reloaded.
    /// </summary>
    private async Task<T?> Fresh<T>(IQueryable<T> query, System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct)
        where T : class
    {
        var alreadyTracked = ChangeTracker.Entries<T>().Select(e => e.Entity).ToHashSet();
        var entity = await query.SingleOrDefaultAsync(predicate, ct);
        if (entity is not null && alreadyTracked.Contains(entity))
        {
            await Entry(entity).ReloadAsync(ct);
        }

        return entity;
    }
}

public sealed class EnumTextConverter<TEnum>() : ValueConverter<TEnum, string>(v => EnumText.ToText(v), v => EnumText.Parse<TEnum>(v))
    where TEnum : struct, Enum;
