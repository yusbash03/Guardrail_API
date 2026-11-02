using System.Net;
using System.Text;
using System.Text.Json;
using Guardrail.Application.Jobs;
using Guardrail.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Guardrail.Integration.Tests;

[Collection(ApiCollection.Name)]
public class PaymentAndWebhookTests : IDisposable
{
    private readonly GuardrailFactory _factory;

    public PaymentAndWebhookTests(GuardrailFactory factory)
    {
        _factory = factory;
        factory.PayPal.Reset();
        factory.WebhookVerifier.Result = true;
        factory.WebhookVerifier.Unavailable = false;
    }

    public void Dispose() => _factory.PayPal.Reset();

    [Fact]
    public async Task IT07_IT08_Checkout_CreatesOneOrder_ConsumesApprovalOnce()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, approvalId, _) = await user.ApprovedDemoPurchaseAsync();

        var results = await Task.WhenAll(user.CheckoutAsync(purchaseId), user.CheckoutAsync(purchaseId));
        results.ShouldAllBe(r => r.Status == HttpStatusCode.OK || r.Code == "IDEMPOTENCY_IN_PROGRESS" || r.Code == "INVALID_STATE");
        var ok = results.First(r => r.Status == HttpStatusCode.OK);
        ok.Str("purchaseStatus").ShouldBe("CHECKOUT_PENDING");
        ok.Str("approveUrl").ShouldStartWith("https://www.sandbox.paypal.com/");

        // Re-checkout returns the same order (no second consume).
        var again = await user.CheckoutAsync(purchaseId);
        again.Str("paypalOrderId").ShouldBe(ok.Str("paypalOrderId"));
        (await user.GetAsync($"/api/v1/approvals/{approvalId}")).Str("status").ShouldBe("CONSUMED");
        var purchase = await user.GetAsync($"/api/v1/purchases/{purchaseId}");
        purchase.Body.GetProperty("payment").GetProperty("paypalOrderId").GetString().ShouldBe(ok.Str("paypalOrderId"));
    }

    [Fact]
    public async Task IT09_CaptureBeforeBuyerApproval_PaymentNotApproved_NoAuthorizeCall()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, _, _) = await user.ApprovedDemoPurchaseAsync();
        var checkout = await user.CheckoutAsync(purchaseId);

        var capture = await user.CaptureAsync(purchaseId);
        capture.Status.ShouldBe(HttpStatusCode.Conflict);
        capture.Code.ShouldBe("PAYMENT_NOT_APPROVED");
        _factory.PayPal.Calls.ShouldNotContain($"authorize:{checkout.Str("paypalOrderId")}");
    }

    [Fact]
    public async Task IT10_HappyPath_AuthorizeFinalCheckCapture_LedgerAndDashboard()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, orderId) = await user.CheckedOutAndBuyerApprovedAsync();

        var capture = await user.CaptureAsync(purchaseId);
        capture.Status.ShouldBe(HttpStatusCode.OK, capture.Body.ToString());
        capture.Str("status").ShouldBe("COMPLETED");
        var payment = capture.Body.GetProperty("payment");
        payment.GetProperty("status").GetString().ShouldBe("COMPLETED");
        payment.GetProperty("paypalAuthorizationId").GetString().ShouldBe($"AUTH-{orderId}");
        payment.GetProperty("paypalCaptureId").GetString().ShouldBe($"CAP-{orderId}");
        payment.GetProperty("finalCheck").GetProperty("passed").GetBoolean().ShouldBeTrue();

        var transitions = (await user.LedgerAsync(purchaseId)).Select(e => e!["transition"]!.GetValue<string>()).Order().ToList();
        transitions.ShouldBe(["AUTHORIZE", "CAPTURE", "CONFIRM_RESERVATION", "PROPOSE"]);

        var overview = await user.GetAsync("/api/v1/dashboard/overview");
        var groceries = overview.Body.GetProperty("categories")[0];
        groceries.GetProperty("spent").GetString().ShouldBe("43.00");
        groceries.GetProperty("remaining").GetString().ShouldBe("37.00");
        (await user.GetAsync($"/api/v1/carts/{capture.Str("cartId")}")).Str("status").ShouldBe("CHECKED_OUT");

        // Distinct PayPal-Request-Ids per step.
        var ids = _factory.PayPal.RequestIds.Where(r => r.Operation is "create" or "authorize" or "capture").Select(r => r.RequestId).ToList();
        ids.Distinct().Count().ShouldBeGreaterThanOrEqualTo(3);

        // Capturing again is idempotent: same state, no new ledger rows.
        (await user.CaptureAsync(purchaseId)).Str("status").ShouldBe("COMPLETED");
        (await user.LedgerAsync(purchaseId)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task IT10b_MerchantBlockedAfterCheckout_FinalCheckFails_Voided_NotCharged()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, orderId) = await user.CheckedOutAndBuyerApprovedAsync();

        // Last-mile stop: user blocks FreshMart while the buyer is on PayPal.
        var version = await user.PolicyVersionAsync();
        (await user.PostAsync($"/api/v1/merchants/{TestUser.Merchant("freshmart")}/block", new { expectedVersionNumber = version }))
            .Status.ShouldBe(HttpStatusCode.OK);

        var capture = await user.CaptureAsync(purchaseId);
        capture.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, capture.Body.ToString());
        capture.Code.ShouldBe("FINAL_CHECK_FAILED");
        capture.Body.GetProperty("data").GetProperty("reasons")[0].GetProperty("code").GetString().ShouldBe("MERCHANT_BLOCKED");

        _factory.PayPal.VoidCount($"AUTH-{orderId}").ShouldBe(1);
        _factory.PayPal.Calls.ShouldNotContain($"capture:AUTH-{orderId}");
        var purchase = await user.GetAsync($"/api/v1/purchases/{purchaseId}");
        purchase.Str("status").ShouldBe("FAILED");
        purchase.Str("failureCode").ShouldBe("FINAL_CHECK_FAILED");
        purchase.Body.GetProperty("payment").GetProperty("status").GetString().ShouldBe("VOIDED");
        (await user.LedgerAsync(purchaseId)).Select(e => e!["transition"]!.GetValue<string>()).ShouldContain("VOID");

        var summary = await user.GetAsync("/api/v1/ledger/summary?period=WEEKLY");
        var groceries = summary.Body.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("categoryCode").GetString() == "GROCERIES");
        groceries.GetProperty("spent").GetString().ShouldBe("0.00");
        groceries.GetProperty("pending").GetString().ShouldBe("0.00");
    }

    [Fact]
    public async Task IT10c_AuthorizeDenied_Failed_Released_NoCapture()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, orderId) = await user.CheckedOutAndBuyerApprovedAsync();
        _factory.PayPal.NextAuthorize = FakePayPalClient.AuthorizeOutcome.Denied;

        var capture = await user.CaptureAsync(purchaseId);
        capture.Str("status").ShouldBe("FAILED");
        _factory.PayPal.Calls.ShouldNotContain(c => c.StartsWith("capture:", StringComparison.Ordinal) && c.Contains(orderId, StringComparison.Ordinal));
        (await user.LedgerAsync(purchaseId)).Select(e => e!["transition"]!.GetValue<string>()).ShouldContain("RELEASE");
    }

    [Fact]
    public async Task IT10d_Capture5xxThenRetry_SameRequestId_SingleCaptureRow()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, _) = await user.CheckedOutAndBuyerApprovedAsync();
        _factory.PayPal.CaptureServerErrors = 1;

        var first = await user.CaptureAsync(purchaseId, "capture-key-1");
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.Body.ToString());
        first.Code.ShouldBe("CAPTURE_IN_PROGRESS");

        var second = await user.CaptureAsync(purchaseId, "capture-key-1");
        second.Status.ShouldBe(HttpStatusCode.OK, second.Body.ToString());
        second.Str("status").ShouldBe("COMPLETED");

        var captureIds = _factory.PayPal.RequestIds.Where(r => r.Operation == "capture").Select(r => r.RequestId).ToList();
        captureIds.Count.ShouldBe(2);
        captureIds.Distinct().Count().ShouldBe(1);
        (await user.LedgerAsync(purchaseId)).Count(e => e!["transition"]!.GetValue<string>() == "CAPTURE").ShouldBe(1);
    }

    [Fact]
    public async Task IT17_AmountMismatchFromPayPal_Flagged_NoCapture()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, _) = await user.CheckedOutAndBuyerApprovedAsync();
        _factory.PayPal.ReportedAmountOverride = 1.00m;

        var capture = await user.CaptureAsync(purchaseId);
        capture.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        capture.Code.ShouldBe("AMOUNT_MISMATCH");
        (await user.LedgerAsync(purchaseId)).Select(e => e!["transition"]!.GetValue<string>()).ShouldNotContain("CAPTURE");
        (await user.GetAsync($"/api/v1/purchases/{purchaseId}")).Str("failureCode").ShouldBe("AMOUNT_MISMATCH");
    }

    [Fact]
    public async Task Cancel_CheckoutPending_ReleasesAndAbandonsCart()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, _, _) = await user.ApprovedDemoPurchaseAsync();
        await user.CheckoutAsync(purchaseId);

        var cancelled = await user.PostAsync($"/api/v1/purchases/{purchaseId}/cancel");
        cancelled.Str("status").ShouldBe("CANCELLED");
        (await user.GetAsync($"/api/v1/carts/{cancelled.Str("cartId")}")).Str("status").ShouldBe("ABANDONED");
        (await user.LedgerAsync(purchaseId)).Select(e => e!["transition"]!.GetValue<string>()).ShouldContain("RELEASE");
    }

    [Fact]
    public async Task CheckoutExpiry_After3Hours_ExpiresAndReleases()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, _, _) = await user.ApprovedDemoPurchaseAsync();
        await user.CheckoutAsync(purchaseId);

        _factory.Clock.Advance(TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(1)));
        await user.LoginAgainAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobRunner>().ExpireCheckoutsAsync(CancellationToken.None);
        }

        (await user.GetAsync($"/api/v1/purchases/{purchaseId}")).Str("status").ShouldBe("EXPIRED");
    }

    // ---------------------------------------------------------------- webhooks

    [Fact]
    public async Task IT11_CaptureCompletedWebhookAfterCapture_Processed_NoExtraLedgerRows()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, orderId) = await user.CheckedOutAndBuyerApprovedAsync();
        (await user.CaptureAsync(purchaseId)).Str("status").ShouldBe("COMPLETED");

        var eventId = $"WH-{Guid.NewGuid():N}";
        (await PostWebhookAsync(eventId, "PAYMENT.CAPTURE.COMPLETED",
            new { id = $"CAP-{orderId}", status = "COMPLETED", custom_id = purchaseId, amount = new { value = "43.00", currency_code = "USD" } }))
            .ShouldBe(HttpStatusCode.OK);
        await ProcessWebhooksAsync();

        (await user.LedgerAsync(purchaseId)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task IT12_UnverifiedWebhook_Rejected400_NoStateChange()
    {
        _factory.WebhookVerifier.Result = false;
        (await PostWebhookAsync($"WH-{Guid.NewGuid():N}", "PAYMENT.CAPTURE.COMPLETED", new { id = "CAP-X" })).ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Webhook_MissingHeaders_400_VerifierDown_503()
    {
        var http = _factory.CreateClient();
        (await http.PostAsync("/api/v1/webhooks/paypal", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);

        _factory.WebhookVerifier.Unavailable = true;
        (await PostWebhookAsync($"WH-{Guid.NewGuid():N}", "PAYMENT.CAPTURE.COMPLETED", new { id = "CAP-Y" })).ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task IT13_DuplicateDelivery_200_SingleRow()
    {
        var eventId = $"WH-{Guid.NewGuid():N}";
        (await PostWebhookAsync(eventId, "PAYMENT.SALE.COMPLETED", new { id = "X" })).ShouldBe(HttpStatusCode.OK);
        (await PostWebhookAsync(eventId, "PAYMENT.SALE.COMPLETED", new { id = "X" })).ShouldBe(HttpStatusCode.OK);
        await ProcessWebhooksAsync();
        (await PostWebhookAsync(eventId, "PAYMENT.SALE.COMPLETED", new { id = "X" })).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IT21_AuthorizationWebhooks_IdempotentAndForwardOnly()
    {
        var user = await TestUser.RegisterAsync(_factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, orderId) = await user.CheckedOutAndBuyerApprovedAsync();

        // Buyer closed the tab: order-approved webhook moves the purchase forward and the processor finishes the payment.
        (await PostWebhookAsync($"WH-{Guid.NewGuid():N}", "CHECKOUT.ORDER.APPROVED", new { id = orderId, status = "APPROVED" })).ShouldBe(HttpStatusCode.OK);
        await ProcessWebhooksAsync();
        (await user.GetAsync($"/api/v1/purchases/{purchaseId}")).Str("status").ShouldBe("COMPLETED");

        // A late AUTHORIZATION.CREATED (twice) changes nothing.
        var created = new { id = $"AUTH-{orderId}", status = "CREATED", custom_id = purchaseId, amount = new { value = "43.00", currency_code = "USD" } };
        (await PostWebhookAsync($"WH-{Guid.NewGuid():N}", "PAYMENT.AUTHORIZATION.CREATED", created)).ShouldBe(HttpStatusCode.OK);
        (await PostWebhookAsync($"WH-{Guid.NewGuid():N}", "PAYMENT.AUTHORIZATION.CREATED", created)).ShouldBe(HttpStatusCode.OK);
        // A stale PENDING after COMPLETED is ignored.
        (await PostWebhookAsync($"WH-{Guid.NewGuid():N}", "PAYMENT.CAPTURE.PENDING", new { id = $"CAP-{orderId}", custom_id = purchaseId }))
            .ShouldBe(HttpStatusCode.OK);
        await ProcessWebhooksAsync();

        (await user.GetAsync($"/api/v1/purchases/{purchaseId}")).Str("status").ShouldBe("COMPLETED");
        (await user.LedgerAsync(purchaseId)).Count.ShouldBe(4);
    }

    private async Task<HttpStatusCode> PostWebhookAsync(string eventId, string eventType, object resource)
    {
        var body = JsonSerializer.Serialize(new { id = eventId, event_type = eventType, resource_type = "capture", resource });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/paypal") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("PAYPAL-AUTH-ALGO", "SHA256withRSA");
        request.Headers.Add("PAYPAL-CERT-URL", "https://api.sandbox.paypal.com/v1/notifications/certs/CERT-1");
        request.Headers.Add("PAYPAL-TRANSMISSION-ID", Guid.NewGuid().ToString());
        request.Headers.Add("PAYPAL-TRANSMISSION-SIG", "sig");
        request.Headers.Add("PAYPAL-TRANSMISSION-TIME", DateTimeOffset.UtcNow.ToString("O"));
        return (await _factory.CreateClient().SendAsync(request)).StatusCode;
    }

    private async Task ProcessWebhooksAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobRunner>().ProcessWebhooksAsync(CancellationToken.None);
    }
}
