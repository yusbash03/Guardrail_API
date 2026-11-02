using Guardrail.Api.Http;
using Guardrail.Api.RateLimiting;
using Guardrail.Application.Approvals;
using Guardrail.Application.Carts;
using Guardrail.Application.Common;
using Guardrail.Application.Evaluation;
using Guardrail.Application.Payments;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Common;

namespace Guardrail.Api.Endpoints;

public sealed record CreateCartRequest(Guid? MerchantId, IReadOnlyList<CartLine>? Items);

public sealed record QuantityRequest(int? Quantity);

public sealed record CreatePurchaseRequest(Guid? CartId, Guid? EvaluationId);

public sealed record RejectRequest(string? Reason);

public static class ShoppingEndpoints
{
    public static void MapShoppingEndpoints(this RouteGroupBuilder api)
    {
        // ------------------------------------------------------------- carts
        var carts = api.MapGroup("/carts").WithTags("Carts");
        carts.MapPost("/", async (CreateCartRequest body, CartService service, HttpContext ctx) =>
        {
            if (body.MerchantId is not { } merchantId)
            {
                return Problems.Result(DomainError.Validation("merchantId is required."), ctx);
            }

            return (await service.CreateAsync(ctx.UserId(), new CreateCartCommand(merchantId, body.Items), ActorType.User, null, ctx.RequestAborted))
                .ToHttp(ctx, StatusCodes.Status201Created);
        }).RequireRateLimiting(RateLimitPolicies.Mutations);

        carts.MapGet("/", async (string? status, CartService service, HttpContext ctx) =>
        {
            CartStatus? parsed = null;
            if (status is not null)
            {
                if (!EnumText.TryParse<CartStatus>(status, out var s))
                {
                    return Problems.Result(DomainError.Validation("Unknown cart status."), ctx);
                }

                parsed = s;
            }

            return Results.Json(await service.ListAsync(ctx.UserId(), parsed, ctx.RequestAborted), JsonDefaults.Options);
        });

        carts.MapGet("/{id:guid}", async (Guid id, CartService service, HttpContext ctx) =>
            (await service.GetAsync(ctx.UserId(), id, ctx.RequestAborted)).ToHttp(ctx));

        carts.MapPost("/{id:guid}/items", async (Guid id, CartLine body, CartService service, HttpContext ctx) =>
            IfMatch(ctx, out var version) is { } bad
                ? bad
                : (await service.AddItemAsync(ctx.UserId(), id, body, version, ctx.RequestAborted)).ToHttp(ctx))
            .RequireRateLimiting(RateLimitPolicies.Mutations);

        carts.MapPatch("/{id:guid}/items/{productId:guid}", async (Guid id, Guid productId, QuantityRequest body, CartService service, HttpContext ctx) =>
        {
            if (IfMatch(ctx, out var version) is { } bad)
            {
                return bad;
            }

            return body.Quantity is not { } quantity
                ? Problems.Result(DomainError.Validation("quantity is required."), ctx)
                : (await service.SetQuantityAsync(ctx.UserId(), id, productId, quantity, version, ctx.RequestAborted)).ToHttp(ctx);
        }).RequireRateLimiting(RateLimitPolicies.Mutations);

        carts.MapDelete("/{id:guid}/items/{productId:guid}", async (Guid id, Guid productId, CartService service, HttpContext ctx) =>
            IfMatch(ctx, out var version) is { } bad
                ? bad
                : (await service.RemoveItemAsync(ctx.UserId(), id, productId, version, ctx.RequestAborted)).ToHttp(ctx))
            .RequireRateLimiting(RateLimitPolicies.Mutations);

        carts.MapDelete("/{id:guid}", async (Guid id, CartService service, HttpContext ctx) =>
            (await service.AbandonAsync(ctx.UserId(), id, ctx.RequestAborted)).ToHttp(ctx, StatusCodes.Status204NoContent))
            .RequireRateLimiting(RateLimitPolicies.Mutations);

        carts.MapPost("/{id:guid}/evaluate", async (Guid id, EvaluationService service, HttpContext ctx) =>
            (await service.EvaluateCartAsync(ctx.UserId(), id, ActorType.User, ctx.RequestAborted)).ToHttp(ctx))
            .RequireRateLimiting(RateLimitPolicies.Mutations);

        api.MapGet("/evaluations/{id:guid}", async (Guid id, EvaluationService service, HttpContext ctx) =>
            (await service.GetAsync(ctx.UserId(), id, ctx.RequestAborted)).ToHttp(ctx)).WithTags("Carts");

        // ------------------------------------------------------------- purchases
        var purchases = api.MapGroup("/purchases").WithTags("Purchases");
        purchases.MapPost("/", (CreatePurchaseRequest body, PurchaseService service, Idempotency idempotency, HttpContext ctx) =>
            idempotency.ExecuteAsync(ctx, body, async () =>
            {
                if (body.CartId is not { } cartId || body.EvaluationId is not { } evaluationId)
                {
                    return Problems.From(DomainError.Validation("cartId and evaluationId are required."), ctx);
                }

                var result = await service.CreateAsync(ctx.UserId(), new CreatePurchaseCommand(cartId, evaluationId), ActorType.User, ctx.RequestAborted);
                return result.Map(r => r.Purchase).ToApi(ctx, StatusCodes.Status201Created);
            })).RequireRateLimiting(RateLimitPolicies.Mutations);

        purchases.MapGet("/", async (string? status, int? page, int? pageSize, PurchaseQueries queries, HttpContext ctx) =>
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

            return Results.Json(await queries.ListAsync(ctx.UserId(), parsed, new PageRequest(page ?? 1, pageSize ?? 20), ctx.RequestAborted),
                JsonDefaults.Options);
        });

