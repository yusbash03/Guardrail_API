using System.Security.Claims;
using System.Threading.RateLimiting;
using Guardrail.Api.Http;
using Guardrail.Domain.Common;
using Microsoft.AspNetCore.RateLimiting;

namespace Guardrail.Api.RateLimiting;

/// <summary>API_CONTRACT.md rate limits (SEC-70). Exceeded → 429 RATE_LIMITED with Retry-After.</summary>
public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string Agent = "agent";
    public const string Mutations = "mutations";
    public const string Webhook = "webhook";

    public static IServiceCollection AddGuardrailRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue("RateLimiting:Enabled", true);
        services.AddRateLimiter(options =>
        {
            options.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? (int)Math.Ceiling(ra.TotalSeconds) : 60;
                context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();
                var error = new DomainError(ErrorCodes.RateLimited, "Too many requests. Slow down and try again shortly.",
                    new Dictionary<string, object?> { ["retryAfterSeconds"] = retryAfter });
                await Problems.Result(error, context.HttpContext).ExecuteAsync(context.HttpContext);
            };

            options.AddPolicy(Auth, ctx => Fixed($"ip:{Ip(ctx)}", enabled ? 10 : 100_000, TimeSpan.FromMinutes(1)));
            options.AddPolicy(Mutations, ctx => Fixed($"user:{User(ctx)}", enabled ? 60 : 100_000, TimeSpan.FromMinutes(1)));
            options.AddPolicy(Webhook, ctx => Fixed($"ip:{Ip(ctx)}", enabled ? 120 : 100_000, TimeSpan.FromMinutes(1)));
            options.AddPolicy<string, AgentTurnPolicy>(Agent);

            // Global "reads" limit: 300/min per user (or IP when anonymous).
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter($"reads:{User(ctx) ?? Ip(ctx)}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = enabled ? 300 : 100_000,
                    Window = TimeSpan.FromMinutes(1),
                }));
        });
        return services;
    }

    private static RateLimitPartition<string> Fixed(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window });

    internal static string? User(HttpContext ctx) => ctx.User.FindFirstValue("sub");

    internal static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Agent turns: 20 per 10 minutes and 1 concurrent turn per user (BR-83, SEC-71).</summary>
public sealed class AgentTurnPolicy(IConfiguration configuration) : IRateLimiterPolicy<string>
{
    private readonly bool _enabled = configuration.GetValue("RateLimiting:Enabled", true);

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        var user = RateLimitPolicies.User(httpContext) ?? RateLimitPolicies.Ip(httpContext);
        var enabled = _enabled;
        return RateLimitPartition.Get(user, _ => new ChainedLimiter(
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = enabled ? 20 : 100_000,
                Window = TimeSpan.FromMinutes(10),
                SegmentsPerWindow = 10,
            }),
            new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 })));
    }

    /// <summary>Acquires both limiters; releases the first if the second rejects.</summary>
    private sealed class ChainedLimiter(RateLimiter window, RateLimiter concurrency) : RateLimiter
    {
        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics? GetStatistics() => concurrency.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            var first = concurrency.AttemptAcquire(permitCount);
            if (!first.IsAcquired)
            {
                return first;
            }

            var second = window.AttemptAcquire(permitCount);
            if (second.IsAcquired)
            {
                return new Combined(first, second);
            }

            first.Dispose();
            return second;
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AttemptAcquireCore(permitCount));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                window.Dispose();
                concurrency.Dispose();
            }

            base.Dispose(disposing);
        }

        private sealed class Combined(RateLimitLease a, RateLimitLease b) : RateLimitLease
        {
            public override bool IsAcquired => true;

            public override IEnumerable<string> MetadataNames => b.MetadataNames;

            public override bool TryGetMetadata(string metadataName, out object? metadata) => b.TryGetMetadata(metadataName, out metadata);

            protected override void Dispose(bool disposing)
            {
                a.Dispose();
                b.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
