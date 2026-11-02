using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrail.Infrastructure.Persistence.Seed;

namespace Guardrail.Integration.Tests.Infrastructure;

public sealed record ApiResult(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)
{
    public string Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty;

    public string Str(string property) => Body.GetProperty(property).ToString();

    public Guid Id(string property = "id") => Guid.Parse(Body.GetProperty(property).GetString()!);
}

public sealed class TestUser(GuardrailFactory factory, HttpClient http, Guid id, string email, string password)
{
    public static readonly string[] DemoSkus =
    [
        "FM-MLK-1G", "FM-EGG-12", "FM-BRD-WW", "FM-CHK-2LB", "FM-RICE-5LB", "FM-BROC-1LB", "FM-CARR-2LB", "FM-SPIN-5OZ", "FM-BAN-2LB", "FM-YOG-32OZ",
    ];

    public HttpClient Http { get; } = http;

    public Guid Id { get; } = id;

    public string Email { get; } = email;

    public string Password { get; } = password;

    public GuardrailFactory Factory { get; } = factory;

    public static Guid Merchant(string slug) => CatalogueSeed.SeedId("merchant:" + slug);

    public static Guid Product(string sku) => CatalogueSeed.SeedId("product:" + sku);

    public static async Task<TestUser> RegisterAsync(GuardrailFactory factory, string timeZone = "America/New_York")
    {
        var http = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var email = $"user-{Guid.NewGuid():N}@example.com";
        const string password = "correct horse battery staple";
        var response = await http.PostAsJsonAsync("/api/v1/auth/register", new { email, password, displayName = "Amara", timeZone });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body.ToString());
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return new TestUser(factory, http, Guid.Parse(body.GetProperty("user").GetProperty("id").GetString()!), email, password);
    }

    /// <summary>After advancing the fake clock past the 15-minute access-token lifetime, sign in again.</summary>
    public async Task LoginAgainAsync()
    {
        var response = await Http.PostAsJsonAsync("/api/v1/auth/login", new { email = Email, password = Password });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToString());
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
    }

    public Task<ApiResult> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public Task<ApiResult> PostAsync(string path, object? body = null, string? idempotencyKey = null, IDictionary<string, string>? headers = null)
    {
        var all = new Dictionary<string, string>(headers ?? new Dictionary<string, string>());
        if (idempotencyKey is not null)
        {
            all["Idempotency-Key"] = idempotencyKey;
        }

        return SendAsync(HttpMethod.Post, path, body, all);
    }

    public Task<ApiResult> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);

    public async Task<ApiResult> SendAsync(HttpMethod method, string path, object? body, IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return new ApiResult(response.StatusCode, json, response);
    }

    // ------------------------------------------------------------------ scenario helpers

    public async Task<int> PolicyVersionAsync() => (await GetAsync("/api/v1/policies")).Body.GetProperty("versionNumber").GetInt32();

    /// <summary>Demo policy v5 shape: GROCERIES $80/week, threshold $30, FreshMart + GreenGrocer approved, new merchants BLOCK, alcohol blocked.</summary>
    public async Task<ApiResult> ApplyDemoPolicyAsync(decimal groceriesWeekly = 80m, decimal threshold = 30m, Guid[]? blocked = null)
    {
        var version = await PolicyVersionAsync();
        var result = await PutAsync("/api/v1/policies", new
        {
            expectedVersionNumber = version,
            changeSummary = "Demo policy",
            policy = new
            {
                approvalThreshold = new { amount = threshold.ToString("0.00") },
                maxTransactionAmount = (object?)null,
                spendingLimits = Array.Empty<object>(),
                categoryBudgets = new[] { new { categoryCode = "GROCERIES", period = "WEEKLY", limit = groceriesWeekly.ToString("0.00") } },
                merchantRules = new
                {
                    approvedMerchantIds = new[] { Merchant("freshmart"), Merchant("greengrocer-co") },
                    blockedMerchantIds = blocked ?? [],
                    newMerchantAction = "BLOCK",
                },
                productRestrictions = new[] { new { match = new { type = "CATEGORY", value = "ALCOHOL" }, action = "BLOCK", note = (string?)null } },
                duplicatePurchase = (object?)null,
                timeRestrictions = Array.Empty<object>(),
                unbudgetedCategoryAction = "REQUIRE_APPROVAL",
            },
        });
        result.Status.ShouldBe(HttpStatusCode.OK, result.Body.ToString());
        return result;
    }

    public async Task<ApiResult> CreateCartAsync(string merchantSlug, params (string Sku, int Quantity)[] items)
    {
        var result = await PostAsync("/api/v1/carts", new
        {
            merchantId = Merchant(merchantSlug),
            items = items.Select(i => new { productId = Product(i.Sku), quantity = i.Quantity }).ToArray(),
        });
        result.Status.ShouldBe(HttpStatusCode.Created, result.Body.ToString());
        return result;
    }

    public Task<ApiResult> CreateDemoCartAsync() => CreateCartAsync("freshmart", DemoSkus.Select(s => (s, 1)).ToArray());

    public async Task<ApiResult> EvaluateAsync(Guid cartId)
    {
        var result = await PostAsync($"/api/v1/carts/{cartId}/evaluate");
        result.Status.ShouldBe(HttpStatusCode.OK, result.Body.ToString());
        return result;
    }

    public Task<ApiResult> CreatePurchaseAsync(Guid cartId, Guid evaluationId, string? key = null) =>
        PostAsync("/api/v1/purchases", new { cartId, evaluationId }, key ?? Guid.NewGuid().ToString());

    public Task<ApiResult> ApproveAsync(Guid approvalId, string hash, string? password = null) =>
        PostAsync($"/api/v1/approvals/{approvalId}/approve", new { cartSnapshotHash = hash, password = password ?? Password },
            headers: new Dictionary<string, string> { ["X-Guardrail-Client"] = "web" });

    public Task<ApiResult> CheckoutAsync(Guid purchaseId, string? key = null) =>
        PostAsync($"/api/v1/purchases/{purchaseId}/checkout", null, key ?? Guid.NewGuid().ToString());

    public Task<ApiResult> CaptureAsync(Guid purchaseId, string? key = null) =>
        PostAsync($"/api/v1/purchases/{purchaseId}/capture", null, key ?? Guid.NewGuid().ToString());

    /// <summary>Demo cart → evaluate → purchase (AWAITING_APPROVAL) → approve → READY_FOR_CHECKOUT.</summary>
    public async Task<(Guid PurchaseId, Guid ApprovalId, string Hash)> ApprovedDemoPurchaseAsync()
    {
        var cart = await CreateDemoCartAsync();
        var evaluation = await EvaluateAsync(cart.Id());
        var purchase = await CreatePurchaseAsync(cart.Id(), evaluation.Id("evaluationId"));
        purchase.Status.ShouldBe(HttpStatusCode.Created, purchase.Body.ToString());
        var approvalId = Guid.Parse(purchase.Body.GetProperty("approval").GetProperty("id").GetString()!);
        var hash = purchase.Str("cartSnapshotHash");
        var approved = await ApproveAsync(approvalId, hash);
        approved.Status.ShouldBe(HttpStatusCode.OK, approved.Body.ToString());
        return (purchase.Id(), approvalId, hash);
    }

    /// <summary>…→ checkout → buyer approves in (fake) PayPal. Returns the PayPal order id.</summary>
    public async Task<(Guid PurchaseId, string OrderId)> CheckedOutAndBuyerApprovedAsync()
    {
        var (purchaseId, _, _) = await ApprovedDemoPurchaseAsync();
        var checkout = await CheckoutAsync(purchaseId);
        checkout.Status.ShouldBe(HttpStatusCode.OK, checkout.Body.ToString());
        var orderId = checkout.Str("paypalOrderId");
        Factory.PayPal.ApproveOrder(orderId);
        return (purchaseId, orderId);
    }

    public async Task<JsonArray> LedgerAsync(Guid purchaseId)
    {
        var result = await GetAsync($"/api/v1/ledger/entries?purchaseId={purchaseId}&pageSize=100");
        return JsonNode.Parse(result.Body.GetProperty("items").GetRawText())!.AsArray();
    }
}
