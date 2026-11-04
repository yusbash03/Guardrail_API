using Guardrail.Api.Http;
using Guardrail.Api.RateLimiting;
using Guardrail.Application.Insights;

namespace Guardrail.Api.Endpoints;

/// <summary>AG_GRID_USAGE.md §5: read-only datasets for the AG Studio Insights workspace and saved report state.</summary>
public static class InsightsEndpoints
{
    public static void MapInsightsEndpoints(this RouteGroupBuilder api)
    {
        var insights = api.MapGroup("/insights").WithTags("Insights");

        insights.MapGet("/datasets", async (DateTimeOffset? from, DateTimeOffset? to, InsightsService service, HttpContext ctx) =>
            (await service.DatasetsAsync(ctx.UserId(), from?.ToUniversalTime(), to?.ToUniversalTime(), ctx.RequestAborted)).ToHttp(ctx));

        insights.MapGet("/reports/{name}", async (string name, InsightsService service, HttpContext ctx) =>
            (await service.GetReportAsync(ctx.UserId(), name, ctx.RequestAborted)).ToHttp(ctx));

        insights.MapPut("/reports/{name}", async (string name, SaveInsightReportCommand body, InsightsService service, HttpContext ctx) =>
            (await service.SaveReportAsync(ctx.UserId(), name, body, ctx.RequestAborted)).ToHttp(ctx))
            .RequireRateLimiting(RateLimitPolicies.Mutations);
    }
}
