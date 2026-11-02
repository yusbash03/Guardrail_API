using Guardrail.Application.Agent;
using NetArchTest.Rules;

namespace Guardrail.Architecture.Tests;

/// <summary>Layer rules (ARCHITECTURE.md §4) and the agent's closed reach (AI_GUARDRAILS.md §3.1, SEC-20, SEC-37).</summary>
public class ArchitectureTests
{
    private static readonly System.Reflection.Assembly Domain = typeof(Guardrail.Domain.Policy.PolicyEngine).Assembly;
    private static readonly System.Reflection.Assembly Application = typeof(Guardrail.Application.DependencyInjection).Assembly;
    private static readonly System.Reflection.Assembly Infrastructure = typeof(Guardrail.Infrastructure.DependencyInjection).Assembly;

    [Fact]
    public void Domain_ReferencesNothingElse()
    {
        var result = Types.InAssembly(Domain).ShouldNot()
            .HaveDependencyOnAny("Guardrail.Application", "Guardrail.Infrastructure", "Guardrail.Api", "Microsoft.EntityFrameworkCore", "Npgsql",
                "Microsoft.AspNetCore", "System.Net.Http")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Failures(result));
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrApi()
    {
        var result = Types.InAssembly(Application).ShouldNot()
            .HaveDependencyOnAny("Guardrail.Infrastructure", "Guardrail.Api", "Npgsql")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Failures(result));
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnApi()
    {
        var result = Types.InAssembly(Infrastructure).ShouldNot().HaveDependencyOn("Guardrail.Api").GetResult();
        result.IsSuccessful.ShouldBeTrue(Failures(result));
    }

    [Fact]
    public void PolicyEngine_HasNoIoDependencies()
    {
        var result = Types.InAssembly(Domain).That().ResideInNamespace("Guardrail.Domain.Policy").ShouldNot()
            .HaveDependencyOnAny("System.IO", "System.Net", "System.Threading.Tasks.Task")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Failures(result));
    }

    [Fact]
    public void AgentTools_CannotReachPaymentApprovalPolicyWriteOrLedgerWrite()
    {
        var result = Types.InAssembly(Application).That().ResideInNamespace("Guardrail.Application.Agent.Tools").ShouldNot()
            .HaveDependencyOnAny(
                "Guardrail.Application.Abstractions.IPayPalClient",
                "Guardrail.Application.Abstractions.IPayPalWebhookVerifier",
                "Guardrail.Application.Approvals",
                "Guardrail.Application.Payments",
                "Guardrail.Application.Policies.PolicyService",
                "Guardrail.Application.Merchants.MerchantService",
                "Guardrail.Application.Ledger.LedgerService",
                "Guardrail.Domain.Ledger.SpendingLedgerEntry",
                "Guardrail.Infrastructure")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Failures(result));
    }

    [Fact]
    public void Agent_HasNoPayPalToolkitOrMcpClient()
    {
        var result = Types.InAssembly(Application).That().ResideInNamespaceStartingWith("Guardrail.Application.Agent").ShouldNot()
            .HaveDependencyOnAny("PayPal", "ModelContextProtocol", "Guardrail.Application.Abstractions.IPayPalClient")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Failures(result));
    }

    [Fact]
    public void ToolRegistry_ListsExactlyTheElevenContractTools()
    {
        ToolRegistry.ToolTypes.Count.ShouldBe(11);
        ToolRegistry.ToolTypes.Select(t => t.Name).ShouldNotContain(n =>
            n.Contains("Approve", StringComparison.Ordinal) && !n.Contains("Approved", StringComparison.Ordinal)
            || n.Contains("Checkout", StringComparison.Ordinal) || n.Contains("Capture", StringComparison.Ordinal)
            || n.Contains("UpdatePolicy", StringComparison.Ordinal) || n.Contains("Block", StringComparison.Ordinal));
        // Every IAgentTool in the assembly is registered — no hidden tool can exist outside the allowlist.
        var toolTypes = Application.GetTypes().Where(t => typeof(IAgentTool).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }).ToList();
        toolTypes.Select(t => t.FullName).Order().ShouldBe(ToolRegistry.ToolTypes.Select(t => t.FullName).Order());
    }

    private static string Failures(TestResult result) =>
        result.IsSuccessful ? string.Empty : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