        purchases.MapGet("/{id:guid}", async (Guid id, PurchaseQueries queries, HttpContext ctx) =>
            (await queries.GetAsync(ctx.UserId(), id, ctx.RequestAborted)).ToHttp(ctx));

        // Checkout and capture are user-only REST endpoints; no agent tool can reach them (BR-80).
        purchases.MapPost("/{id:guid}/checkout", (Guid id, CheckoutService service, Idempotency idempotency, HttpContext ctx) =>
            idempotency.ExecuteAsync(ctx, null, async () =>
                (await service.CheckoutAsync(ctx.UserId(), id, ctx.RequestAborted)).ToApi(ctx)))
            .RequireRateLimiting(RateLimitPolicies.Mutations);

        purchases.MapPost("/{id:guid}/capture", (Guid id, PaymentCompletionService service, Idempotency idempotency, HttpContext ctx) =>
            idempotency.ExecuteAsync(ctx, null, async () =>
                (await service.CompleteAsync(id, ctx.UserId(), LedgerSource.PaypalApi, ctx.RequestAborted)).ToApi(ctx)))
            .RequireRateLimiting(RateLimitPolicies.Mutations);

        purchases.MapPost("/{id:guid}/cancel", async (Guid id, PurchaseService service, HttpContext ctx) =>
            (await service.CancelAsync(ctx.UserId(), id, ctx.RequestAborted)).ToHttp(ctx))
            .RequireRateLimiting(RateLimitPolicies.Mutations);

        // ------------------------------------------------------------- approvals
        var approvals = api.MapGroup("/approvals").WithTags("Approvals");
        approvals.MapGet("/", async (string? status, PurchaseQueries queries, HttpContext ctx) =>
        {
            ApprovalStatus? parsed = null;
            if (status is not null)
            {
                if (!EnumText.TryParse<ApprovalStatus>(status, out var s))
                {
                    return Problems.Result(DomainError.Validation("Unknown approval status."), ctx);
                }

                parsed = s;
            }

            return Results.Json(await queries.ListApprovalsAsync(ctx.UserId(), parsed, ctx.RequestAborted), JsonDefaults.Options);
        });

        approvals.MapGet("/{id:guid}", async (Guid id, PurchaseQueries queries, HttpContext ctx) =>
            (await queries.GetApprovalAsync(ctx.UserId(), id, ctx.RequestAborted)).ToHttp(ctx));

        // BR-02 / ADR-015: web client header + displayed snapshot hash + the user's password.
        approvals.MapPost("/{id:guid}/approve", async (Guid id, ApproveCommand body, ApprovalService service, HttpContext ctx) =>
        {
            if (!ctx.HasWebClientHeader())
            {
                return Problems.Result(new DomainError(ErrorCodes.Forbidden, "Approvals can only be made from the Guardrail web app."), ctx);
            }

            return (await service.ApproveAsync(ctx.UserId(), id, body, ctx.RequestAborted)).ToHttp(ctx);
        }).RequireRateLimiting(RateLimitPolicies.Mutations);

        approvals.MapPost("/{id:guid}/reject", async (Guid id, RejectRequest? body, ApprovalService service, HttpContext ctx) =>
            (await service.RejectAsync(ctx.UserId(), id, body?.Reason, ctx.RequestAborted)).ToHttp(ctx))
            .RequireRateLimiting(RateLimitPolicies.Mutations);
    }

    /// <summary>Optional If-Match: "&lt;cartVersion&gt;" (API_CONTRACT.md carts).</summary>
    private static IResult? IfMatch(HttpContext ctx, out int? version)
    {
        version = null;
        var header = ctx.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        if (int.TryParse(header.Trim().Trim('"').Replace("W/", string.Empty, StringComparison.Ordinal).Trim('"'), out var parsed))
        {
            version = parsed;
            return null;
        }

        return Problems.Result(DomainError.Validation("If-Match must be \"<cartVersion>\"."), ctx);
    }
}
