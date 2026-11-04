using System.Net;
using Guardrail.Integration.Tests.Infrastructure;

namespace Guardrail.Integration.Tests;

[Collection(ApiCollection.Name)]
public class InsightsTests(GuardrailFactory factory)
{
    [Fact]
    public async Task IT26_Datasets_AreUserScoped_IncludeBlockedAttempts_AndCarryNoPayerData()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.ApplyDemoPolicyAsync();
        await user.ApprovedDemoPurchaseAsync(); // FreshMart, $43.00, REQUIRES_APPROVAL → approved

        // A blocked attempt at a NEW merchant (new merchants BLOCK) never becomes a purchase but must show in evaluations.
        var blockedCart = await user.CreateCartAsync("quickcart-express", ("QC-CHIP-FAM", 2));
        (await user.EvaluateAsync(blockedCart.Id())).Str("decision").ShouldBe("BLOCKED");

        var result = await user.GetAsync("/api/v1/insights/datasets");
        result.Status.ShouldBe(HttpStatusCode.OK, result.Body.ToString());
        var body = result.Body;
        body.GetProperty("currency").GetString().ShouldBe("USD");

        var purchase = body.GetProperty("purchases").EnumerateArray().ShouldHaveSingleItem();
        purchase.GetProperty("merchant").GetString().ShouldBe("FreshMart");
        purchase.GetProperty("merchantStatus").GetString().ShouldBe("APPROVED");
        purchase.GetProperty("amount").GetDecimal().ShouldBe(43.00m);
        purchase.GetProperty("decision").GetString().ShouldBe("REQUIRES_APPROVAL");
        purchase.GetProperty("requestedBy").GetString().ShouldBe("USER");
        purchase.GetProperty("approvalMinutes").GetInt32().ShouldBeGreaterThanOrEqualTo(0);

        body.GetProperty("lineItems").GetArrayLength().ShouldBe(TestUser.DemoSkus.Length);
        body.GetProperty("ledger").EnumerateArray().ShouldContain(e => e.GetProperty("transition").GetString() == "RESERVE" || e.GetProperty("transition").GetString() == "CONFIRM_RESERVATION");

        var evaluations = body.GetProperty("evaluations").EnumerateArray().ToList();
        var blocked = evaluations.Where(e => e.GetProperty("decision").GetString() == "BLOCKED").ShouldHaveSingleItem();
        blocked.GetProperty("merchant").GetString().ShouldBe("QuickCart Express");
        blocked.GetProperty("primaryReasonCode").GetString().ShouldBe("MERCHANT_NOT_APPROVED");
        blocked.GetProperty("amount").GetDecimal().ShouldBe(8.98m);
        blocked.GetProperty("categoryCodes").EnumerateArray().Select(c => c.GetString()).ShouldBe(["GROCERIES"]);

        // Groceries has a weekly budget; the monthly summary still tracks its spend (no limit).
        var groceries = body.GetProperty("budgets").EnumerateArray()
            .Where(b => b.GetProperty("scope").GetString() == "CATEGORY" && b.GetProperty("categoryCode").GetString() == "GROCERIES")
            .ToDictionary(b => b.GetProperty("period").GetString()!);
        groceries["WEEKLY"].GetProperty("limit").GetDecimal().ShouldBe(80.00m);
        groceries["WEEKLY"].GetProperty("spent").GetDecimal().ShouldBe(0m); // approved, not yet captured → pending
        groceries["WEEKLY"].GetProperty("pending").GetDecimal().ShouldBe(43.00m);

        body.GetProperty("merchants").EnumerateArray().ShouldContain(m => m.GetProperty("merchant").GetString() == "FreshMart" && m.GetProperty("status").GetString() == "APPROVED");

        // These rows also reach the LLM: no email, display name or PayPal identifiers.
        var raw = body.GetRawText();
        raw.ShouldNotContain(user.Email, Case.Insensitive);
        raw.ShouldNotContain("paypal", Case.Insensitive);

        // Another user sees none of it.
        var bob = await TestUser.RegisterAsync(factory);
        var bobData = (await bob.GetAsync("/api/v1/insights/datasets")).Body;
        bobData.GetProperty("purchases").GetArrayLength().ShouldBe(0);
        bobData.GetProperty("evaluations").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task IT27_Datasets_RejectInvalidRange()
    {
        var user = await TestUser.RegisterAsync(factory);
        var result = await user.GetAsync("/api/v1/insights/datasets?from=2026-12-01T00:00:00Z&to=2026-11-01T00:00:00Z");
        result.Status.ShouldBe(HttpStatusCode.BadRequest);
        result.Code.ShouldBe("VALIDATION_FAILED");
    }

    [Fact]
    public async Task IT28_Reports_SaveLoadAndIsolation()
    {
        var user = await TestUser.RegisterAsync(factory);
        const string path = "/api/v1/insights/reports/default";

        (await user.GetAsync(path)).Status.ShouldBe(HttpStatusCode.NotFound);

        var saved = await user.SendAsync(HttpMethod.Put, path, new { state = new { pages = new[] { new { id = "p1", title = "Where my money goes" } } }, version = "3.1.0" });
        saved.Status.ShouldBe(HttpStatusCode.OK, saved.Body.ToString());

        var loaded = await user.GetAsync(path);
        loaded.Status.ShouldBe(HttpStatusCode.OK);
        loaded.Str("version").ShouldBe("3.1.0");
        loaded.Body.GetProperty("state").GetProperty("pages")[0].GetProperty("title").GetString().ShouldBe("Where my money goes");

        // Overwrite keeps one row per name.
        (await user.SendAsync(HttpMethod.Put, path, new { state = new { pages = Array.Empty<object>() }, version = "3.1.1" })).Status.ShouldBe(HttpStatusCode.OK);
        (await user.GetAsync(path)).Str("version").ShouldBe("3.1.1");

        // Validation: state must be an object; names are slugs.
        (await user.SendAsync(HttpMethod.Put, path, new { state = "nope" })).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await user.SendAsync(HttpMethod.Put, "/api/v1/insights/reports/Bad%20Name", new { state = new { } })).Status.ShouldBe(HttpStatusCode.BadRequest);

        // Another user's report is invisible.
        var bob = await TestUser.RegisterAsync(factory);
        (await bob.GetAsync(path)).Status.ShouldBe(HttpStatusCode.NotFound);
    }
}
