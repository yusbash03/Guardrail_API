using Guardrail.Application.Common;
using Guardrail.Domain.Common;

namespace Guardrail.Api.Http;

/// <summary>A materialized response (status + JSON body) so idempotent endpoints can store and replay it verbatim.</summary>
public sealed record ApiResponse(int Status, object? Body, bool IsProblem, IReadOnlyDictionary<string, string>? Headers = null)
{
    public IResult ToResult()
    {
        if (Status == StatusCodes.Status204NoContent)
        {
            return Results.NoContent();
        }

        return new HeaderedJsonResult(this);
    }

    private sealed class HeaderedJsonResult(ApiResponse response) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (response.Headers is not null)
            {
                foreach (var (key, value) in response.Headers)
                {
                    httpContext.Response.Headers[key] = value;
                }
            }

            return Results.Json(response.Body, JsonDefaults.Options, response.IsProblem ? "application/problem+json" : "application/json", response.Status)
                .ExecuteAsync(httpContext);
        }
    }
}

/// <summary>RFC 9457 problem details with a stable machine-readable <c>code</c> (ERROR_HANDLING.md §1).</summary>
public static class Problems
{
    private static readonly Dictionary<string, int> StatusByCode = new(StringComparer.Ordinal)
    {
        [ErrorCodes.ValidationFailed] = 400,
        [ErrorCodes.Unauthenticated] = 401,
        [ErrorCodes.ReauthenticationFailed] = 401,
        [ErrorCodes.Forbidden] = 403,
        [ErrorCodes.NotFound] = 404,
        [ErrorCodes.InvalidState] = 409,
        [ErrorCodes.PolicyVersionConflict] = 409,
        [ErrorCodes.CartLocked] = 409,
        [ErrorCodes.CartNotOpen] = 409,
        [ErrorCodes.CartChanged] = 409,
        [ErrorCodes.EvaluationStale] = 409,
        [ErrorCodes.ApprovalNotPending] = 409,
        [ErrorCodes.ApprovalNotApproved] = 409,
        [ErrorCodes.ApprovalSnapshotMismatch] = 409,
        [ErrorCodes.ApprovalInvalidated] = 409,
        [ErrorCodes.PaymentNotApproved] = 409,
        [ErrorCodes.IdempotencyInProgress] = 409,
        [ErrorCodes.LedgerInvalidTransition] = 409,
        [ErrorCodes.ApprovalExpired] = 410,
        [ErrorCodes.EvaluationExpired] = 410,
        [ErrorCodes.CartVersionMismatch] = 412,
        [ErrorCodes.PolicyInvalid] = 422,
        [ErrorCodes.PolicyMerchantConflict] = 422,
        [ErrorCodes.CurrencyMismatch] = 422,
        [ErrorCodes.EvaluationBlocked] = 422,
        [ErrorCodes.NoActivePolicy] = 422,
        [ErrorCodes.MixedMerchantCart] = 422,
        [ErrorCodes.ProductNotFound] = 422,
        [ErrorCodes.ProductUnavailable] = 422,
        [ErrorCodes.CartLimitExceeded] = 422,
        [ErrorCodes.IdempotencyKeyReused] = 422,
        [ErrorCodes.PaymentInstrumentDeclined] = 422,
        [ErrorCodes.AmountMismatch] = 422,
        [ErrorCodes.FinalCheckFailed] = 422,
        [ErrorCodes.RateLimited] = 429,
        [ErrorCodes.ApprovalStepUpLocked] = 429,
        [ErrorCodes.CaptureInProgress] = 202,
        [ErrorCodes.PaymentProviderUnavailable] = 503,
        [ErrorCodes.AgentUnavailable] = 503,
        [ErrorCodes.InternalError] = 500,
    };

    public static int StatusOf(string code) => StatusByCode.GetValueOrDefault(code, 400);

    public static ApiResponse From(DomainError error, HttpContext context)
    {
        var status = StatusOf(error.Code);
        var slug = error.Code.ToLowerInvariant().Replace('_', '-');
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"https://guardrail.ai/errors/{slug}",
            ["title"] = Title(error.Code),
            ["status"] = status,
            ["detail"] = error.Message,
            ["instance"] = context.Request.Path.Value,
            ["code"] = error.Code,
            ["correlationId"] = context.Items[CorrelationIdMiddleware.ItemKey] as string,
            ["errors"] = error.ValidationErrors,
            ["data"] = error.Data,
        };

        Dictionary<string, string>? headers = null;
        if (error.Data?.TryGetValue("retryAfterSeconds", out var retry) == true && retry is not null)
        {
            headers = new Dictionary<string, string> { ["Retry-After"] = retry.ToString()! };
        }
        else if (status is 429 or 503)
        {
            headers = new Dictionary<string, string> { ["Retry-After"] = "30" };
        }

        return new ApiResponse(status, body, IsProblem: status >= 400, headers);
    }

    public static IResult Result(DomainError error, HttpContext context) => From(error, context).ToResult();

    private static string Title(string code) =>
        string.Join(' ', code.Split('_').Select((w, i) => i == 0 ? w[0] + w[1..].ToLowerInvariant() : w.ToLowerInvariant()));
}

public static class ResultExtensions
{
    public static ApiResponse ToApi<T>(this Result<T> result, HttpContext context, int successStatus = StatusCodes.Status200OK) =>
        result.IsSuccess ? new ApiResponse(successStatus, result.Value, false) : Problems.From(result.Error!, context);

    public static IResult ToHttp<T>(this Result<T> result, HttpContext context, int successStatus = StatusCodes.Status200OK) =>
        result.ToApi(context, successStatus).ToResult();
}
