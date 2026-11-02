using System.Net;
using Guardrail.Integration.Tests.Infrastructure;

namespace Guardrail.Integration.Tests;

public sealed class CaptureIntentFactory : IAsyncLifetime
{
    public GuardrailFactory Factory { get; } = new() { Intent = "CAPTURE" };

    public Task InitializeAsync() => Factory.InitializeAsync();

    public Task DisposeAsync() => Factory.DisposeAsync();
}

/// <summary>IT-10e: PayPal:Intent=CAPTURE fallback (PAYMENT_STATE_MACHINE.md §5).</summary>
public class CaptureIntentFallbackTests(CaptureIntentFactory fixture) : IClassFixture<CaptureIntentFactory>
{
    [Fact]
    public async Task IT10e_CaptureFallback_CompletesWithAuthorizeAndCaptureRows()
    {
        var user = await TestUser.RegisterAsync(fixture.Factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, orderId) = await user.CheckedOutAndBuyerApprovedAsync();

        var capture = await user.CaptureAsync(purchaseId);
        capture.Status.ShouldBe(HttpStatusCode.OK, capture.Body.ToString());
        capture.Str("status").ShouldBe("COMPLETED");
        fixture.Factory.PayPal.Calls.ShouldContain($"capture-order:{orderId}");
        fixture.Factory.PayPal.Calls.ShouldNotContain($"authorize:{orderId}");

        var transitions = (await user.LedgerAsync(purchaseId)).Select(e => e!["transition"]!.GetValue<string>()).ToList();
        transitions.ShouldContain("AUTHORIZE");
        transitions.ShouldContain("CAPTURE");
    }

    [Fact]
    public async Task CaptureFallback_FinalCheckFails_NeverCaptured_Released()
    {
        var user = await TestUser.RegisterAsync(fixture.Factory);
        await user.ApplyDemoPolicyAsync();
        var (purchaseId, orderId) = await user.CheckedOutAndBuyerApprovedAsync();
        var version = await user.PolicyVersionAsync();
        await user.PostAsync($"/api/v1/merchants/{TestUser.Merchant("freshmart")}/block", new { expectedVersionNumber = version });

        var capture = await user.CaptureAsync(purchaseId);
        capture.Code.ShouldBe("FINAL_CHECK_FAILED");
        fixture.Factory.PayPal.Calls.ShouldNotContain($"capture-order:{orderId}");
        (await user.LedgerAsync(purchaseId)).Select(e => e!["transition"]!.GetValue<string>()).ShouldContain("VOID");
    }
}
