using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Ledger;
using Guardrail.Application.Policies;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Dashboard;

public sealed record PeriodOverviewDto(DateOnly PeriodStart, string? Limit, string Spent, string Pending, string? Remaining);

public sealed record InsightDto(string Kind, string Severity, string Text);

public sealed record DashboardOverviewDto(
    string Currency,
    PeriodOverviewDto Weekly,
    PeriodOverviewDto Monthly,
    IReadOnlyList<LedgerCategorySummaryDto> Categories,
    int PendingApprovals,
    IReadOnlyList<PurchaseDto> RecentPurchases,
    IReadOnlyList<InsightDto> Insights);

public sealed class DashboardService(IAppDbContext db, PolicyLoader policies, SpendingQueries spending, PurchaseQueries purchases, TimeProvider clock)
{
    public async Task<Result<DashboardOverviewDto>> OverviewAsync(Guid userId, CancellationToken cancellationToken)
    {
        var current = await policies.LoadCurrentAsync(userId, cancellationToken);
        if (current is null)
        {
            return new DomainError(ErrorCodes.NoActivePolicy, "No active policy.");
        }

        var now = clock.GetUtcNow();
        var today = BudgetPeriods.LocalDate(now, current.Profile.TimeZone);
        var weekStart = BudgetPeriods.WeekStart(today);
        var monthStart = BudgetPeriods.MonthStart(today);
        var weekly = await spending.BalancesAsync(userId, BudgetPeriodType.Weekly, weekStart, cancellationToken);
        var monthly = await spending.BalancesAsync(userId, BudgetPeriodType.Monthly, monthStart, cancellationToken);
        var policy = current.Snapshot;

        var categories = new List<LedgerCategorySummaryDto>();
        var insights = new List<InsightDto>();
        foreach (var budget in policy.CategoryBudgets.OrderBy(b => b.CategoryCode, StringComparer.Ordinal).ThenBy(b => b.Period))
        {
            var balances = budget.Period == BudgetPeriodType.Weekly ? weekly : monthly;
            var balance = balances.GetValueOrDefault(budget.CategoryCode, new PeriodBalance(0, 0));
            categories.Add(new LedgerCategorySummaryDto(budget.CategoryCode, budget.Period, MoneyFormat.Format(budget.Limit),
                MoneyFormat.Format(balance.Spent), MoneyFormat.Format(balance.Pending), MoneyFormat.Format(budget.Limit - balance.Committed)));
            insights.AddRange(BudgetPace(budget, balance, today, weekStart, monthStart));
        }

        var pendingApprovals = await db.ApprovalRequests.CountAsync(
            a => a.UserId == userId && a.Status == ApprovalStatus.Pending && a.ExpiresAt > now, cancellationToken);
        if (pendingApprovals > 0)
        {
            insights.Add(new InsightDto("PENDING_APPROVALS", "ACTION",
                pendingApprovals == 1 ? "1 purchase is waiting for your approval." : $"{pendingApprovals} purchases are waiting for your approval."));
        }

        return new DashboardOverviewDto(
            policy.Currency,
            Period(policy, BudgetPeriodType.Weekly, weekStart, weekly),
            Period(policy, BudgetPeriodType.Monthly, monthStart, monthly),
            categories,
            pendingApprovals,
            await purchases.RecentAsync(userId, 5, cancellationToken),
            insights);
    }

    private static PeriodOverviewDto Period(PolicySnapshot policy, BudgetPeriodType period, DateOnly start, Dictionary<string, PeriodBalance> balances)
    {
        var limit = policy.SpendingLimits.FirstOrDefault(l => l.Period == period)?.Limit;
        var spent = balances.Values.Sum(b => b.Spent);
        var pending = balances.Values.Sum(b => b.Pending);
        return new PeriodOverviewDto(start, MoneyFormat.Format(limit), MoneyFormat.Format(spent), MoneyFormat.Format(pending),
            MoneyFormat.Format(limit - spent - pending));
    }

    /// <summary>Deterministic template insights; numbers come from the ledger (API_CONTRACT.md dashboard).</summary>
    private static IEnumerable<InsightDto> BudgetPace(
        CategoryBudgetRule budget, PeriodBalance balance, DateOnly today, DateOnly weekStart, DateOnly monthStart)
    {
        if (balance.Committed <= 0)
        {
            yield break;
        }

        var used = (int)Math.Round(balance.Committed / budget.Limit * 100m, MidpointRounding.AwayFromZero);
        var end = BudgetPeriods.PeriodEnd(budget.Period, budget.Period == BudgetPeriodType.Weekly ? weekStart : monthStart);
        var daysLeft = end.DayNumber - today.DayNumber;
        var category = CategoryCodes.DisplayName(budget.CategoryCode);
        var period = budget.Period == BudgetPeriodType.Weekly ? "weekly" : "monthly";
        var severity = used >= 100 ? "ALERT" : used >= 80 ? "WARNING" : "INFO";
        var left = daysLeft switch
        {
            0 => "it resets tomorrow",
            1 => "1 day left",
            _ => $"{daysLeft} days left",
        };
        yield return new InsightDto("BUDGET_PACE", severity, $"You've used {used}% of your {period} {category} budget with {left}.");
    }
}

public sealed class AuditQueries(IAppDbContext db)
{
    public async Task<PagedResult<AuditLogDto>> ListAsync(Guid userId, string? entityType, PageRequest page, CancellationToken cancellationToken)
    {
        var query = db.AuditLogs.AsNoTracking().Where(a => a.UserId == userId);
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        var items = rows.Select(a => new AuditLogDto(a.Id, a.ActorType, a.Action, a.EntityType, a.EntityId, a.BeforeHash, a.AfterHash,
            System.Text.Json.JsonDocument.Parse(a.Data).RootElement.Clone(), a.CreatedAt)).ToList();
        return new PagedResult<AuditLogDto>(items, page.SafePage, page.SafePageSize, total);
    }
}
