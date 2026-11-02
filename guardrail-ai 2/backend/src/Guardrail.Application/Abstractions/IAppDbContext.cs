using Guardrail.Domain.Agent;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Authorization;
using Guardrail.Domain.Catalogue;
using Guardrail.Domain.Ledger;
using Guardrail.Domain.Payments;
using Guardrail.Domain.Shopping;
using Guardrail.Domain.Spending;
using Guardrail.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Guardrail.Application.Abstractions;

/// <summary>Unit of work over PostgreSQL. Row-lock helpers issue SELECT … FOR UPDATE (ADR-008).</summary>
public interface IAppDbContext
{
    DbSet<User> Users { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<Category> Categories { get; }

    DbSet<SpendingProfile> SpendingProfiles { get; }

    DbSet<PolicyVersion> PolicyVersions { get; }

    DbSet<SpendingPolicy> SpendingPolicies { get; }

    DbSet<Merchant> Merchants { get; }

    DbSet<Product> Products { get; }

    DbSet<ShoppingSession> ShoppingSessions { get; }

    DbSet<ShoppingCart> Carts { get; }

    DbSet<CartItem> CartItems { get; }

    DbSet<PolicyEvaluation> PolicyEvaluations { get; }

    DbSet<Purchase> Purchases { get; }

    DbSet<ApprovalRequest> ApprovalRequests { get; }

    DbSet<Payment> Payments { get; }

    DbSet<PayPalWebhookEvent> WebhookEvents { get; }

    DbSet<SpendingLedgerEntry> LedgerEntries { get; }

    DbSet<AgentConversation> Conversations { get; }

    DbSet<AgentMessage> AgentMessages { get; }

    DbSet<AgentExecution> AgentExecutions { get; }

    DbSet<AuditLog> AuditLogs { get; }

    DbSet<IdempotencyKey> IdempotencyKeys { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    void ClearTracking();

    /// <summary>Per-user serialization of budget-increasing writes (SEC-41).</summary>
    Task<SpendingProfile?> LockProfileAsync(Guid userId, CancellationToken cancellationToken);

    Task<ShoppingCart?> LockCartAsync(Guid cartId, Guid userId, CancellationToken cancellationToken);

    Task<Purchase?> LockPurchaseAsync(Guid purchaseId, Guid? userId, CancellationToken cancellationToken);

    Task<ApprovalRequest?> LockApprovalAsync(Guid approvalId, Guid? userId, CancellationToken cancellationToken);

    /// <summary>Atomic single-use consume (PAYMENT_STATE_MACHINE.md §2). Returns false when 0 rows were updated.</summary>
    Task<bool> TryConsumeApprovalAsync(
        Guid approvalId, Guid userId, string currentCartHash, decimal purchaseAmount, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Claims purchase ids with FOR UPDATE SKIP LOCKED inside the current transaction (multi-instance safe jobs).
    /// Purchase rows are always locked before approval rows to keep a single lock order.
    /// </summary>
    Task<IReadOnlyList<Guid>> ClaimPurchasesWithExpiredApprovalsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>READY_FOR_CHECKOUT without approval past checkout_expires_at, or CHECKOUT_PENDING past the 3 h PayPal window.</summary>
    Task<IReadOnlyList<Guid>> ClaimExpiredCheckoutPurchaseIdsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Purchases with non-terminal PayPal payments not updated since <paramref name="olderThan"/> (reconciler).</summary>
    Task<IReadOnlyList<Guid>> FindPurchasesToReconcileAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ClaimReceivedWebhookIdsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>INSERT … ON CONFLICT (paypal_event_id) DO NOTHING. Returns true when inserted.</summary>
    Task<bool> InsertWebhookEventIfNewAsync(PayPalWebhookEvent webhookEvent, CancellationToken cancellationToken);

    Task<bool> CanConnectAsync(CancellationToken cancellationToken);
}
