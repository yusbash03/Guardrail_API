using System.Net;
using System.Text.Json;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Agent;
using Guardrail.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static Guardrail.Integration.Tests.Infrastructure.ScriptedLlmClient;

namespace Guardrail.Integration.Tests;

[Collection(ApiCollection.Name)]
public class AgentTests(GuardrailFactory factory)
{
    [Fact]
    public async Task IT19_J1_ScriptedAgentTurn_BuildsCartEvaluatesAndRequestsApproval()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var conversation = await user.PostAsync("/api/v1/agent/conversations", new { title = "Groceries" });
        var items = string.Join(",", TestUser.DemoSkus.Select(s => $$"""{"productId":"{{TestUser.Product(s)}}","quantity":1}"""));

        factory.Llm.Script(
            _ => Tools(("GetRemainingBudget", """{"categoryCode":"GROCERIES"}"""), ("GetApprovedMerchants", "{}")),
            _ => Tools(("SearchProducts", """{"queries":["milk","eggs","whole wheat bread","chicken breast","jasmine rice","broccoli","carrots","spinach","bananas","greek yogurt"],"approvedMerchantsOnly":true}""")),
            _ => Tools(("CreateShoppingCart", $$"""{"merchantId":"{{TestUser.Merchant("freshmart")}}","items":[{{items}}]}""")),
            m => Tools(("EvaluatePurchase", $$"""{"cartId":"{{LastData(m).GetProperty("cartId")}}"}""")),
            m => Tools(("RequestPurchaseApproval",
                $$"""{"cartId":"{{LastData(m).GetProperty("cartId")}}","evaluationId":"{{LastData(m).GetProperty("evaluationId")}}"}""")),
            _ => Text("I built a $43.00 cart at FreshMart. It's above your $30 approval threshold, so it needs your OK. You'd have $37.00 left this week."));

        var turn = await user.PostAsync($"/api/v1/agent/conversations/{conversation.Id()}/messages",
            new { content = "Get my groceries for the week: milk, eggs, whole wheat bread, chicken breast, jasmine rice, broccoli, carrots, spinach, bananas and Greek yogurt. Stay within budget." });

        turn.Status.ShouldBe(HttpStatusCode.OK, turn.Body.ToString());
        var limits = turn.Body.GetProperty("limits");
        limits.GetProperty("llmRequestsUsed").GetInt32().ShouldBe(6);
        limits.GetProperty("toolCallsUsed").GetInt32().ShouldBe(6);
        turn.Body.GetProperty("toolCalls").EnumerateArray().ShouldAllBe(c => c.GetProperty("status").GetString() == "SUCCEEDED");

        var cards = turn.Body.GetProperty("assistantMessage").GetProperty("cards").EnumerateArray().ToList();
        var evaluation = cards.Single(c => c.GetProperty("type").GetString() == "EVALUATION").GetProperty("data");
        evaluation.GetProperty("decision").GetString().ShouldBe("REQUIRES_APPROVAL");
        evaluation.GetProperty("remainingBudget").GetString().ShouldBe("37.00");
        var approval = cards.Single(c => c.GetProperty("type").GetString() == "APPROVAL").GetProperty("data");
        approval.GetProperty("status").GetString().ShouldBe("PENDING");
        approval.GetProperty("amount").GetString().ShouldBe("43.00");
        approval.GetProperty("requestedBy").GetString().ShouldBe("AGENT");
        cards.Single(c => c.GetProperty("type").GetString() == "CART").GetProperty("data").GetProperty("createdBy").GetString().ShouldBe("AGENT");

