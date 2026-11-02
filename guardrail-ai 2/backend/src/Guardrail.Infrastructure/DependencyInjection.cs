using Guardrail.Application.Abstractions;
using Guardrail.Infrastructure.Admin;
using Guardrail.Infrastructure.Catalogue;
using Guardrail.Infrastructure.Jobs;
using Guardrail.Infrastructure.Llm;
using Guardrail.Infrastructure.PayPal;
using Guardrail.Infrastructure.Persistence;
using Guardrail.Infrastructure.Persistence.Seed;
using Guardrail.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Guardrail.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Default is required (env ConnectionStrings__Default).");
        }
        services.AddDbContext<AppDbContext>(o => o
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(0))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICatalogueProvider, SeedCatalogueProvider>();
        services.AddScoped<DatabaseSeeder>();
        services.AddScoped<DemoResetService>();

        // Security
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IPasswordService, PasswordService>();

        // PayPal (Sandbox only)
        services.AddOptions<PayPalOptions>().Bind(configuration.GetSection(PayPalOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<PayPalOptions>, PayPalOptionsValidator>();
        services.AddSingleton<PayPalTokenProvider>();
        services.AddScoped<IPayPalClient, PayPalClient>();
        services.AddScoped<IPayPalWebhookVerifier, PayPalWebhookVerifier>();
        services.AddHttpClient(PayPalClient.HttpClientName, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<PayPalOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan; // resilience pipeline owns timeouts
            })
            .AddStandardResilienceHandler(o =>
            {
                // POSTs are only retried with the same PayPal-Request-Id (the same request message) — ERROR_HANDLING.md §3.
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.UseJitter = true;
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            });

        // LLM (free tier: Gemini → Groq)
        services.Configure<LlmSettings>(configuration.GetSection(LlmSettings.Section));
        services.AddSingleton<LlmRateLimiters>();
        services.AddScoped<ILlmClient, RateLimitedLlmRouter>();
        foreach (var (name, select) in new (string, Func<LlmSettings, LlmProviderOptions>)[]
                 {
                     (RateLimitedLlmRouter.PrimaryClientName, s => s.Primary),
                     (RateLimitedLlmRouter.FallbackClientName, s => s.Fallback),
                 })
        {
            services.AddHttpClient(name, (sp, client) =>
            {
                var settings = sp.GetRequiredService<IOptions<LlmSettings>>().Value;
                var provider = select(settings);
                if (Uri.TryCreate(provider.BaseUrl.EndsWith('/') ? provider.BaseUrl : provider.BaseUrl + "/", UriKind.Absolute, out var baseUri))
                {
                    client.BaseAddress = baseUri;
                }

                client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
            });
        }

        // Background jobs
        services.Configure<JobsOptions>(configuration.GetSection(JobsOptions.Section));
        services.AddHostedService<ApprovalExpirySweeper>();
        services.AddHostedService<CheckoutExpirySweeper>();
        services.AddHostedService<WebhookProcessor>();
        services.AddHostedService<PaymentReconciler>();
        return services;
    }
}
