using FluentValidation;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Agent;
using Guardrail.Application.Approvals;
using Guardrail.Application.Auth;
using Guardrail.Application.Carts;
using Guardrail.Application.Catalogue;
using Guardrail.Application.Common;
using Guardrail.Application.Dashboard;
using Guardrail.Application.Evaluation;
using Guardrail.Application.Insights;
using Guardrail.Application.Jobs;
using Guardrail.Application.Ledger;
using Guardrail.Application.Merchants;
using Guardrail.Application.Payments;
using Guardrail.Application.Policies;
using Guardrail.Application.Purchases;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Guardrail.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ApprovalOptions>(configuration.GetSection(ApprovalOptions.Section));
        services.Configure<EvaluationOptions>(configuration.GetSection(EvaluationOptions.Section));
        services.Configure<CheckoutOptions>(configuration.GetSection(CheckoutOptions.Section));
        services.Configure<CheckoutOptions>(o =>
        {
            // PayPal:Intent / ReturnUrl / CancelUrl / BrandName live in the PayPal section (ENVIRONMENT_CONFIGURATION.md).
            var payPal = configuration.GetSection("PayPal");
            o.Intent = payPal["Intent"] ?? o.Intent;
            o.ReturnUrl = payPal["ReturnUrl"] ?? o.ReturnUrl;
            o.CancelUrl = payPal["CancelUrl"] ?? o.CancelUrl;
            o.BrandName = payPal["BrandName"] ?? o.BrandName;
        });
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));
        services.Configure<DemoOptions>(configuration.GetSection(DemoOptions.Section));

        services.AddValidatorsFromAssemblyContaining<PolicyDocumentValidator>(ServiceLifetime.Singleton, includeInternalTypes: false);
        foreach (var validator in typeof(DependencyInjection).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } && t.BaseType is { IsGenericType: true } b
                                 && b.GetGenericTypeDefinition() == typeof(AbstractValidator<>)))
        {
            services.AddSingleton(validator);
        }

        services.AddScoped<AuditWriter>();
        services.AddScoped<UserProvisioner>();
        services.AddScoped<AuthService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<PolicyLoader>();
        services.AddScoped<PolicyService>();
        services.AddScoped<MerchantQueries>();
        services.AddScoped<MerchantService>();
        services.AddScoped<CatalogueService>();
        services.AddScoped<CartService>();
        services.AddScoped<EvaluationService>();
        services.AddScoped<LedgerService>();
        services.AddScoped<SpendingQueries>();
        services.AddScoped<PurchaseLifecycle>();
        services.AddScoped<PurchaseQueries>();
        services.AddScoped<PurchaseService>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<PaymentStateApplier>();
        services.AddScoped<CheckoutService>();
        services.AddScoped<PaymentCompletionService>();
        services.AddScoped<WebhookService>();
        services.AddScoped<JobRunner>();
        services.AddScoped<DashboardService>();
        services.AddScoped<AuditQueries>();
        services.AddScoped<InsightsService>();

        foreach (var toolType in ToolRegistry.ToolTypes)
        {
            services.AddScoped(typeof(IAgentTool), toolType);
        }

        services.AddScoped<ToolRegistry>();
        services.AddScoped<ToolDispatcher>();
        services.AddScoped<CardHydrator>();
        services.AddScoped<AgentOrchestrator>();
        services.AddScoped<ConversationService>();
        return services;
    }
}
