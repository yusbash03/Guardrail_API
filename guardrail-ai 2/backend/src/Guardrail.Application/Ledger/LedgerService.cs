using System.Globalization;
using System.Text.Json.Nodes;
using Guardrail.Application.Abstractions;
using Guardrail.Domain.Common;
using Guardrail.Domain.Ledger;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Guardrail.Application.Ledger;

/// <summary>
/// The only writer of spending_ledger_entries (SPENDING_LEDGER.md §3). Callers hold the purchase row lock.
/// Idempotent: a transition already applied for (purchase, category, external_ref) is a no-op (BR-70).
/// </summary>
public sealed class LedgerService(IAppDbContext db, TimeProvider clock, ILogger<LedgerService> log)
{
    /// <returns>false when the transition was refused (invalid source bucket); the refusal is logged at Error.</returns>
    public async Task<bool> ApplyAsync(
        Purchase purchase,
        LedgerTransition transition,
        LedgerSource source,
        Guid? paymentId,
        CancellationToken cancellationToken,
        string externalRef = "",
        IReadOnlyDictionary<string, decimal>? amountsOverride = null)
    {
        var amounts = amountsOverride ?? await AllocationAsync(purchase, cancellationToken);

        // Loading tracks the rows, so Local contains persisted + not-yet-saved entries for this purchase.
        await db.LedgerEntries.Where(e => e.PurchaseId == purchase.Id).LoadAsync(cancellationToken);
        var entries = db.LedgerEntries.Local.Where(e => e.PurchaseId == purchase.Id).ToList();
        var now = clock.GetUtcNow();
        var ok = true;

        foreach (var (category, amount) in amounts)
        {
            if (amount <= 0)
            {
                continue;
            }

            var categoryEntries = entries.Where(e => e.CategoryCode == category).ToList();
            if (categoryEntries.Any(e => e.Transition == transition && e.ExternalRef == externalRef))
            {
                continue; // already applied
            }

            var current = LedgerRules.CurrentBucket(categoryEntries);
            if (!LedgerRules.AllowedSources(transition).Contains(current))
            {
                log.LogError(
                    "LEDGER_INVALID_TRANSITION {Transition} from {Bucket} for purchase {PurchaseId} category {Category}",
                    transition, current, purchase.Id, category);
                ok = false;
                continue;
            }

            db.LedgerEntries.Add(SpendingLedgerEntry.Create(
                purchase.UserId, purchase.Id, paymentId, category, amount, purchase.Currency, transition,
                purchase.BudgetWeekStart, purchase.BudgetMonthStart, source, externalRef, now, current));
        }

        return ok;
    }

    /// <summary>Releases whatever is still held (PROPOSED/RESERVED → RELEASE; AUTHORIZED → VOID).</summary>
    public async Task ReleaseHeldAsync(Purchase purchase, LedgerSource source, Guid? paymentId, CancellationToken cancellationToken)
    {
        await db.LedgerEntries.Where(e => e.PurchaseId == purchase.Id).LoadAsync(cancellationToken);
        var buckets = db.LedgerEntries.Local.Where(e => e.PurchaseId == purchase.Id)
            .GroupBy(e => e.CategoryCode)
            .Select(g => LedgerRules.CurrentBucket(g))
            .ToList();

        if (buckets.Any(b => b is LedgerBucket.Proposed or LedgerBucket.Reserved))
        {
            await ApplyAsync(purchase, LedgerTransition.Release, source, paymentId, cancellationToken);
        }

        if (buckets.Any(b => b == LedgerBucket.Authorized))
        {
            await ApplyAsync(purchase, LedgerTransition.Void, source, paymentId, cancellationToken);
        }
    }

    /// <summary>Per-category amounts from the frozen cart snapshot the purchase was created from.</summary>
    public async Task<IReadOnlyDictionary<string, decimal>> AllocationAsync(Purchase purchase, CancellationToken cancellationToken)
    {
        var snapshotJson = await db.PolicyEvaluations
            .Where(e => e.Id == purchase.PolicyEvaluationId)
            .Select(e => e.CartSnapshotJson)
            .SingleAsync(cancellationToken);
        return AllocationFromSnapshot(snapshotJson);
    }

    public static IReadOnlyDictionary<string, decimal> AllocationFromSnapshot(string canonicalSnapshotJson)
    {
        var items = JsonNode.Parse(canonicalSnapshotJson)!["items"]!.AsArray();
        return items
            .Select(i => (Category: i!["categoryCode"]!.GetValue<string>(),
                Amount: decimal.Parse(i["lineTotal"]!.GetValue<string>(), CultureInfo.InvariantCulture)))
            .GroupBy(x => x.Category, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount), StringComparer.Ordinal);
    }

    /// <summary>Pro-rata allocation of a refund across categories; remainder cent to the largest category (SPENDING_LEDGER.md §3).</summary>
    public static IReadOnlyDictionary<string, decimal> ProRata(IReadOnlyDictionary<string, decimal> allocation, decimal amount)
    {
        var total = allocation.Values.Sum();
        if (total <= 0)
        {
            return new Dictionary<string, decimal>();
        }

        var result = allocation.ToDictionary(p => p.Key, p => Math.Floor(amount * p.Value / total * 100) / 100, StringComparer.Ordinal);
        var remainder = amount - result.Values.Sum();
        var largest = allocation.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
        result[largest] += remainder;
        return result;
    }
}
