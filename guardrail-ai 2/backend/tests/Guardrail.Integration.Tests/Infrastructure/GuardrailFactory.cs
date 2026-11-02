using System.Security.Cryptography;
using Guardrail.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Guardrail.Integration.Tests.Infrastructure;

/// <summary>Real PostgreSQL 16 (Testcontainers) + the real API pipeline, with PayPal, webhook verifier, LLM and clock faked.</summary>
public sealed class GuardrailFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string JobToken = "test-job-token-0123456789abcdef0123456789abcdef";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("guardrail")
        .WithUsername("guardrail")
        .WithPassword("guardrail")
        .Build();

    public FakePayPalClient PayPal { get; } = new();

    public FakeWebhookVerifier WebhookVerifier { get; } = new();

    public ScriptedLlmClient Llm { get; } = new();

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public string Intent { get; init; } = "AUTHORIZE";

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Database:SeedOnStartup", "true");
        builder.UseSetting("Jwt:Issuer", "https://test.guardrail");
        builder.UseSetting("Jwt:Audience", "guardrail-web");
        builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
        builder.UseSetting("PayPal:AllowMissingCredentials", "true");
        builder.UseSetting("PayPal:Intent", Intent);
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("Jobs:InternalToken", JobToken);
        builder.UseSetting("RateLimiting:Enabled", "false");
        builder.UseSetting("Seed:CreateDemoUser", "false");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IPayPalClient>();
            services.AddSingleton<IPayPalClient>(PayPal);
            services.RemoveAll<IPayPalWebhookVerifier>();
            services.AddSingleton<IPayPalWebhookVerifier>(WebhookVerifier);
            services.RemoveAll<ILlmClient>();
            services.AddSingleton<ILlmClient>(Llm);
        });
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<GuardrailFactory>
{
    public const string Name = "api";
}
