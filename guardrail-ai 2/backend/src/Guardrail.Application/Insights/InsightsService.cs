using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Dtos;
using Guardrail.Application.Ledger;
using Guardrail.Application.Merchants;
using Guardrail.Application.Policies;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Common;
using Guardrail.Domain.Insights;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Insights;

// AG_GRID_USAGE.md §2.2: datasets for the AG Studio "Insights" workspace. Built server-side from the same sources of
// truth as the rest of the app; user-scoped and read-only. No emails, names, PayPal ids or payer data — these rows
// also reach the LLM. Money is sent as numbers (2 dp) for charting.

public sealed record InsightPurchaseRow(
    Guid PurchaseId,
    DateTimeOffset CreatedAt,
    DateOnly WeekStart,
    DateOnly MonthStart,
    string Merchant,
    MerchantStatus MerchantStatus,
    decimal Amount,
    string Currency,
    PurchaseStatus Status,
    PolicyDecisionType Decision,
    ActorType RequestedBy,
    int? ApprovalMinutes,
    bool? FinalCheckPassed,
    PaymentStatus? PaymentStatus);

public sealed record InsightLineItemRow(
    Guid PurchaseId,
    DateTimeOffset CreatedAt,
    DateOnly WeekStart,
    string Merchant,
    string Product,
    string CategoryCode,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal);

public sealed record InsightLedgerRow(
    DateTimeOffset OccurredAt,
    Guid PurchaseId,
    LedgerTransition Transition,
    LedgerBucket FromBucket,
    LedgerBucket ToBucket,
    string CategoryCode,
    decimal Amount,
    LedgerSource Source,
    DateOnly WeekStart,
    DateOnly MonthStart);

public sealed record InsightEvaluationRow(
    DateTimeOffset EvaluatedAt,
    string Merchant,
    PolicyDecisionType Decision,
    string PrimaryReasonCode,
    decimal Amount,
    IReadOnlyList<string> CategoryCodes,
    ActorType RequestedBy);

public sealed record InsightBudgetRow(
    BudgetScope Scope,
    string? CategoryCode,
    BudgetPeriodType Period,
    DateOnly PeriodStart,
    decimal? Limit,
    decimal Spent,
    decimal Pending,
    decimal? Remaining);

public sealed record InsightMerchantRow(string Merchant, MerchantStatus Status, IReadOnlyList<string> CategoryCodes);

public sealed record InsightDatasetsDto(
    string Currency,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<InsightPurchaseRow> Purchases,
    IReadOnlyList<InsightLineItemRow> LineItems,
    IReadOnlyList<InsightLedgerRow> Ledger,
    IReadOnlyList<InsightEvaluationRow> Evaluations,
    IReadOnlyList<InsightBudgetRow> Budgets,
    IReadOnlyList<InsightMerchantRow> Merchants);

public sealed record InsightReportDto(string Name, JsonElement State, string? Version, DateTimeOffset UpdatedAt);

public sealed record SaveInsightReportCommand(JsonElement? State, string? Version);

