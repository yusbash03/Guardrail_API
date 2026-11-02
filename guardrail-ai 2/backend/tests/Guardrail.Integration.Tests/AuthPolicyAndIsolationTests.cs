using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Guardrail.Integration.Tests.Infrastructure;

namespace Guardrail.Integration.Tests;

[Collection(ApiCollection.Name)]
public class AuthPolicyAndIsolationTests(GuardrailFactory factory)
{
    [Fact]
    public async Task IT01_RegisterLoginRefreshRotation_ReuseRevokesFamily()
    {
        var user = await TestUser.RegisterAsync(factory);
        var http = factory.CreateClient(new() { HandleCookies = false });

        var login = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = user.Password });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie1 = RefreshCookie(login);

        // Missing client header → rejected (SEC-04).
        var noHeader = await Refresh(http, cookie1, withHeader: false);
        noHeader.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var rotated = await Refresh(http, cookie1);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie2 = RefreshCookie(rotated);
        cookie2.ShouldNotBe(cookie1);

        // Reusing the rotated token revokes the whole family…
        (await Refresh(http, cookie1)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // …so the newest token is dead too.
        (await Refresh(http, cookie2)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WrongPassword_GenericError_AndLockoutAfterFive()
    {
        var user = await TestUser.RegisterAsync(factory);
        var http = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var bad = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = "wrong password!!" });
            bad.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var locked = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = user.Password });
        locked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await locked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().ShouldBe("Invalid email or password.");
    }

    [Fact]
    public async Task Register_WeakPassword_ValidationFailed()
    {
        var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/v1/auth/register",
            new { email = $"{Guid.NewGuid():N}@x.com", password = "short", displayName = "A", timeZone = "UTC" });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("VALIDATION_FAILED");
    }

    [Fact]
    public async Task NewUser_HasDefaultPolicyV1()
    {
        var user = await TestUser.RegisterAsync(factory);
        var policy = await user.GetAsync("/api/v1/policies");
        policy.Body.GetProperty("versionNumber").GetInt32().ShouldBe(1);
        policy.Body.GetProperty("approvalThreshold").GetProperty("amount").GetString().ShouldBe("0.00");
        policy.Body.GetProperty("merchantRules").GetProperty("newMerchantAction").GetString().ShouldBe("BLOCK");
        policy.Body.GetProperty("unbudgetedCategoryAction").GetString().ShouldBe("REQUIRE_APPROVAL");
        policy.Body.GetProperty("productRestrictions")[0].GetProperty("match").GetProperty("value").GetString().ShouldBe("ALCOHOL");
    }

    [Fact]
    public async Task IT02_PolicyPut_CreatesVersionAndAudit_StaleVersionConflicts()
    {
        var user = await TestUser.RegisterAsync(factory);
        var updated = await user.ApplyDemoPolicyAsync();
        updated.Body.GetProperty("versionNumber").GetInt32().ShouldBe(2);

        var versions = await user.GetAsync("/api/v1/policies/versions");
        versions.Body.GetProperty("totalCount").GetInt32().ShouldBe(2);

        var audit = await user.GetAsync("/api/v1/audit-logs?entityType=PolicyVersion");
        var row = audit.Body.GetProperty("items").EnumerateArray().First(a => a.GetProperty("action").GetString() == "POLICY_UPDATED");
        row.GetProperty("beforeHash").GetString()!.Length.ShouldBe(64);
        row.GetProperty("afterHash").GetString().ShouldNotBe(row.GetProperty("beforeHash").GetString());

        var stale = await user.PutAsync("/api/v1/policies", new { expectedVersionNumber = 1, policy = new { merchantRules = new { approvedMerchantIds = Array.Empty<Guid>(), blockedMerchantIds = Array.Empty<Guid>(), newMerchantAction = "BLOCK" }, unbudgetedCategoryAction = "ALLOW" } });
        stale.Status.ShouldBe(HttpStatusCode.Conflict);
        stale.Code.ShouldBe("POLICY_VERSION_CONFLICT");
    }

    [Fact]
    public async Task PolicyPut_MerchantBothApprovedAndBlocked_Conflict()
    {
        var user = await TestUser.RegisterAsync(factory);
        var freshmart = TestUser.Merchant("freshmart");
        var result = await user.PutAsync("/api/v1/policies", new
        {
            expectedVersionNumber = 1,
            policy = new
            {
                merchantRules = new { approvedMerchantIds = new[] { freshmart }, blockedMerchantIds = new[] { freshmart }, newMerchantAction = "BLOCK" },
                unbudgetedCategoryAction = "ALLOW",
            },
        });
        result.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        result.Code.ShouldBe("POLICY_MERCHANT_CONFLICT");
    }

    [Fact]
    public async Task Merchants_ApproveAndBlock_CreateVersions()
    {
        var user = await TestUser.RegisterAsync(factory);
        var quickcart = TestUser.Merchant("quickcart-express");
        var approved = await user.PostAsync($"/api/v1/merchants/{quickcart}/approve", new { expectedVersionNumber = 1 });
        approved.Status.ShouldBe(HttpStatusCode.OK, approved.Body.ToString());
        approved.Body.GetProperty("merchant").GetProperty("status").GetString().ShouldBe("APPROVED");
        approved.Body.GetProperty("policyVersionNumber").GetInt32().ShouldBe(2);

        var blocked = await user.PostAsync($"/api/v1/merchants/{quickcart}/block", new { expectedVersionNumber = 2 });
        blocked.Body.GetProperty("merchant").GetProperty("status").GetString().ShouldBe("BLOCKED");

        var list = await user.GetAsync("/api/v1/merchants?status=BLOCKED");
        list.Body.EnumerateArray().Select(m => m.GetProperty("slug").GetString()).ShouldBe(["quickcart-express"]);
    }

    [Fact]
    public async Task IT15_CrossUserAccess_Returns404Everywhere()
    {
        var alice = await TestUser.RegisterAsync(factory);
        await alice.ApplyDemoPolicyAsync();
        var cart = await alice.CreateDemoCartAsync();
        var evaluation = await alice.EvaluateAsync(cart.Id());
        var purchase = await alice.CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));
        var approvalId = purchase.Body.GetProperty("approval").GetProperty("id").GetString();
        var conversation = await alice.PostAsync("/api/v1/agent/conversations", new { title = "mine" });

        var bob = await TestUser.RegisterAsync(factory);
        (await bob.GetAsync($"/api/v1/carts/{cart.Id()}")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.PostAsync($"/api/v1/carts/{cart.Id()}/evaluate")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/v1/evaluations/{evaluation.Id("evaluationId")}")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/v1/purchases/{purchase.Id()}")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.PostAsync($"/api/v1/purchases/{purchase.Id()}/cancel")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.CheckoutAsync(purchase.Id())).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/v1/approvals/{approvalId}")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.ApproveAsync(Guid.Parse(approvalId!), purchase.Str("cartSnapshotHash"))).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.PostAsync($"/api/v1/approvals/{approvalId}/reject", new { })).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/v1/agent/conversations/{conversation.Id()}/messages")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.PostAsync($"/api/v1/agent/conversations/{conversation.Id()}/messages", new { content = "hi" })).Status.ShouldBe(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/v1/ledger/entries?purchaseId={purchase.Id()}")).Body.GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Unauthenticated_Gets401ProblemDetails()
    {
        var http = factory.CreateClient();
        var response = await http.GetAsync("/api/v1/policies");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().ShouldBe("UNAUTHENTICATED");
        response.Headers.Contains("X-Correlation-Id").ShouldBeTrue();
    }

    [Fact]
    public async Task IT22_InternalJobs_RequireToken()
    {
        var http = factory.CreateClient();
        (await http.PostAsync("/api/v1/internal/jobs/run", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/v1/internal/jobs/run");
        wrong.Headers.Add("X-Job-Token", "nope");
        (await http.SendAsync(wrong)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var right = new HttpRequestMessage(HttpMethod.Post, "/api/v1/internal/jobs/run");
        right.Headers.Add("X-Job-Token", GuardrailFactory.JobToken);
        (await http.SendAsync(right)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OpenApiDocument_IsServed()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text[..Math.Min(text.Length, 500)]);
        text.ShouldContain("/api/v1/purchases/{id}/capture");
        text.ShouldNotContain("/api/v1/internal/jobs/run");
    }

    [Fact]
    public async Task Health_IsAnonymous()
    {
        var response = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/health");
        response.GetProperty("db").GetString().ShouldBe("Healthy");
    }

    [Fact]
    public async Task Catalogue_SearchFindsDemoItems()
    {
        var user = await TestUser.RegisterAsync(factory);
        var result = await user.GetAsync("/api/v1/products?query=greek%20yogurt");
        var names = result.Body.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        names.ShouldContain("Greek Yogurt");
        (await user.GetAsync("/api/v1/categories")).Body.GetArrayLength().ShouldBe(6);
    }

    private static string RefreshCookie(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("gr_rt=", StringComparison.Ordinal));
        header.ShouldContain("httponly", Case.Insensitive);
        header.ShouldContain("samesite=strict", Case.Insensitive);
        header.ShouldContain("path=/api/v1/auth", Case.Insensitive);
        return header.Split(';')[0];
    }

    private static Task<HttpResponseMessage> Refresh(HttpClient http, string cookie, bool withHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", cookie);
        if (withHeader)
        {
            request.Headers.Add("X-Guardrail-Client", "web");
        }

        return http.SendAsync(request);
    }
}
