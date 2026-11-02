using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Policies;
using Guardrail.Domain.Common;
using Guardrail.Domain.Ledger;
using Guardrail.Domain.Policy;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Ledger;

public sealed record PeriodBalance(decimal Spent, decimal Pending)
{
    public decimal Committed => Spent + Pending;
}

/// <summary>Read side of the ledger: spending snapshots for the engine and summaries for the UI/agent.</summary>
public sealed class SpendingQueries(IAppDbContext db, PolicyLoader policies, TimeProvider clock)
{
    /// <summary>SpendingSnapshot for the periods containing <paramref name="now"/> (SPENDING_POLICY_ENGINE.md §1.4).</summary>
    public async Task<SpendingSnapshot> BuildSnapshotAsync(
        Guid userId, string timeZone, PolicySnapshot? policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (weekStart, monthStart) = BudgetPeriods.PeriodStarts(now, timeZone);
        var weekly = await BalancesAsync(userId, BudgetPeriodType.Weekly, weekStart, cancellationToken);
        var monthly = await BalancesAsync(userId, BudgetPeriodType.Monthly, monthStart, cancellationToken);

        var recent = new List<RecentPurchase>();
        if (policy?.DuplicatePurchase is { } duplicate)
        {
            var since = now.AddHours(-duplicate.WindowHours);
            var statuses = Purchase.CountsForDuplicates.ToList();
            var purchases = await db.Purchases.AsNoTracking()
                .Where(p => p.UserId == userId && p.CreatedAt >= since && statuses.Contains(p.Status))
                .Select(p => new { p.Id, p.CreatedAt, p.MerchantId, p.CartSnapshotHash, p.CartId })
                .ToListAsync(cancellationToken);
            var cartIds = purchases.Select(p => p.CartId).ToList();
            var items = await db.CartItems.AsNoTracking()
                .Where(i => cartIds.Contains(i.CartId))
                .Select(i => new { i.CartId, i.ProductId })
                .ToListAsync(cancellationToken);
            recent.AddRange(purchases.Select(p => new RecentPurchase(
                p.Id, p.CreatedAt, p.MerchantId, items.Where(i => i.CartId == p.CartId).Select(i => i.ProductId).ToList(), p.CartSnapshotHash)));
        }

        return new SpendingSnapshot(
            weekStart,
            monthStart,
            weekly.ToDictionary(p => p.Key, p => p.Value.Committed),
            monthly.ToDictionary(p => p.Key, p => p.Value.Committed),
            weekly.Values.Sum(v => v.Committed),
            monthly.Values.Sum(v => v.Committed),
            recent);
    }

    /// <summary>Per-category spent (CAPTURED) and pending (PROPOSED+RESERVED+AUTHORIZED) for one period (SPENDING_LEDGER.md §2).</summary>
    public async Task<Dictionary<string, PeriodBalance>> BalancesAsync(
        Guid userId, BudgetPeriodType period, DateOnly periodStart, CancellationToken cancellationToken)
    {
        var query = db.LedgerEntries.AsNoTracking().Where(e => e.UserId == userId);
        query = period == BudgetPeriodType.Weekly
            ? query.Where(e => e.BudgetWeekStart == periodStart)
            : query.Where(e => e.BudgetMonthStart == periodStart);

        var rows = await query
            .GroupBy(e => new { e.CategoryCode, e.FromBucket, e.ToBucket })
            .Select(g => new { g.Key.CategoryCode, g.Key.FromBucket, g.Key.ToBucket, Amount = g.Sum(e => e.Amount) })
            .ToListAsync(cancellationToken);

        return rows.GroupBy(r => r.CategoryCode).ToDictionary(g => g.Key, g =>
        {
            decimal Balance(LedgerBucket bucket) =>
                g.Where(r => r.ToBucket == bucket).Sum(r => r.Amount) - g.Where(r => r.FromBucket == bucket).Sum(r => r.Amount);

            return new PeriodBalance(Balance(LedgerBucket.Captured), LedgerRules.Pending.Sum(Balance));
        });
    }

    public async Task<Result<LedgerSummaryDto>> SummaryAsync(
        Guid userId, BudgetPeriodType period, DateOnly? date, CancellationToken cancellationToken)
    {
        var current = await policies.LoadCurrentAsync(userId, cancellationToken);
        if (current is null)
        {
            return new DomainError(ErrorCodes.NoActivePolicy, "No active policy.");
        }

        var localDate = date ?? BudgetPeriods.LocalDate(clock.GetUtcNow(), current.Profile.TimeZone);
        var start = period == BudgetPeriodType.Weekly ? BudgetPeriods.WeekStart(localDate) : BudgetPeriods.MonthStart(localDate);
        var balances = await BalancesAsync(userId, period, start, cancellationToken);
        var snapshot = current.Snapshot;

        var budgets = snapshot.CategoryBudgets.Where(b => b.Period == period).ToDictionary(b => b.CategoryCode, b => b.Limit);
        var categories = budgets.Keys.Union(balances.Keys).Order(StringComparer.Ordinal).Select(code =>
        {
            var balance = balances.GetValueOrDefault(code, new PeriodBalance(0, 0));
            decimal? limit = budgets.TryGetValue(code, out var l) ? l : null;
            return new LedgerCategorySummaryDto(code, period, MoneyFormat.Format(limit), MoneyFormat.Format(balance.Spent),
                MoneyFormat.Format(balance.Pending), MoneyFormat.Format(limit - balance.Committed));
        }).ToList();

        var overallLimit = snapshot.SpendingLimits.FirstOrDefault(l => l.Period == period)?.Limit;
        var spent = balances.Values.Sum(b => b.Spent);
        var pending = balances.Values.Sum(b => b.Pending);
        return new LedgerSummaryDto(period, start, BudgetPeriods.PeriodEnd(period, start), snapshot.Currency, categories,
            new LedgerPeriodTotalsDto(MoneyFormat.Format(overallLimit), MoneyFormat.Format(spent), MoneyFormat.Format(pending),
                MoneyFormat.Format(overallLimit - spent - pending)));
    }

    public async Task<PagedResult<LedgerEntryDto>> EntriesAsync(
        Guid userId, Guid? purchaseId, DateTimeOffset? from, DateTimeOffset? to, PageRequest page, CancellationToken cancellationToken)
    {
        var query = db.LedgerEntries.AsNoTracking().Where(e => e.UserId == userId);
        if (purchaseId is { } pid)
        {
            query = query.Where(e => e.PurchaseId == pid);
        }

        if (from is { } f)
        {
            query = query.Where(e => e.OccurredAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(e => e.OccurredAt <= t);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<LedgerEntryDto>(items.Select(e => new LedgerEntryDto(e.Id, e.PurchaseId, e.PaymentId, e.CategoryCode,
            MoneyFormat.Format(e.Amount), e.Currency, e.Transition, e.FromBucket, e.ToBucket, e.BudgetWeekStart, e.BudgetMonthStart, e.Source,
            e.ExternalRef, e.OccurredAt)).ToList(), page.SafePage, page.SafePageSize, total);
    }
}