public sealed partial class InsightsService(
    IAppDbContext db,
    PolicyLoader policies,
    SpendingQueries spending,
    MerchantQueries merchants,
    TimeProvider clock)
{
    public const int DefaultDays = 90;
    public const int MaxDays = 366;
    public const int MaxStateBytes = 512 * 1024;

    public async Task<Result<InsightDatasetsDto>> DatasetsAsync(Guid userId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.AddDays(-DefaultDays);
        if (start > end || end - start > TimeSpan.FromDays(MaxDays))
        {
            return DomainError.Validation($"from must be before to and the range at most {MaxDays} days.");
        }

        var current = await policies.LoadCurrentAsync(userId, cancellationToken);
        if (current is null)
        {
            return new DomainError(ErrorCodes.NoActivePolicy, "No active policy.");
        }

        var merchantNames = await db.Merchants.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);
        string NameOf(Guid id) => merchantNames.GetValueOrDefault(id, "Unknown merchant");

        // ------------------------------------------------------------- purchases (+ approval timing, payment outcome)
        var purchases = await db.Purchases.AsNoTracking()
            .Where(p => p.UserId == userId && p.CreatedAt >= start && p.CreatedAt <= end)
            .OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        var purchaseIds = purchases.Select(p => p.Id).ToList();
        var evaluationIds = purchases.Select(p => p.PolicyEvaluationId).ToList();

        // Decision and requester live on the purchase's policy evaluation (as in PurchaseDto).
        var purchaseEvaluations = await db.PolicyEvaluations.AsNoTracking()
            .Where(e => evaluationIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new { e.Decision, e.RequestedByActor }, cancellationToken);
        var approvals = (await db.ApprovalRequests.AsNoTracking().Where(a => purchaseIds.Contains(a.PurchaseId)).ToListAsync(cancellationToken))
            .GroupBy(a => a.PurchaseId).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.CreatedAt).First());
        var payments = (await db.Payments.AsNoTracking().Where(p => purchaseIds.Contains(p.PurchaseId)).ToListAsync(cancellationToken))
            .GroupBy(p => p.PurchaseId).ToDictionary(g => g.Key, PurchaseQueries.PickPayment);

        var purchaseRows = purchases.Select(p =>
        {
            var approval = approvals.GetValueOrDefault(p.Id);
            var payment = payments.GetValueOrDefault(p.Id);
            int? approvalMinutes = approval is { DecidedAt: { } decided } && approval.Status is ApprovalStatus.Approved or ApprovalStatus.Consumed
                ? (int)Math.Round((decided - approval.CreatedAt).TotalMinutes)
                : null;
            bool? finalCheck = payment?.FinalCheckResult is { } json ? JsonDefaults.Deserialize<FinalCheckDto>(json).Passed : null;
            var evaluation = purchaseEvaluations.GetValueOrDefault(p.PolicyEvaluationId);
            return new InsightPurchaseRow(
                p.Id, p.CreatedAt, p.BudgetWeekStart, p.BudgetMonthStart, NameOf(p.MerchantId), current.Snapshot.MerchantStatusOf(p.MerchantId),
                Round(p.Amount), p.Currency, p.Status, evaluation?.Decision ?? PolicyDecisionType.Allowed,
                evaluation?.RequestedByActor ?? ActorType.User, approvalMinutes, finalCheck, payment?.Status);
        }).ToList();

        // ------------------------------------------------------------- line items (price watch)
        var cartIds = purchases.Select(p => p.CartId).ToList();
        var purchaseByCart = purchases.ToDictionary(p => p.CartId);
        var lineItems = (await db.CartItems.AsNoTracking().Where(i => cartIds.Contains(i.CartId)).ToListAsync(cancellationToken))
            .Select(i =>
            {
                var p = purchaseByCart[i.CartId];
                return new InsightLineItemRow(p.Id, p.CreatedAt, p.BudgetWeekStart, NameOf(p.MerchantId), i.ProductName, i.CategoryCode, Round(i.UnitPrice), i.Quantity, Round(i.LineTotal));
            })
            .OrderBy(i => i.CreatedAt).ToList();

        // ------------------------------------------------------------- ledger
        var ledger = (await db.LedgerEntries.AsNoTracking()
                .Where(e => e.UserId == userId && e.OccurredAt >= start && e.OccurredAt <= end)
                .OrderBy(e => e.OccurredAt).ToListAsync(cancellationToken))
            .Select(e => new InsightLedgerRow(e.OccurredAt, e.PurchaseId, e.Transition, e.FromBucket, e.ToBucket, e.CategoryCode, Round(e.Amount),
                e.Source, e.BudgetWeekStart, e.BudgetMonthStart))
            .ToList();

        // ------------------------------------------------------------- evaluations (incl. BLOCKED attempts that never became purchases)
        var evaluations = await db.PolicyEvaluations.AsNoTracking()
            .Where(e => e.UserId == userId && e.EvaluatedAt >= start && e.EvaluatedAt <= end)
            .OrderBy(e => e.EvaluatedAt).ToListAsync(cancellationToken);
        var evaluatedCartIds = evaluations.Select(e => e.CartId).Distinct().ToList();
        var cartMerchants = await db.Carts.AsNoTracking().Where(c => evaluatedCartIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.MerchantId, cancellationToken);
        var cartCategories = (await db.CartItems.AsNoTracking().Where(i => evaluatedCartIds.Contains(i.CartId))
                .Select(i => new { i.CartId, i.CategoryCode }).ToListAsync(cancellationToken))
            .GroupBy(i => i.CartId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(i => i.CategoryCode).Distinct().Order().ToList());

        var evaluationRows = evaluations.Select(e => new InsightEvaluationRow(
            e.EvaluatedAt,
            cartMerchants.TryGetValue(e.CartId, out var merchantId) ? NameOf(merchantId) : "Unknown merchant",
            e.Decision,
            PrimaryReasonCode(e.Reasons),
            Round(e.Amount),
            cartCategories.GetValueOrDefault(e.CartId, []),
            e.RequestedByActor)).ToList();

        // ------------------------------------------------------------- budgets (current week + month, from the ledger summary)
        var budgets = new List<InsightBudgetRow>();
        foreach (var period in new[] { BudgetPeriodType.Weekly, BudgetPeriodType.Monthly })
        {
            var summary = await spending.SummaryAsync(userId, period, null, cancellationToken);
            if (!summary.IsSuccess)
            {
                continue;
            }

            var s = summary.Value!;
            if (s.Overall.Limit is not null)
            {
                budgets.Add(new InsightBudgetRow(BudgetScope.Overall, null, period, s.PeriodStart, Money(s.Overall.Limit), Money(s.Overall.Spent)!.Value,
                    Money(s.Overall.Pending)!.Value, Money(s.Overall.Remaining)));
            }

            budgets.AddRange(s.Categories.Where(c => c.Period == period).Select(c => new InsightBudgetRow(
                BudgetScope.Category, c.CategoryCode, period, s.PeriodStart, Money(c.Limit), Money(c.Spent)!.Value, Money(c.Pending)!.Value, Money(c.Remaining))));
        }

        // ------------------------------------------------------------- merchants
        var merchantRows = (await merchants.ListAsync(userId, null, null, null, cancellationToken))
            .Select(m => new InsightMerchantRow(m.Name, m.Status, m.CategoryCodes)).ToList();

        return new InsightDatasetsDto(current.Profile.Currency, start, end, purchaseRows, lineItems, ledger, evaluationRows, budgets, merchantRows);
    }

    public async Task<Result<InsightReportDto>> GetReportAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        if (!ValidName(name))
        {
            return InvalidName();
        }

        var report = await db.InsightReports.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId && r.Name == name, cancellationToken);
        return report is null ? DomainError.NotFound("Report") : ToDto(report);
    }

    public async Task<Result<InsightReportDto>> SaveReportAsync(Guid userId, string name, SaveInsightReportCommand command, CancellationToken cancellationToken)
    {
        if (!ValidName(name))
        {
            return InvalidName();
        }

        if (command.State is not { ValueKind: JsonValueKind.Object } state)
        {
            return DomainError.Validation("state must be a JSON object (AG Studio api.getState()).");
        }

        var json = state.GetRawText();
        if (json.Length > MaxStateBytes)
        {
            return DomainError.Validation($"state must be at most {MaxStateBytes / 1024} KB.");
        }

        if (command.Version is { Length: > 40 })
        {
            return DomainError.Validation("version must be at most 40 characters.");
        }

        var now = clock.GetUtcNow();
        var report = await db.InsightReports.SingleOrDefaultAsync(r => r.UserId == userId && r.Name == name, cancellationToken);
        if (report is null)
        {
            report = InsightReport.Create(userId, name, json, command.Version, now);
            db.InsightReports.Add(report);
        }
        else
        {
            report.Update(json, command.Version, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(report);
    }

    private static InsightReportDto ToDto(InsightReport r)
    {
        using var doc = JsonDocument.Parse(r.State);
        return new InsightReportDto(r.Name, doc.RootElement.Clone(), r.StateVersion, r.UpdatedAt);
    }

    /// <summary>First BLOCK reason, else first REQUIRE_APPROVAL, else the first reason (mirrors PolicyDecision.PrimaryOf).</summary>
    private static string PrimaryReasonCode(string reasonsJson)
    {
        using var doc = JsonDocument.Parse(reasonsJson);
        var reasons = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [];
        string? Of(string severity) => reasons
            .Where(r => r.TryGetProperty("severity", out var s) && s.GetString() == severity)
            .Select(r => r.GetProperty("code").GetString()).FirstOrDefault();
        return Of("BLOCK") ?? Of("REQUIRE_APPROVAL")
            ?? reasons.Select(r => r.TryGetProperty("code", out var c) ? c.GetString() : null).FirstOrDefault()
            ?? "NONE";
    }

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.ToEven);

    private static decimal? Money(string? text) => text is null ? null : decimal.Parse(text, CultureInfo.InvariantCulture);

    private static bool ValidName(string name) => NamePattern().IsMatch(name);

    private static DomainError InvalidName() => DomainError.Validation("Report name must be 1–60 characters: lowercase letters, digits and dashes.");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,59}$")]
    private static partial Regex NamePattern();
}
