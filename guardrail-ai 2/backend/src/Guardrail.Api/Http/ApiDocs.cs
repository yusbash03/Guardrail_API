using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Guardrail.Api.Http;

/// <summary>
/// Interactive API docs: Scalar at /scalar and Swagger UI at /swagger, both reading /openapi/v1.json.
/// On by default outside Production; set ApiDocs:Enabled=true to publish them in Production (e.g. a hackathon demo).
/// </summary>
public static class ApiDocs
{
    public const string BearerScheme = "Bearer";

    public static bool IsEnabled(WebApplication app) =>
        app.Configuration.GetValue("ApiDocs:Enabled", !app.Environment.IsProduction());

    /// <summary>Declares JWT bearer auth in the OpenAPI document so Scalar/Swagger can send the access token.</summary>
    public static OpenApiOptions AddBearerSecurity(this OpenApiOptions options) =>
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Access token from POST /api/v1/auth/login (accessToken).",
            };
            document.Security ??= [];
            document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, document)] = [] });
            return Task.CompletedTask;
        });

    public static void UseSwaggerDocs(this WebApplication app)
    {
        // Static middleware (not an endpoint), so it must run before the authenticated-by-default fallback policy.
        app.UseSwaggerUI(o =>
        {
            o.SwaggerEndpoint("/openapi/v1.json", "Guardrail AI v1");
            o.RoutePrefix = "swagger";
        });
    }

    public static void MapScalar(this WebApplication app) =>
        app.MapScalarApiReference("/scalar", o => o
                .WithTitle("Guardrail AI API")
                .WithOpenApiRoutePattern("/openapi/{documentName}.json")
                .AddPreferredSecuritySchemes(BearerScheme))
            .AllowAnonymous();
}
