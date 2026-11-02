using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Guardrail.Api.Http;
using Guardrail.Api.RateLimiting;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Jobs;
using Guardrail.Application.Payments;
using Guardrail.Domain.Common;
using Guardrail.Infrastructure.Admin;
using Guardrail.Infrastructure.Jobs;
using Microsoft.Extensions.Options;

namespace Guardrail.Api.Endpoints;

public static class OpsEndpoints
{
    private const int MaxWebhookBodyBytes = 64 * 1024;

    public static void MapOpsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", async (IAppDbContext db, HttpContext ctx) =>
        {
            var dbHealthy = await db.CanConnectAsync(ctx.RequestAborted);
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
            return Results.Json(new { status = dbHealthy ? "Healthy" : "Degraded", db = dbHealthy ? "Healthy" : "Unhealthy", version },
                statusCode: dbHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous().WithTags("Ops");

        // PAYPAL_WEBHOOK_CONTRACT.md: raw body, verify by postback, store, 200 fast. Not routed through Vercel.
        api.MapPost("/webhooks/paypal", async (HttpContext ctx, WebhookService webhooks) =>
        {
            if (ctx.Request.ContentLength > MaxWebhookBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var buffer = new char[MaxWebhookBodyBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, ctx.RequestAborted);
            if (read > MaxWebhookBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var headers = ctx.Request.Headers
                .Where(h => h.Key.StartsWith("PAYPAL-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            return await webhooks.ReceiveAsync(headers, new string(buffer, 0, read), ctx.RequestAborted) switch
            {
                WebhookReceiveOutcome.Accepted or WebhookReceiveOutcome.Duplicate => Results.Ok(),
                WebhookReceiveOutcome.VerifierUnavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                _ => Results.BadRequest(),
            };
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Webhook).WithTags("Webhooks");

        // Render Cron backstop (ADR-020). Never exposed through the Vercel rewrite.
        api.MapPost("/internal/jobs/run", async (HttpContext ctx, JobRunner jobs, IOptions<JobsOptions> options) =>
        {
            var expected = options.Value.InternalToken;
            var presented = ctx.Request.Headers["X-Job-Token"].ToString();
            if (string.IsNullOrEmpty(expected) || expected.Length < 32
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected)))
            {
                return Problems.Result(new DomainError(ErrorCodes.Unauthenticated, "Invalid job token."), ctx);
            }

            return Results.Json(await jobs.RunAllAsync(ctx.RequestAborted));
        }).AllowAnonymous().ExcludeFromDescription();

        api.MapPost("/admin/demo/reset", async (HttpContext ctx, DemoResetService reset, IOptions<DemoOptions> demo) =>
        {
            if (!demo.Value.ResetEnabled)
            {
                return Problems.Result(DomainError.NotFound(), ctx);
            }

            return await reset.ResetAsync(ctx.RequestAborted)
                ? Results.NoContent()
                : Problems.Result(DomainError.InvalidState("Seed:DemoUserPassword is not configured."), ctx);
        }).RequireAuthorization(AuthPolicies.Admin).WithTags("Ops");
    }
}

public static class AuthPolicies
{
    public const string Admin = "admin";
}