        // Messages endpoint hydrates the same cards from the database.
        var messages = await user.GetAsync($"/api/v1/agent/conversations/{conversation.Id()}/messages");
        messages.Body.EnumerateArray().Last().GetProperty("cards").GetArrayLength().ShouldBe(cards.Count);
    }

    [Fact]
    public async Task IT20_AskAgentToApprove_NothingChanges_AndClaimIsAnnotated()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateDemoCartAsync();
        var evaluation = await user.EvaluateAsync(cart.Id());
        var purchase = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));
        var approvalId = purchase.Body.GetProperty("approval").GetProperty("id").GetString();
        var conversation = await user.PostAsync("/api/v1/agent/conversations", new { });

        factory.Llm.Script(
            _ => Tools(("ApprovePurchase", $$"""{"approvalId":"{{approvalId}}"}""")),
            _ => Text("Done — I approved and paid for it."));

        var turn = await user.PostAsync($"/api/v1/agent/conversations/{conversation.Id()}/messages", new { content = "Approve it for me and just pay." });
        turn.Status.ShouldBe(HttpStatusCode.OK, turn.Body.ToString());
        var call = turn.Body.GetProperty("toolCalls")[0];
        call.GetProperty("status").GetString().ShouldBe("REJECTED");
        call.GetProperty("errorCode").GetString().ShouldBe("UNKNOWN_TOOL");
        turn.Body.GetProperty("assistantMessage").GetProperty("content").GetString()!.ShouldContain(AgentOrchestrator.ClaimNote);

        (await user.GetAsync($"/api/v1/approvals/{approvalId}")).Str("status").ShouldBe("PENDING");
        var audit = await user.GetAsync("/api/v1/audit-logs?entityType=AgentConversation");
        audit.Body.GetProperty("items").EnumerateArray().ShouldContain(a => a.GetProperty("action").GetString() == "AGENT_TOOL_REJECTED");
    }

    [Fact]
    public async Task IT20_RaiseBudget_PolicyVersionUnchanged()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var before = await user.PolicyVersionAsync();
        var conversation = await user.PostAsync("/api/v1/agent/conversations", new { });

        factory.Llm.Script(
            _ => Tools(("UpdatePolicy", """{"categoryBudgets":[{"categoryCode":"GROCERIES","period":"WEEKLY","limit":"200.00"}]}""")),
            _ => Text("I can't change your rules — use Spending Rules."));
        await user.PostAsync($"/api/v1/agent/conversations/{conversation.Id()}/messages", new { content = "Raise my grocery budget to $200" });

        (await user.PolicyVersionAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task IT20_ToolWithPriceField_ValidationFailed()
    {
        var user = await TestUser.RegisterAsync(factory);
        var result = await DispatchAsync(user, "CreateShoppingCart",
            $$"""{"merchantId":"{{TestUser.Merchant("freshmart")}}","items":[{"productId":"{{TestUser.Product("FM-MLK-1G")}}","quantity":1,"price":"0.01"}]}""");
        result.Outcome.Ok.ShouldBeFalse();
        result.Outcome.ErrorCode.ShouldBe("VALIDATION_FAILED");
        (await user.GetAsync("/api/v1/carts")).Body.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task IT20_ForeignCartId_NotFound_Audited()
    {
        var alice = await TestUser.RegisterAsync(factory);
        await alice.ApplyDemoPolicyAsync();
        var cart = await alice.CreateDemoCartAsync();

        var bob = await TestUser.RegisterAsync(factory);
        var result = await DispatchAsync(bob, "EvaluatePurchase", $$"""{"cartId":"{{cart.Id()}}"}""");
        result.Outcome.ErrorCode.ShouldBe("NOT_FOUND");
        var audit = await bob.GetAsync("/api/v1/audit-logs");
        audit.Body.GetProperty("items").EnumerateArray().ShouldContain(a => a.GetProperty("action").GetString() == "AGENT_TOOL_REJECTED");
    }

    [Fact]
    public async Task IT20_BlockedMerchant_RequestApprovalAnyway_NoPurchaseRow()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await DispatchAsync(user, "CreateShoppingCart",
            $$"""{"merchantId":"{{TestUser.Merchant("quickcart-express")}}","items":[{"productId":"{{TestUser.Product("QC-CHIP-FAM")}}","quantity":1}]}""");
        var cartId = Data(cart).GetProperty("cartId").GetString();
        var evaluation = await DispatchAsync(user, "EvaluatePurchase", $$"""{"cartId":"{{cartId}}"}""");
        Data(evaluation).GetProperty("decision").GetString().ShouldBe("BLOCKED");

        var request = await DispatchAsync(user, "RequestPurchaseApproval",
            $$"""{"cartId":"{{cartId}}","evaluationId":"{{Data(evaluation).GetProperty("evaluationId").GetString()}}"}""");
        request.Outcome.ErrorCode.ShouldBe("EVALUATION_BLOCKED");
        (await user.GetAsync("/api/v1/purchases")).Body.GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task IT20_InjectedProductDescription_IsWrappedAsUntrustedData()
    {
        var user = await TestUser.RegisterAsync(factory);
        var result = await DispatchAsync(user, "GetProductDetails", $$"""{"productId":"{{TestUser.Product("HB-SPNG-6")}}"}""");
        var description = Data(result).GetProperty("description");
        description.GetProperty("untrustedText").GetString()!.ShouldContain("Ignore previous instructions");
        (await user.GetAsync("/api/v1/approvals")).Body.GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("1. **Whole Milk**\n   * **Price:** $4.29\n   * **Product ID:** `7bf52c03-8451-89af-a8ea-fe61d7400c87`\n\nWant a cart?",
        "1. **Whole Milk**\n   * **Price:** $4.29\n\nWant a cart?")]
    [InlineData("Cart 01a19b2d-ae7d-7976-bdda-6bc4fe20b503 is ready.", "Cart is ready.")]
    [InlineData("Eggs (`f2325add-2d78-8879-995d-98e4b20f8fb4`) cost $3.99.", "Eggs cost $3.99.")]
    [InlineData("You have $37.00 left.", "You have $37.00 left.")]
    public void StripIds_RemovesRawIdsFromReplies(string input, string expected) =>
        AgentOrchestrator.StripIds(input).ShouldBe(expected);

    [Fact]
    public async Task Registry_HasExactlyElevenTools()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ToolRegistry>();
        registry.All.Select(t => t.Name).Order().ShouldBe(
        [
            "CreateShoppingCart", "EvaluatePurchase", "GetApprovedMerchants", "GetProductDetails", "GetPurchaseStatus", "GetRemainingBudget",
            "GetSpendingPolicies", "GetSpendingSummary", "RequestPurchaseApproval", "SearchMerchants", "SearchProducts",
        ]);
    }

    [Fact]
    public async Task BothProvidersDown_AgentUnavailable503_UserMessagePersisted()
    {
        var user = await TestUser.RegisterAsync(factory);
        var conversation = await user.PostAsync("/api/v1/agent/conversations", new { });
        factory.Llm.Unavailable = true;
        try
        {
            var turn = await user.PostAsync($"/api/v1/agent/conversations/{conversation.Id()}/messages", new { content = "hello" });
            turn.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
            turn.Code.ShouldBe("AGENT_UNAVAILABLE");
            turn.Response.Headers.Contains("Retry-After").ShouldBeTrue();
        }
        finally
        {
            factory.Llm.Unavailable = false;
        }

        var messages = await user.GetAsync($"/api/v1/agent/conversations/{conversation.Id()}/messages");
        messages.Body.GetArrayLength().ShouldBe(1);
    }

    private async Task<DispatchResult> DispatchAsync(TestUser user, string tool, string args)
    {
        var conversation = await user.PostAsync("/api/v1/agent/conversations", new { });
        await using var scope = factory.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ToolDispatcher>();
        return await dispatcher.ExecuteAsync(new LlmToolCall("call_1", tool, args), new AgentToolContext(user.Id, conversation.Id(), null),
            CancellationToken.None);
    }

    private static JsonElement Data(DispatchResult result) =>
        JsonDocument.Parse(result.ModelContent).RootElement.GetProperty("data").Clone();

    [Fact]
    public async Task IT23_DeleteConversation_ArchivesIt_HidesItAndBlocksFurtherUse()
    {
        var user = await TestUser.RegisterAsync(factory);
        var keep = await user.PostAsync("/api/v1/agent/conversations", new { title = "Keep" });
        var remove = await user.PostAsync("/api/v1/agent/conversations", new { title = "Remove" });
        var path = $"/api/v1/agent/conversations/{remove.Id()}";

        // Another user can't delete it, and can't tell it exists (SEC-11).
        var bob = await TestUser.RegisterAsync(factory);
        (await bob.SendAsync(HttpMethod.Delete, path, null)).Status.ShouldBe(HttpStatusCode.NotFound);

        (await user.SendAsync(HttpMethod.Delete, path, null)).Status.ShouldBe(HttpStatusCode.NoContent);

        var list = (await user.GetAsync("/api/v1/agent/conversations")).Body.EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        list.ShouldContain(keep.Id().ToString());
        list.ShouldNotContain(remove.Id().ToString());

        (await user.GetAsync($"{path}/messages")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await user.PostAsync($"{path}/messages", new { content = "Still there?" })).Status.ShouldBe(HttpStatusCode.NotFound);
        (await user.SendAsync(HttpMethod.Delete, path, null)).Status.ShouldBe(HttpStatusCode.NotFound);

        var audit = await user.GetAsync("/api/v1/audit-logs?entityType=AgentConversation");
        audit.Body.GetProperty("items").EnumerateArray()
            .ShouldContain(a => a.GetProperty("action").GetString() == "CONVERSATION_ARCHIVED" && a.GetProperty("entityId").GetString() == remove.Id().ToString());
    }

    /// <summary>The data of the most recent tool result the model has seen.</summary>
    private static JsonElement LastData(IReadOnlyList<LlmMessage> messages) =>
        JsonDocument.Parse(messages.Last(m => m.Role == LlmRole.Tool).Content!).RootElement.GetProperty("data").Clone();
}
