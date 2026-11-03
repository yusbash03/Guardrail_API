using System.Text;
using Guardrail.Api;
using Guardrail.Api.Endpoints;
using Guardrail.Api.Http;
using Guardrail.Api.RateLimiting;
using Guardrail.Application;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Domain.Common;
using Guardrail.Infrastructure;
using Guardrail.Infrastructure.Persistence;
using Guardrail.Infrastructure.Persistence.Seed;
using Guardrail.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- logging (Serilog JSON; no bodies, tokens or prompts at Info)
builder.Host.UseSerilog((context, services, logger) =>
{
    logger.ReadFrom.Configuration(context.Configuration).Enrich.FromLogContext();
    if (context.HostingEnvironment.IsDevelopment())
    {
        logger.WriteTo.Console();
    }
    else
    {
        logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
});

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024); // SEC-50 global cap

// ---------------------------------------------------------------- services
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<Idempotency>();
builder.Services.AddExceptionHandler<ExceptionToProblemDetails>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
builder.Services.Configure<CorsSettings>(builder.Configuration.GetSection(CorsSettings.Section));
builder.Services.AddGuardrailRateLimiting(builder.Configuration);
builder.Services.AddOpenApi("v1", o => o.AddBearerSecurity());

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwt.KeyBytes().Length > 0 ? jwt.KeyBytes() : Encoding.UTF8.GetBytes(new string('x', 64))),
            NameClaimType = "sub",
            RoleClaimType = "role",
        };
        o.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await Problems.Result(new DomainError(ErrorCodes.Unauthenticated, "Missing, invalid or expired access token."), context.HttpContext)
                    .ExecuteAsync(context.HttpContext);
            },
            OnForbidden = context => Problems.Result(new DomainError(ErrorCodes.Forbidden, "You are not allowed to do this."), context.HttpContext)
                .ExecuteAsync(context.HttpContext),
        };
    });

// Lifetime validation uses the injected TimeProvider, like every other time check (CODING_STANDARDS.md).
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TimeProvider>((o, clock) =>
    o.TokenValidationParameters.LifetimeValidator = (notBefore, expires, _, parameters) =>
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return (notBefore is null || notBefore.Value <= now + parameters.ClockSkew)
               && (expires is null || expires.Value >= now - parameters.ClockSkew);
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()) // SEC-10
    .AddPolicy(AuthPolicies.Admin, p => p.RequireAuthenticatedUser().RequireClaim("role", "ADMIN"));

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials().WithExposedHeaders("X-Correlation-Id", "Retry-After")));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); // behind Render's proxy
    o.KnownProxies.Clear();
});

var app = builder.Build();

// ---------------------------------------------------------------- database (migrations are a separate step in production, ADR-014)
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
    {
        await db.Database.MigrateAsync();
    }

    if (app.Configuration.GetValue("Database:SeedOnStartup", true))
    {
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync(CancellationToken.None);
    }
}

// ---------------------------------------------------------------- pipeline
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts(); // SEC-60
}

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.CacheControl = "no-store"; // ADR-013: API responses are never cached
    await next();
});

app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error : Serilog.Events.LogEventLevel.Information);
var apiDocs = ApiDocs.IsEnabled(app);
if (apiDocs)
{
    app.UseSwaggerDocs();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
if (apiDocs)
{
    app.MapScalar();
}

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapPolicyAndCatalogueEndpoints();
api.MapShoppingEndpoints();
api.MapLedgerAndAgentEndpoints();
api.MapOpsEndpoints();

app.Run();

public partial class Program;
