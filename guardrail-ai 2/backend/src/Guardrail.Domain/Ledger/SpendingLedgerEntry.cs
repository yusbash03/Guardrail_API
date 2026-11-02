using Guardrail.Domain.Common;

namespace Guardrail.Domain.Ledger;

/// <summary>Immutable bucket transition for one (purchase, category) (ADR-009, SPENDING_LEDGER.md).</summary>
public sealed class SpendingLedgerEntry
{
    private SpendingLedgerEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid PurchaseId { get; private set; }

    public Guid? PaymentId { get; private set; }

    public string CategoryCode { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = MoneyFormat.DefaultCurrency;

    public LedgerTransition Transition { get; private set; }

    public LedgerBucket FromBucket { get; private set; }

    public LedgerBucket ToBucket { get; private set; }

    public DateOnly BudgetWeekStart { get; private set; }

    public DateOnly BudgetMonthStart { get; private set; }

    public LedgerSource Source { get; private set; }

    public string ExternalRef { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static SpendingLedgerEntry Create(
        Guid userId,
        Guid purchaseId,
        Guid? paymentId,
        string categoryCode,
        decimal amount,
        string currency,
        LedgerTransition transition,
        DateOnly weekStart,
        DateOnly monthStart,
        LedgerSource source,
        string externalRef,
        DateTimeOffset now,
        LedgerBucket? fromBucket = null)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Ledger amounts must be positive.");
        }

        var (defaultFrom, to) = LedgerRules.Buckets(transition);
        var from = fromBucket ?? defaultFrom;
        if (!LedgerRules.AllowedSources(transition).Contains(from))
        {
            throw new DomainException(new DomainError(ErrorCodes.LedgerInvalidTransition, $"{transition} cannot start from {from}."));
        }

        return new SpendingLedgerEntry
        {
            Id = Ids.New(),
            UserId = userId,
            PurchaseId = purchaseId,
            PaymentId = paymentId,
            CategoryCode = categoryCode,
            Amount = amount,
            Currency = currency,
            Transition = transition,
            FromBucket = from,
            ToBucket = to,
            BudgetWeekStart = weekStart,
            BudgetMonthStart = monthStart,
            Source = source,
            ExternalRef = externalRef,
            OccurredAt = now,
            CreatedAt = now,
        };
    }
}

public static class LedgerRules
{
    /// <summary>Buckets that count toward budgets (BR-72).</summary>
    public static readonly IReadOnlySet<LedgerBucket> Committed =
        new HashSet<LedgerBucket> { LedgerBucket.Proposed, LedgerBucket.Reserved, LedgerBucket.Authorized, LedgerBucket.Captured };

    public static readonly IReadOnlySet<LedgerBucket> Pending =
        new HashSet<LedgerBucket> { LedgerBucket.Proposed, LedgerBucket.Reserved, LedgerBucket.Authorized };

    /// <summary>Canonical (from, to) for each transition. RELEASE's `from` is resolved at write time (PROPOSED or RESERVED).</summary>
    public static (LedgerBucket From, LedgerBucket To) Buckets(LedgerTransition transition) => transition switch
    {
        LedgerTransition.Propose => (LedgerBucket.None, LedgerBucket.Proposed),
        LedgerTransition.Reserve => (LedgerBucket.None, LedgerBucket.Reserved),
        LedgerTransition.ConfirmReservation => (LedgerBucket.Proposed, LedgerBucket.Reserved),
        LedgerTransition.Release => (LedgerBucket.Reserved, LedgerBucket.Released),
        LedgerTransition.Authorize => (LedgerBucket.Reserved, LedgerBucket.Authorized),
        LedgerTransition.Capture => (LedgerBucket.Authorized, LedgerBucket.Captured),
        LedgerTransition.Void => (LedgerBucket.Authorized, LedgerBucket.Released),
        LedgerTransition.Fail => (LedgerBucket.Authorized, LedgerBucket.Failed),
        LedgerTransition.Refund => (LedgerBucket.Captured, LedgerBucket.Refunded),
        _ => throw new ArgumentOutOfRangeException(nameof(transition)),
    };

    public static IReadOnlySet<LedgerBucket> AllowedSources(LedgerTransition transition) => transition switch
    {
        LedgerTransition.Release => new HashSet<LedgerBucket> { LedgerBucket.Proposed, LedgerBucket.Reserved },
        _ => new HashSet<LedgerBucket> { Buckets(transition).From },
    };

    /// <summary>
    /// The bucket an amount currently sits in, replaying entries in order.
    /// Replays the latest to_bucket; entries are written only from the current bucket so this is exact.
    /// </summary>
    public static LedgerBucket CurrentBucket(IEnumerable<SpendingLedgerEntry> entriesForPurchaseCategory) =>
        entriesForPurchaseCategory
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .LastOrDefault()?.ToBucket ?? LedgerBucket.None;

    /// <summary>balance(bucket) = Σ to_bucket − Σ from_bucket.</summary>
    public static decimal Balance(IEnumerable<SpendingLedgerEntry> entries, LedgerBucket bucket)
    {
        var total = 0m;
        foreach (var e in entries)
        {
            if (e.ToBucket == bucket)
            {
                total += e.Amount;
            }

            if (e.FromBucket == bucket)
            {
                total -= e.Amount;
            }
        }

        return total;
    }

    public static decimal CommittedOf(IEnumerable<SpendingLedgerEntry> entries)
    {
        var list = entries as IReadOnlyCollection<SpendingLedgerEntry> ?? entries.ToList();
        return Committed.Sum(b => Balance(list, b));
    }
}
