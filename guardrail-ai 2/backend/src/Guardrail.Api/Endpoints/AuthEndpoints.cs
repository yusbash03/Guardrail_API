using Guardrail.Api.Http;
using Guardrail.Api.RateLimiting;
using Guardrail.Application.Auth;
using Guardrail.Domain.Common;
using Microsoft.Extensions.Options;

namespace Guardrail.Api.Endpoints;

public sealed record PatchProfileRequest(string? TimeZone);

public static class AuthEndpoints
{
    public const string RefreshCookie = "gr_rt";
    private const string CookiePath = "/api/v1/auth";

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/register", async (RegisterCommand body, AuthService service, HttpContext ctx) =>
        {
            var result = await service.RegisterAsync(body, ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
            return Session(result, ctx, StatusCodes.Status201Created);
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        auth.MapPost("/login", async (LoginCommand body, AuthService service, HttpContext ctx) =>
        {
            var result = await service.LoginAsync(body, ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
            return Session(result, ctx, StatusCodes.Status200OK);
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        // SEC-04: cookie-authenticated, so it requires the custom header and a same-origin Origin (CSRF defence).
        auth.MapPost("/refresh", async (AuthService service, HttpContext ctx, IOptions<CorsSettings> cors) =>
        {
            if (!ctx.HasWebClientHeader() || !IsSameOrigin(ctx, cors.Value))
            {
                return Problems.Result(new DomainError(ErrorCodes.Forbidden, "Missing X-Guardrail-Client header or cross-origin request."), ctx);
            }

            var result = await service.RefreshAsync(ctx.Request.Cookies[RefreshCookie], ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                ClearCookie(ctx);
            }

            return Session(result, ctx, StatusCodes.Status200OK);
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        auth.MapPost("/logout", async (AuthService service, HttpContext ctx) =>
        {
            await service.LogoutAsync(ctx.UserId(), ctx.Request.Cookies[RefreshCookie], ctx.RequestAborted);
            ClearCookie(ctx);
            return Results.NoContent();
        });

        api.MapGet("/me", async (AuthService service, HttpContext ctx) =>
            (await service.MeAsync(ctx.UserId(), ctx.RequestAborted)).ToHttp(ctx)).WithTags("Auth");

        var profile = api.MapGroup("/spending-profile").WithTags("Spending profile");
        profile.MapGet("/", async (ProfileService service, HttpContext ctx) =>
            (await service.GetAsync(ctx.UserId(), ctx.RequestAborted)).ToHttp(ctx));
        profile.MapPatch("/", async (PatchProfileRequest body, ProfileService service, HttpContext ctx) =>
            (await service.UpdateTimeZoneAsync(ctx.UserId(), body.TimeZone, ctx.RequestAborted)).ToHttp(ctx))
            .RequireRateLimiting(RateLimitPolicies.Mutations);
    }

    private static IResult Session(Application.Common.Result<AuthSession> result, HttpContext ctx, int status)
    {
        if (!result.IsSuccess)
        {
            return Problems.Result(result.Error!, ctx);
        }

        var session = result.Value!;
        ctx.Response.Cookies.Append(RefreshCookie, session.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            MaxAge = session.RefreshTokenExpiresAt - DateTimeOffset.UtcNow,
            IsEssential = true,
        });
        return new ApiResponse(status, session.Response, false).ToResult();
    }

    private static void ClearCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = CookiePath, Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict });

    private static bool IsSameOrigin(HttpContext ctx, CorsSettings cors)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true; // same-origin fetches may omit Origin; the custom header still blocks simple cross-site posts
        }

        if (cors.AllowedOrigins.Contains("*") || cors.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && string.Equals(uri.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CorsSettings
{
    public const string Section = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}
