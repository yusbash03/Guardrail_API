using Guardrail.Api.Http;
using Guardrail.Api.RateLimiting;
using Guardrail.Application.Catalogue;
using Guardrail.Application.Common;
using Guardrail.Application.Merchants;
using Guardrail.Application.Policies;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;

namespace Guardrail.Api.Endpoints;

public sealed record UpdatePolicyRequest(int? ExpectedVersionNumber, string? ChangeSummary, PolicyDocument? Policy);

public sealed record ExpectedVersionRequest(int? ExpectedVersionNumber);

public static class PolicyAndCatalogueEndpoints
{
    public static void MapPolicyAndCatalogueEndpoints(this RouteGroupBuilder api)
    {
        // ------------------------------------------------------------- policies (user only; BR-04)
        var policies = api.MapGroup("/policies").WithTags("Policies");
        policies.MapGet("/", async (PolicyService service, HttpContext ctx) =>
            (await service.GetCurrentAsync(ctx.UserId(), ctx.RequestAborted)).ToHttp(ctx));

        policies.MapPut("/", async (UpdatePolicyRequest body, PolicyService service, HttpContext ctx) =>
        {
            if (body.ExpectedVersionNumber is not { } expected || body.Policy is null)
            {
                return Problems.Result(DomainError.Validation("expectedVersionNumber and policy are required."), ctx);
            }

            if (body.ChangeSummary is { Length: > 300 })
            {
                return Problems.Result(DomainError.Validation("changeSummary must be at most 300 characters."), ctx);
            }

            return (await service.UpdateAsync(ctx.UserId(), new UpdatePolicyCommand(expected, body.ChangeSummary, body.Policy), ctx.RequestAborted))
                .ToHttp(ctx);
        }).RequireRateLimiting(RateLimitPolicies.Mutations);

        policies.MapGet("/versions", async (int? page, int? pageSize, PolicyService service, HttpContext ctx) =>
            Results.Json(await service.ListVersionsAsync(ctx.UserId(), new PageRequest(page ?? 1, pageSize ?? 20), ctx.RequestAborted),
                JsonDefaults.Options));

        policies.MapGet("/versions/{versionNumber:int}", async (int versionNumber, PolicyService service, HttpContext ctx) =>
            (await service.GetVersionAsync(ctx.UserId(), versionNumber, ctx.RequestAborted)).ToHttp(ctx));

        // ------------------------------------------------------------- merchants
        var merchants = api.MapGroup("/merchants").WithTags("Merchants");
        merchants.MapGet("/", async (string? query, string? categoryCode, string? status, MerchantQueries service, HttpContext ctx) =>
        {
            MerchantStatus? parsed = null;
            if (status is not null)
            {
                if (!EnumText.TryParse<MerchantStatus>(status, out var s))
                {
                    return Problems.Result(DomainError.Validation("status must be APPROVED, BLOCKED or NEW."), ctx);
                }

                parsed = s;
            }

            return Results.Json(await service.ListAsync(ctx.UserId(), query, categoryCode, parsed, ctx.RequestAborted), JsonDefaults.Options);
        });

        merchants.MapGet("/{id:guid}", async (Guid id, MerchantQueries service, HttpContext ctx) =>
            (await service.GetAsync(ctx.UserId(), id, ctx.RequestAborted)).ToHttp(ctx));

        merchants.MapPost("/{id:guid}/approve", (Guid id, ExpectedVersionRequest body, MerchantService service, HttpContext ctx) =>
            Change(body, ctx, v => service.ApproveAsync(ctx.UserId(), id, v, ctx.RequestAborted))).RequireRateLimiting(RateLimitPolicies.Mutations);
        merchants.MapPost("/{id:guid}/block", (Guid id, ExpectedVersionRequest body, MerchantService service, HttpContext ctx) =>
            Change(body, ctx, v => service.BlockAsync(ctx.UserId(), id, v, ctx.RequestAborted))).RequireRateLimiting(RateLimitPolicies.Mutations);
        merchants.MapPost("/{id:guid}/reset", (Guid id, ExpectedVersionRequest body, MerchantService service, HttpContext ctx) =>
            Change(body, ctx, v => service.ResetAsync(ctx.UserId(), id, v, ctx.RequestAborted))).RequireRateLimiting(RateLimitPolicies.Mutations);

        // ------------------------------------------------------------- catalogue
        api.MapGet("/categories", async (CatalogueService service, HttpContext ctx) =>
            Results.Json(await service.CategoriesAsync(ctx.RequestAborted), JsonDefaults.Options)).WithTags("Catalogue");

        api.MapGet("/products", async (string? query, string? categoryCode, Guid? merchantId, string? maxUnitPrice, int? page, int? pageSize,
            CatalogueService service, HttpContext ctx) =>
        {
            decimal? max = null;
            if (maxUnitPrice is not null)
            {
                if (!MoneyFormat.TryParseLoose(maxUnitPrice, out var m))
                {
                    return Problems.Result(DomainError.Validation("maxUnitPrice must look like 12 or 12.50."), ctx);
                }

                max = m;
            }

            if (query is { Length: > 100 })
            {
                return Problems.Result(DomainError.Validation("query must be at most 100 characters."), ctx);
            }

            var result = await service.SearchAsync(ctx.UserId(), new ProductQuery(query, categoryCode, merchantId, max),
                new PageRequest(page ?? 1, pageSize ?? 20), ctx.RequestAborted);
            return Results.Json(result, JsonDefaults.Options);
        }).WithTags("Catalogue");

        api.MapGet("/products/{id:guid}", async (Guid id, CatalogueService service, HttpContext ctx) =>
            (await service.GetAsync(ctx.UserId(), id, ctx.RequestAborted)).ToHttp(ctx)).WithTags("Catalogue");
    }

    private static async Task<IResult> Change(ExpectedVersionRequest body, HttpContext ctx, Func<int, Task<Result<Application.Dtos.MerchantChangeDto>>> action)
    {
        if (body.ExpectedVersionNumber is not { } version)
        {
            return Problems.Result(DomainError.Validation("expectedVersionNumber is required."), ctx);
        }

        return (await action(version)).ToHttp(ctx);
    }
}
