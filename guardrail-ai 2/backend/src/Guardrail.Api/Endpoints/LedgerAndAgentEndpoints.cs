using Guardrail.Api.Http;
using Guardrail.Api.RateLimiting;
using Guardrail.Application.Agent;
using Guardrail.Application.Common;
using Guardrail.Application.Dashboard;
using Guardrail.Application.Ledger;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Common;

namespace Guardrail.Api.Endpoints;

public sealed record CreateConversationRequest(string? Title);

public sealed record PostMessageRequest(string? Content);

public static class LedgerAndAgentEndpoints
{
    public static void MapLedgerAndAgentEndpoints(this RouteGroupBuilder api)
    {
        // ------------------------------------------------------------- ledger, transactions, dashboard, audit
        var ledger = api.MapGroup("/ledger").WithTags("Ledger");
        ledger.MapGet("/summary", async (string? period, DateOnly? date, SpendingQueries queries, HttpContext ctx) =>
        {
            if (!EnumText.TryParse<BudgetPeriodType>(period ?? "WEEKLY", out var parsed))
            {
                return Problems.Result(DomainError.Validation("period must be WEEKLY or MONTHLY."), ctx);
            }

            return (await queries.SummaryAsync(ctx.UserId(), parsed, date, ctx.RequestAborted)).ToHttp(ctx);
        });

        ledger.MapGet("/entries", async (Guid? purchaseId, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
            SpendingQueries queries, HttpContext ctx) =>
            Results.Json(await queries.EntriesAsync(ctx.UserId(), purchaseId, from?.ToUniversalTime(), to?.ToUniversalTime(),
                new PageRequest(page ?? 1, pageSize ?? 50), ctx.RequestAborted), JsonDefaults.Options));

        api.MapGet("/transactions", async (string? status, DateTimeOffset? from, DateTimeOffset? to, string? sort, int? page, int? pageSize,
            PurchaseQueries queries, HttpContext ctx) =>
        {
            PurchaseStatus? parsed = null;
            if (status is not null)
            {
                if (!EnumText.TryParse<PurchaseStatus>(status, out var s))
                {
                    return Problems.Result(DomainError.Validation("Unknown purchase status."), ctx);
                }

                parsed = s;
            }

            return Results.Json(await queries.TransactionsAsync(ctx.UserId(), parsed, from?.ToUniversalTime(), to?.ToUniversalTime(), sort,
                new PageRequest(page ?? 1, pageSize ?? 20), ctx.RequestAborted), JsonDefaults.Options);
        }).WithTags("Ledger");

        api.MapGet("/dashboard/overview", async (DashboardService service, HttpContext ctx) =>
            (await service.OverviewAsync(ctx.UserId(), ctx.RequestAborted)).ToHttp(ctx)).WithTags("Dashboard");

        api.MapGet("/audit-logs", async (string? entityType, int? page, int? pageSize, AuditQueries queries, HttpContext ctx) =>
            Results.Json(await queries.ListAsync(ctx.UserId(), entityType, new PageRequest(page ?? 1, pageSize ?? 20), ctx.RequestAborted),
                JsonDefaults.Options)).WithTags("Audit");

        // ------------------------------------------------------------- agent
        var agent = api.MapGroup("/agent/conversations").WithTags("Agent");
        agent.MapPost("/", async (CreateConversationRequest? body, ConversationService service, HttpContext ctx) =>
            new ApiResponse(StatusCodes.Status201Created, await service.CreateAsync(ctx.UserId(), body?.Title, ctx.RequestAborted), false).ToResult())
            .RequireRateLimiting(RateLimitPolicies.Mutations);

        agent.MapGet("/", async (ConversationService service, HttpContext ctx) =>
            Results.Json(await service.ListAsync(ctx.UserId(), ctx.RequestAborted), JsonDefaults.Options));

        agent.MapDelete("/{id:guid}", async (Guid id, ConversationService service, HttpContext ctx) =>
            (await service.ArchiveAsync(ctx.UserId(), id, ctx.RequestAborted)).ToHttp(ctx, StatusCodes.Status204NoContent))
            .RequireRateLimiting(RateLimitPolicies.Mutations);

        agent.MapGet("/{id:guid}/messages", async (Guid id, int? afterSequence, ConversationService service, HttpContext ctx) =>
            (await service.MessagesAsync(ctx.UserId(), id, afterSequence, ctx.RequestAborted)).ToHttp(ctx));

        // BR-83 / SEC-71: 20 turns per 10 min and one concurrent turn per user.
        agent.MapPost("/{id:guid}/messages", async (Guid id, PostMessageRequest body, AgentOrchestrator orchestrator, HttpContext ctx) =>
            (await orchestrator.RunTurnAsync(ctx.UserId(), id, body.Content, ctx.RequestAborted)).ToHttp(ctx))
            .RequireRateLimiting(RateLimitPolicies.Agent);
    }
}
