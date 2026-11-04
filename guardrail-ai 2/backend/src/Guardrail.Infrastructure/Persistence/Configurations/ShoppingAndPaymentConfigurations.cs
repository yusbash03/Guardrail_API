using Guardrail.Domain.Agent;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Authorization;
using Guardrail.Domain.Catalogue;
using Guardrail.Domain.Insights;
using Guardrail.Domain.Ledger;
using Guardrail.Domain.Payments;
using Guardrail.Domain.Shopping;
using Guardrail.Domain.Spending;
using Guardrail.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Guardrail.Infrastructure.Persistence.Configurations;

internal sealed class ShoppingSessionConfiguration : IEntityTypeConfiguration<ShoppingSession>
{
    public void Configure(EntityTypeBuilder<ShoppingSession> b)
    {
        b.ToTable("shopping_sessions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.UserId, x.ConversationId });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AgentConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShoppingCartConfiguration : IEntityTypeConfiguration<ShoppingCart>
{
    public void Configure(EntityTypeBuilder<ShoppingCart> b)
    {
        b.ToTable("shopping_carts");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.UserId, x.Status });
        b.Property(x => x.Version).HasDefaultValue(1);
        b.Property(x => x.Subtotal).AsMoney();
        b.Property(x => x.Total).AsMoney();
        b.Property(x => x.Currency).AsCurrency();
        b.HasXmin();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ShoppingSession>().WithMany().HasForeignKey(x => x.ShoppingSessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.CartId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> b)
    {
        b.ToTable("cart_items", t => t.HasCheckConstraint("ck_cart_items_quantity", "quantity between 1 and 99"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.CartId, x.ProductId }).IsUnique();
        b.Property(x => x.UnitPrice).AsMoney();
        b.Property(x => x.LineTotal).AsMoney();
        b.Property(x => x.Currency).AsCurrency();
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryCode).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PolicyEvaluationConfiguration : IEntityTypeConfiguration<PolicyEvaluation>
{
    public void Configure(EntityTypeBuilder<PolicyEvaluation> b)
    {
        b.ToTable("policy_evaluations");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.CartId, x.CartVersion });
        b.HasIndex(x => new { x.UserId, x.EvaluatedAt }).IsDescending(false, true);
        b.Property(x => x.CartSnapshotHash).AsHash();
        b.Property(x => x.CartSnapshotJson).AsJson();
        b.Property(x => x.Reasons).AsJson();
        b.Property(x => x.BudgetImpacts).AsJson();
        b.Property(x => x.Amount).AsMoney();
        b.Property(x => x.Currency).AsCurrency();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ShoppingCart>().WithMany().HasForeignKey(x => x.CartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PolicyVersion>().WithMany().HasForeignKey(x => x.PolicyVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseConfiguration : IEntityTypeConfiguration<Purchase>
{
    public void Configure(EntityTypeBuilder<Purchase> b)
    {
        b.ToTable("purchases", t => t.HasCheckConstraint("ck_purchases_amount", "amount > 0"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.CartId, x.CartVersion }).IsUnique();
        b.HasIndex(x => new { x.UserId, x.Status, x.CreatedAt }).IsDescending(false, false, true);
        b.HasIndex(x => x.CheckoutExpiresAt).HasFilter("status in ('READY_FOR_CHECKOUT','CHECKOUT_PENDING')");
        b.Property(x => x.CartSnapshotHash).AsHash();
        b.Property(x => x.Amount).AsMoney();
        b.Property(x => x.Currency).AsCurrency();
        b.HasXmin();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ShoppingCart>().WithMany().HasForeignKey(x => x.CartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PolicyEvaluation>().WithMany().HasForeignKey(x => x.PolicyEvaluationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PolicyVersion>().WithMany().HasForeignKey(x => x.PolicyVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> b)
    {
        b.ToTable("approval_requests");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.PurchaseId).IsUnique();
        b.HasIndex(x => new { x.UserId, x.Status, x.ExpiresAt });
        b.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_approvals_expiry").HasFilter("status in ('PENDING','APPROVED')");
        b.Property(x => x.CartSnapshotHash).AsHash();
        b.Property(x => x.Amount).AsMoney();
        b.Property(x => x.Currency).AsCurrency();
        b.Property(x => x.Reasons).AsJson();
        b.HasXmin();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Purchase>().WithMany().HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ShoppingCart>().WithMany().HasForeignKey(x => x.CartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PolicyVersion>().WithMany().HasForeignKey(x => x.PolicyVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PolicyEvaluation>().WithMany().HasForeignKey(x => x.PolicyEvaluationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Provider).HasDefaultValue("PAYPAL");
        b.Property(x => x.Amount).AsMoney();
        b.Property(x => x.AuthorizedAmount).AsMoney();
        b.Property(x => x.CapturedAmount).AsMoney();
        b.Property(x => x.RefundedAmount).AsMoney().HasDefaultValue(0m);
        b.Property(x => x.Currency).AsCurrency();
        b.Property(x => x.FinalCheckResult).AsJson();
        b.Property(x => x.LastPaypalResponse).AsJson();
        b.HasIndex(x => x.PaypalOrderId).IsUnique();
        b.HasIndex(x => x.PaypalAuthorizationId).IsUnique();
        b.HasIndex(x => x.PaypalCaptureId).IsUnique();
        b.HasIndex(x => x.PaypalCreateRequestId).IsUnique();
        b.HasIndex(x => x.PaypalAuthorizeRequestId).IsUnique();
        b.HasIndex(x => x.PaypalCaptureRequestId).IsUnique();
        b.HasIndex(x => x.PaypalVoidRequestId).IsUnique();
        // One non-terminal payment per purchase (DOMAIN_MODEL.md).
        b.HasIndex(x => x.PurchaseId).IsUnique().HasDatabaseName("ux_payments_one_active_per_purchase")
            .HasFilter("status not in ('DECLINED','FAILED','VOIDED')");
        b.HasXmin();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Purchase>().WithMany().HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayPalWebhookEventConfiguration : IEntityTypeConfiguration<PayPalWebhookEvent>
{
    public void Configure(EntityTypeBuilder<PayPalWebhookEvent> b)
    {
        b.ToTable("paypal_webhook_events");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.PaypalEventId).IsUnique();
        b.HasIndex(x => new { x.Status, x.ReceivedAt }).HasDatabaseName("ix_webhooks_pending").HasFilter("status = 'RECEIVED'");
        b.Property(x => x.RawBody).IsRequired();
        b.Property(x => x.Headers).AsJson();
        b.Property(x => x.Attempts).HasDefaultValue(0);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.RelatedPaymentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SpendingLedgerEntryConfiguration : IEntityTypeConfiguration<SpendingLedgerEntry>
{
    public void Configure(EntityTypeBuilder<SpendingLedgerEntry> b)
    {
        b.ToTable("spending_ledger_entries", t => t.HasCheckConstraint("ck_spending_ledger_entries_amount", "amount > 0"));
        b.HasKey(x => x.Id);
        // BR-70: each transition at most once per (purchase, category, external_ref).
        b.HasIndex(x => new { x.PurchaseId, x.CategoryCode, x.Transition, x.ExternalRef }).IsUnique();
        b.HasIndex(x => new { x.UserId, x.BudgetWeekStart, x.CategoryCode }).HasDatabaseName("ix_ledger_user_week");
        b.HasIndex(x => new { x.UserId, x.BudgetMonthStart, x.CategoryCode }).HasDatabaseName("ix_ledger_user_month");
        b.Property(x => x.Amount).AsMoney();
        b.Property(x => x.Currency).AsCurrency();
        b.Property(x => x.ExternalRef).HasDefaultValue(string.Empty);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Purchase>().WithMany().HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryCode).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AgentConversationConfiguration : IEntityTypeConfiguration<AgentConversation>
{
    public void Configure(EntityTypeBuilder<AgentConversation> b)
    {
        b.ToTable("agent_conversations");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.UserId, x.LastMessageAt });
        b.Property(x => x.Title).IsRequired();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AgentMessageConfiguration : IEntityTypeConfiguration<AgentMessage>
{
    public void Configure(EntityTypeBuilder<AgentMessage> b)
    {
        b.ToTable("agent_messages");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ConversationId, x.Sequence }).IsUnique();
        b.Property(x => x.Content).IsRequired();
        b.Property(x => x.Cards).AsJson().HasDefaultValueSql("'[]'::jsonb");
        b.Property(x => x.TokenUsage).AsJson();
        b.HasOne<AgentConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AgentExecutionConfiguration : IEntityTypeConfiguration<AgentExecution>
{
    public void Configure(EntityTypeBuilder<AgentExecution> b)
    {
        b.ToTable("agent_executions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ConversationId, x.CreatedAt }).HasDatabaseName("ix_agent_exec_conv");
        b.Property(x => x.ToolName).IsRequired();
        b.Property(x => x.ToolCallId).IsRequired();
        b.Property(x => x.Arguments).AsJson();
        b.Property(x => x.Result).AsJson();
        b.HasOne<AgentConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<AgentMessage>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_logs");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.UserId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_audit_user_time");
        b.HasIndex(x => new { x.EntityType, x.EntityId }).HasDatabaseName("ix_audit_entity");
        b.HasIndex(x => new { x.UserId, x.Action, x.CreatedAt }).HasDatabaseName("ix_audit_user_action_time");
        b.Property(x => x.Action).IsRequired();
        b.Property(x => x.EntityType).IsRequired();
        b.Property(x => x.BeforeHash).HasColumnType("char(64)");
        b.Property(x => x.AfterHash).HasColumnType("char(64)");
        b.Property(x => x.Data).AsJson().HasDefaultValueSql("'{}'::jsonb");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> b)
    {
        b.ToTable("idempotency_keys", t => t.HasCheckConstraint("ck_idempotency_keys_key", "length(key) between 8 and 100"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.UserId, x.Key, x.RequestPath }).IsUnique();
        b.Property(x => x.RequestHash).AsHash();
        b.Property(x => x.ResponseBody).AsJson();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InsightReportConfiguration : IEntityTypeConfiguration<InsightReport>
{
    public void Configure(EntityTypeBuilder<InsightReport> b)
    {
        b.ToTable("insight_reports", t => t.HasCheckConstraint("ck_insight_reports_name", "length(name) between 1 and 60"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.State).AsJson().IsRequired();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
