using Guardrail.Domain.Common;

namespace Guardrail.Domain.Shopping;

/// <summary>Persisted, immutable output of an evaluation (policy_evaluations).</summary>
public sealed class PolicyEvaluation
{
    private PolicyEvaluation()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid CartId { get; private set; }

    public int CartVersion { get; private set; }

    public string CartSnapshotHash { get; private set; } = string.Empty;

    public string CartSnapshotJson { get; private set; } = "{}";

    public Guid PolicyVersionId { get; private set; }

    public PolicyDecisionType Decision { get; private set; }

    /// <summary>jsonb PolicyReason[] (wire shape).</summary>
    public string Reasons { get; private set; } = "[]";

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = MoneyFormat.DefaultCurrency;

    /// <summary>jsonb BudgetImpact[] (wire shape).</summary>
    public string BudgetImpacts { get; private set; } = "[]";

    public ActorType RequestedByActor { get; private set; }

    public Guid? AgentExecutionId { get; private set; }

    public DateTimeOffset EvaluatedAt { get; private set; }

    public static PolicyEvaluation Record(
        Guid userId,
        Guid cartId,
        int cartVersion,
        string snapshotHash,
        string snapshotJson,
        Guid policyVersionId,
        PolicyDecisionType decision,
        string reasonsJson,
        decimal amount,
        string currency,
        string budgetImpactsJson,
        ActorType actor,
        DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        UserId = userId,
        CartId = cartId,
        CartVersion = cartVersion,
        CartSnapshotHash = snapshotHash,
        CartSnapshotJson = snapshotJson,
        PolicyVersionId = policyVersionId,
        Decision = decision,
        Reasons = reasonsJson,
        Amount = amount,
        Currency = currency,
        BudgetImpacts = budgetImpactsJson,
        RequestedByActor = actor,
        EvaluatedAt = now,
    };
}

public sealed class Purchase
{
    private static readonly Dictionary<PurchaseStatus, PurchaseStatus[]> Allowed = new()
    {
        [PurchaseStatus.AwaitingApproval] =
            [PurchaseStatus.ReadyForCheckout, PurchaseStatus.Rejected, PurchaseStatus.Expired, PurchaseStatus.Cancelled],
        [PurchaseStatus.ReadyForCheckout] = [PurchaseStatus.CheckoutPending, PurchaseStatus.Cancelled, PurchaseStatus.Expired],
        // Out-of-order verified webhooks may skip intermediate states (forward-only, PAYMENT_STATE_MACHINE.md §4).
        [PurchaseStatus.CheckoutPending] =
        [
            PurchaseStatus.PayerApproved, PurchaseStatus.PaymentAuthorized, PurchaseStatus.CapturePending, PurchaseStatus.Completed,
            PurchaseStatus.Expired, PurchaseStatus.Cancelled, PurchaseStatus.Failed,
        ],
        [PurchaseStatus.PayerApproved] =
            [PurchaseStatus.PaymentAuthorized, PurchaseStatus.CapturePending, PurchaseStatus.Completed, PurchaseStatus.Failed],
        [PurchaseStatus.PaymentAuthorized] = [PurchaseStatus.CapturePending, PurchaseStatus.Completed, PurchaseStatus.Failed],
        [PurchaseStatus.CapturePending] = [PurchaseStatus.Completed, PurchaseStatus.Failed],
        [PurchaseStatus.Completed] = [PurchaseStatus.PartiallyRefunded, PurchaseStatus.Refunded],
        [PurchaseStatus.PartiallyRefunded] = [PurchaseStatus.PartiallyRefunded, PurchaseStatus.Refunded],
    };

    private Purchase()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid CartId { get; private set; }

    public int CartVersion { get; private set; }

    public string CartSnapshotHash { get; private set; } = string.Empty;

    public Guid PolicyEvaluationId { get; private set; }

    public Guid PolicyVersionId { get; private set; }

    public Guid MerchantId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = MoneyFormat.DefaultCurrency;

    public PurchaseStatus Status { get; private set; }

    public DateOnly BudgetWeekStart { get; private set; }

    public DateOnly BudgetMonthStart { get; private set; }

    public string? FailureCode { get; private set; }

    public DateTimeOffset? CheckoutExpiresAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static IReadOnlySet<PurchaseStatus> Terminal { get; } = new HashSet<PurchaseStatus>
    {
        PurchaseStatus.Rejected, PurchaseStatus.Cancelled, PurchaseStatus.Expired, PurchaseStatus.Failed, PurchaseStatus.Refunded,
    };

    /// <summary>Statuses that count as "recent purchases" for duplicate checks (SPENDING_POLICY_ENGINE.md §1.4).</summary>
    public static IReadOnlySet<PurchaseStatus> CountsForDuplicates { get; } = new HashSet<PurchaseStatus>
    {
        PurchaseStatus.AwaitingApproval, PurchaseStatus.ReadyForCheckout, PurchaseStatus.CheckoutPending, PurchaseStatus.PayerApproved,
        PurchaseStatus.PaymentAuthorized, PurchaseStatus.CapturePending, PurchaseStatus.Completed,
    };

    public static bool CanTransition(PurchaseStatus from, PurchaseStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>BR-42/BR-43: created only from a non-BLOCKED evaluation; amount and currency come from the snapshot.</summary>
    public static Purchase Create(
        ShoppingCart cart,
        PolicyEvaluation evaluation,
        DateOnly weekStart,
        DateOnly monthStart,
        DateTimeOffset now,
        TimeSpan allowedCheckoutWindow)
    {
        if (evaluation.Decision == PolicyDecisionType.Blocked)
        {
            throw new DomainException(new DomainError(ErrorCodes.EvaluationBlocked, "Blocked evaluations never create purchases."));
        }

        var requiresApproval = evaluation.Decision == PolicyDecisionType.RequiresApproval;
        return new Purchase
        {
            Id = Ids.New(),
            UserId = cart.UserId,
            CartId = cart.Id,
            CartVersion = evaluation.CartVersion,
            CartSnapshotHash = evaluation.CartSnapshotHash,
            PolicyEvaluationId = evaluation.Id,
            PolicyVersionId = evaluation.PolicyVersionId,
            MerchantId = cart.MerchantId,
            Amount = evaluation.Amount,
            Currency = evaluation.Currency,
            Status = requiresApproval ? PurchaseStatus.AwaitingApproval : PurchaseStatus.ReadyForCheckout,
            BudgetWeekStart = weekStart,
            BudgetMonthStart = monthStart,
            CheckoutExpiresAt = requiresApproval ? null : now.Add(allowedCheckoutWindow),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void TransitionTo(PurchaseStatus target, DateTimeOffset now, string? failureCode = null)
    {
        if (!CanTransition(Status, target))
        {
            throw DomainException.InvalidTransition("Purchase", EnumText.ToText(Status), EnumText.ToText(target));
        }

        Status = target;
        UpdatedAt = now;
        if (failureCode is not null)
        {
            FailureCode = failureCode;
        }

        if (target == PurchaseStatus.Completed)
        {
            CompletedAt = now;
        }
    }

    public void StartCheckout(DateTimeOffset now, TimeSpan payPalApprovalTimeout)
    {
        TransitionTo(PurchaseStatus.CheckoutPending, now);
        CheckoutExpiresAt = now.Add(payPalApprovalTimeout);
    }

    public void Flag(string failureCode, DateTimeOffset now)
    {
        FailureCode = failureCode;
        UpdatedAt = now;
    }

    public bool IsTerminal => Terminal.Contains(Status);
}
