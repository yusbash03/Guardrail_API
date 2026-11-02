using System.Net;
using Guardrail.Application.Jobs;
using Guardrail.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Guardrail.Integration.Tests;

[Collection(ApiCollection.Name)]
public class PurchaseAndApprovalTests(GuardrailFactory factory)
{
    [Fact]
    public async Task Evaluate_DemoCart_MatchesCanonicalDecision()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateDemoCartAsync();
        cart.Str("total").ShouldBe("43.00");

        var evaluation = await user.EvaluateAsync(cart.Id());
        evaluation.Str("decision").ShouldBe("REQUIRES_APPROVAL");
        evaluation.Str("reasonCode").ShouldBe("APPROVAL_THRESHOLD_EXCEEDED");
        evaluation.Str("purchaseAmount").ShouldBe("43.00");
        evaluation.Str("remainingBudget").ShouldBe("37.00");
        evaluation.Str("cartSnapshotHash").ShouldBe(cart.Str("snapshotHash"));
        evaluation.Body.GetProperty("isAllowed").GetBoolean().ShouldBeFalse();

        // I7: persisted and retrievable.
        var again = await user.GetAsync($"/api/v1/evaluations/{evaluation.Id("evaluationId")}");
        again.Str("remainingBudget").ShouldBe("37.00");
    }

    [Fact]
    public async Task IT03_IT04_PurchaseThenApprove_LedgerProposeThenConfirm()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateDemoCartAsync();
        var evaluation = await user.EvaluateAsync(cart.Id());

        var purchase = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));
        purchase.Status.ShouldBe(HttpStatusCode.Created, purchase.Body.ToString());
        purchase.Str("status").ShouldBe("AWAITING_APPROVAL");
        (await user.LedgerAsync(purchase.Id())).Select(e => e!["transition"]!.GetValue<string>()).ShouldBe(["PROPOSE"]);

        // Cart is locked (BR-30).
        var locked = await user.PostAsync($"/api/v1/carts/{cart.Id()}/items", new { productId = TestUser.Product("FM-CHED-8OZ"), quantity = 1 });
        locked.Code.ShouldBe("CART_LOCKED");

        var approvalId = Guid.Parse(purchase.Body.GetProperty("approval").GetProperty("id").GetString()!);
        var approved = await user.ApproveAsync(approvalId, purchase.Str("cartSnapshotHash"));
        approved.Status.ShouldBe(HttpStatusCode.OK, approved.Body.ToString());
        approved.Body.GetProperty("purchase").GetProperty("status").GetString().ShouldBe("READY_FOR_CHECKOUT");
        approved.Body.GetProperty("approval").GetProperty("status").GetString().ShouldBe("APPROVED");

        var ledger = await user.LedgerAsync(purchase.Id());
        ledger.Select(e => e!["transition"]!.GetValue<string>()).Order().ShouldBe(["CONFIRM_RESERVATION", "PROPOSE"]);

        var summary = await user.GetAsync("/api/v1/ledger/summary?period=WEEKLY");
        var groceries = summary.Body.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("categoryCode").GetString() == "GROCERIES");
        groceries.GetProperty("pending").GetString().ShouldBe("43.00");
        groceries.GetProperty("remaining").GetString().ShouldBe("37.00");

        // Approve twice → 409.
        (await user.ApproveAsync(approvalId, purchase.Str("cartSnapshotHash"))).Code.ShouldBe("APPROVAL_NOT_PENDING");
    }

    [Fact]
    public async Task IT05_ApproveGuards_Hash_Password_Lockout_Header()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateDemoCartAsync();
        var evaluation = await user.EvaluateAsync(cart.Id());
        var purchase = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));
        var approvalId = Guid.Parse(purchase.Body.GetProperty("approval").GetProperty("id").GetString()!);
        var hash = purchase.Str("cartSnapshotHash");

        var wrongHash = await user.ApproveAsync(approvalId, new string('a', 64));
        wrongHash.Status.ShouldBe(HttpStatusCode.Conflict);
        wrongHash.Code.ShouldBe("APPROVAL_SNAPSHOT_MISMATCH");

        var noHeader = await user.PostAsync($"/api/v1/approvals/{approvalId}/approve", new { cartSnapshotHash = hash, password = user.Password });
        noHeader.Status.ShouldBe(HttpStatusCode.Forbidden);

        var missingPassword = await user.PostAsync($"/api/v1/approvals/{approvalId}/approve", new { cartSnapshotHash = hash },
            headers: new Dictionary<string, string> { ["X-Guardrail-Client"] = "web" });
        missingPassword.Code.ShouldBe("VALIDATION_FAILED");

        for (var i = 0; i < 5; i++)
        {
            var wrong = await user.ApproveAsync(approvalId, hash, "not my password");
            wrong.Status.ShouldBe(HttpStatusCode.Unauthorized);
            wrong.Code.ShouldBe("REAUTHENTICATION_FAILED");
        }

        var sixth = await user.ApproveAsync(approvalId, hash);
        sixth.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        sixth.Code.ShouldBe("APPROVAL_STEP_UP_LOCKED");
        sixth.Response.Headers.Contains("Retry-After").ShouldBeTrue();

        // State unchanged and failures audited.
        (await user.GetAsync($"/api/v1/approvals/{approvalId}")).Str("status").ShouldBe("PENDING");
        var audit = await user.GetAsync("/api/v1/audit-logs?pageSize=100");
        audit.Body.GetProperty("items").EnumerateArray().Count(a => a.GetProperty("action").GetString() == "APPROVAL_STEP_UP_FAILED").ShouldBe(5);
    }

    [Fact]
    public async Task IT05b_ApproveAfterExpiry_Gone_AndReservationReleased()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateDemoCartAsync();
        var evaluation = await user.EvaluateAsync(cart.Id());
        var purchase = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));
        var approvalId = Guid.Parse(purchase.Body.GetProperty("approval").GetProperty("id").GetString()!);

        factory.Clock.Advance(TimeSpan.FromMinutes(31));
        await user.LoginAgainAsync();
        var late = await user.ApproveAsync(approvalId, purchase.Str("cartSnapshotHash"));
        late.Status.ShouldBe(HttpStatusCode.Gone);
        late.Code.ShouldBe("APPROVAL_EXPIRED");

        (await user.GetAsync($"/api/v1/purchases/{purchase.Id()}")).Str("status").ShouldBe("EXPIRED");
        (await user.LedgerAsync(purchase.Id())).Select(e => e!["transition"]!.GetValue<string>()).Order().ShouldBe(["PROPOSE", "RELEASE"]);
        var reopened = await user.GetAsync($"/api/v1/carts/{cart.Id()}");
        reopened.Str("status").ShouldBe("OPEN");
        reopened.Body.GetProperty("version").GetInt32().ShouldBeGreaterThan(evaluation.Body.GetProperty("cartVersion").GetInt32());
    }

    [Fact]
    public async Task Sweeper_ExpiresPendingApprovals()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateDemoCartAsync();
        var evaluation = await user.EvaluateAsync(cart.Id());
        var purchase = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));

        factory.Clock.Advance(TimeSpan.FromMinutes(31));
        await user.LoginAgainAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<JobRunner>().ExpireApprovalsAsync(CancellationToken.None)).ShouldBeGreaterThan(0);
        }

        (await user.GetAsync($"/api/v1/purchases/{purchase.Id()}")).Str("status").ShouldBe("EXPIRED");
        (await user.LedgerAsync(purchase.Id())).Count.ShouldBe(2);
    }

    [Fact]
    public async Task IT06_PolicyChangeWhilePending_InvalidatesApprovalAndReleases()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateDemoCartAsync();
        var evaluation = await user.EvaluateAsync(cart.Id());
        var purchase = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));
        var approvalId = Guid.Parse(purchase.Body.GetProperty("approval").GetProperty("id").GetString()!);

        await user.ApplyDemoPolicyAsync(groceriesWeekly: 90m);

        var approval = await user.GetAsync($"/api/v1/approvals/{approvalId}");
        approval.Str("status").ShouldBe("INVALIDATED");
        approval.Str("invalidatedReason").ShouldBe("POLICY_VERSION_CHANGED");
        (await user.GetAsync($"/api/v1/purchases/{purchase.Id()}")).Str("status").ShouldBe("CANCELLED");
        (await user.LedgerAsync(purchase.Id())).Select(e => e!["transition"]!.GetValue<string>()).ShouldContain("RELEASE");

        var approve = await user.ApproveAsync(approvalId, purchase.Str("cartSnapshotHash"));
        approve.Code.ShouldBe("APPROVAL_NOT_PENDING");
    }

    [Fact]
    public async Task Reject_ReleasesAndUnlocksCart()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateDemoCartAsync();
        var evaluation = await user.EvaluateAsync(cart.Id());
        var purchase = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));
        var approvalId = purchase.Body.GetProperty("approval").GetProperty("id").GetString();

        var rejected = await user.PostAsync($"/api/v1/approvals/{approvalId}/reject", new { reason = "Too expensive" });
        rejected.Status.ShouldBe(HttpStatusCode.OK, rejected.Body.ToString());
        rejected.Body.GetProperty("purchase").GetProperty("status").GetString().ShouldBe("REJECTED");
        (await user.GetAsync($"/api/v1/carts/{cart.Id()}")).Str("status").ShouldBe("OPEN");

        var summary = await user.GetAsync("/api/v1/ledger/summary?period=WEEKLY");
        summary.Body.GetProperty("categories")[0].GetProperty("pending").GetString().ShouldBe("0.00");
    }

    [Fact]
    public async Task IT14_ParallelPurchases_CannotExceedBudget()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync(threshold: 100m);
        // Two ~$45 carts (3 × 9.98 + 2 × 7.49 = 44.92) against an $80 weekly budget.
        var a = await user.CreateCartAsync("freshmart", ("FM-CHK-2LB", 3), ("FM-RICE-5LB", 2));
        var b = await user.CreateCartAsync("freshmart", ("FM-CHK-2LB", 3), ("FM-RICE-5LB", 2));
        var ea = await user.EvaluateAsync(a.Id());
        var eb = await user.EvaluateAsync(b.Id());
        ea.Str("decision").ShouldBe("ALLOWED");
        eb.Str("decision").ShouldBe("ALLOWED");

        var results = await Task.WhenAll(
            user.CreatePurchaseAsync(a.Id(), ea.Id("evaluationId")),
            user.CreatePurchaseAsync(b.Id(), eb.Id("evaluationId")));

        results.Count(r => r.Status == HttpStatusCode.Created).ShouldBe(1);
        var loser = results.Single(r => r.Status != HttpStatusCode.Created);
        loser.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, loser.Body.ToString());
        loser.Code.ShouldBe("EVALUATION_BLOCKED");
        loser.Body.GetProperty("data").GetProperty("reasons")[0].GetProperty("code").GetString().ShouldBe("CATEGORY_BUDGET_EXCEEDED");
    }

    [Fact]
    public async Task IT16_BlockedMerchantCart_EvaluatesBlocked_PurchaseRefused()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateCartAsync("quickcart-express", ("QC-CHIP-FAM", 1), ("QC-SPWT-12", 1), ("QC-TRAIL-16", 1));
        cart.Str("total").ShouldBe("18.00");
        var evaluation = await user.EvaluateAsync(cart.Id());
        evaluation.Str("decision").ShouldBe("BLOCKED");
        evaluation.Str("reasonCode").ShouldBe("MERCHANT_NOT_APPROVED");
        evaluation.Str("remainingBudget").ShouldBe("62.00");

        var purchase = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));
        purchase.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        purchase.Code.ShouldBe("EVALUATION_BLOCKED");
        (await user.GetAsync("/api/v1/purchases")).Body.GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Purchase_StaleEvaluation_AfterCartChange_Conflicts()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateCartAsync("freshmart", ("FM-MLK-1G", 1));
        var evaluation = await user.EvaluateAsync(cart.Id());
        await user.PostAsync($"/api/v1/carts/{cart.Id()}/items", new { productId = TestUser.Product("FM-EGG-12"), quantity = 1 });

        var purchase = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));
        purchase.Status.ShouldBe(HttpStatusCode.Conflict);
        purchase.Code.ShouldBe("EVALUATION_STALE");
    }

    [Fact]
    public async Task Cart_IfMatchMismatch_PreconditionFailed()
    {
        var user = await TestUser.RegisterAsync(factory);
        var cart = await user.CreateCartAsync("freshmart", ("FM-MLK-1G", 1));
        var result = await user.SendAsync(HttpMethod.Post, $"/api/v1/carts/{cart.Id()}/items",
            new { productId = TestUser.Product("FM-EGG-12"), quantity = 1 }, new Dictionary<string, string> { ["If-Match"] = "\"999\"" });
        result.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        result.Code.ShouldBe("CART_VERSION_MISMATCH");
    }

    [Fact]
    public async Task Cart_MixedMerchant_Rejected()
    {
        var user = await TestUser.RegisterAsync(factory);
        var cart = await user.CreateCartAsync("freshmart", ("FM-MLK-1G", 1));
        var mixed = await user.PostAsync($"/api/v1/carts/{cart.Id()}/items", new { productId = TestUser.Product("GG-MLK-1G"), quantity = 1 });
        mixed.Code.ShouldBe("MIXED_MERCHANT_CART");
    }

    [Fact]
    public async Task IT18_IdempotencyKey_ReplaysAndRejectsDifferentBody()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateDemoCartAsync();
        var evaluation = await user.EvaluateAsync(cart.Id());
        var key = Guid.NewGuid().ToString();

        var first = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"), key);
        var replay = await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"), key);
        replay.Status.ShouldBe(first.Status);
        replay.Id().ShouldBe(first.Id());
        replay.Response.Headers.GetValues("Idempotent-Replayed").Single().ShouldBe("true");

        var different = await user.CreatePurchaseAsync(cart.Id(), Guid.NewGuid(), key);
        different.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        different.Code.ShouldBe("IDEMPOTENCY_KEY_REUSED");

        var missing = await user.PostAsync("/api/v1/purchases", new { cartId = cart.Id(), evaluationId = evaluation.Id("evaluationId") });
        missing.Code.ShouldBe("VALIDATION_FAILED");
    }

    [Fact]
    public async Task Dashboard_ReflectsPendingApproval()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        var cart = await user.CreateDemoCartAsync();
        var evaluation = await user.EvaluateAsync(cart.Id());
        await user.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));

        var overview = await user.GetAsync("/api/v1/dashboard/overview");
        overview.Status.ShouldBe(HttpStatusCode.OK, overview.Body.ToString());
        overview.Body.GetProperty("pendingApprovals").GetInt32().ShouldBe(1);
        overview.Body.GetProperty("categories")[0].GetProperty("remaining").GetString().ShouldBe("37.00");
        overview.Body.GetProperty("insights").GetArrayLength().ShouldBeGreaterThan(0);
    }
}
