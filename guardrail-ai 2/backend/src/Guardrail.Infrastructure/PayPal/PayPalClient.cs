using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Guardrail.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Guardrail.Infrastructure.PayPal;

public sealed class PayPalOptions
{
    public const string Section = "PayPal";
    public const string SandboxHost = "api-m.sandbox.paypal.com";

    public string BaseUrl { get; set; } = "https://" + SandboxHost;

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? WebhookId { get; set; }

    public string Intent { get; set; } = "AUTHORIZE";

    /// <summary>Development only: start without credentials (PayPal calls then fail with PAYMENT_PROVIDER_UNAVAILABLE).</summary>
    public bool AllowMissingCredentials { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>SEC-35: the live PayPal host is rejected at startup; credentials are required outside development.</summary>
public sealed class PayPalOptionsValidator : IValidateOptions<PayPalOptions>
{
    public ValidateOptionsResult Validate(string? name, PayPalOptions o)
    {
        if (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri) || uri.Host != PayPalOptions.SandboxHost || uri.Scheme != Uri.UriSchemeHttps)
        {
            return ValidateOptionsResult.Fail($"PayPal:BaseUrl must be https://{PayPalOptions.SandboxHost} (Sandbox only).");
        }

        if (o.Intent is not ("AUTHORIZE" or "CAPTURE"))
        {
            return ValidateOptionsResult.Fail("PayPal:Intent must be AUTHORIZE or CAPTURE.");
        }

        if (!o.AllowMissingCredentials
            && (string.IsNullOrWhiteSpace(o.ClientId) || string.IsNullOrWhiteSpace(o.ClientSecret) || string.IsNullOrWhiteSpace(o.WebhookId)))
        {
            return ValidateOptionsResult.Fail("PayPal:ClientId, PayPal:ClientSecret and PayPal:WebhookId are required.");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>OAuth 2.0 client-credentials token cached until expires_in − 120 s, guarded against stampedes (PAYPAL_INTEGRATION.md §3).</summary>
public sealed class PayPalTokenProvider(IHttpClientFactory httpClientFactory, IOptions<PayPalOptions> options, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && clock.GetUtcNow() < _expiresAt)
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _expiresAt)
            {
                return _token;
            }

            var settings = options.Value;
            if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
            {
                throw new PayPalUnavailableException("PayPal credentials are not configured.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/oauth2/token")
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ClientId}:{settings.ClientSecret}")));

            using var response = await Send(httpClientFactory.CreateClient(PayPalClient.HttpClientName), request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw PayPalClient.ToException(response, body);
            }

            using var doc = JsonDocument.Parse(body);
            _token = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _expiresAt = clock.GetUtcNow().AddSeconds(Math.Max(expiresIn - 120, 30));
            return _token!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _token = null;

    internal static async Task<HttpResponseMessage> Send(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new PayPalUnavailableException("PayPal could not be reached.", ex);
        }
    }
}

/// <summary>Orders v2 + Payments v2 REST client. The only component that talks to PayPal (BR-03).</summary>
public sealed class PayPalClient(
    IHttpClientFactory httpClientFactory,
    PayPalTokenProvider tokens,
    ILogger<PayPalClient> log) : IPayPalClient
{
    public const string HttpClientName = "paypal";

    public async Task<PayPalOrder> CreateOrderAsync(CreateOrderRequest r, Guid requestId, CancellationToken cancellationToken)
    {
        var body = new
        {
            intent = r.Intent,
            purchase_units = new[]
            {
                new
                {
                    reference_id = r.PurchaseId.ToString(),
                    custom_id = r.PurchaseId.ToString(),
                    invoice_id = $"GR-{r.PurchaseId}",
                    description = r.Description,
                    amount = new
                    {
                        currency_code = r.Currency,
                        value = Amount(r.Amount),
                        breakdown = new { item_total = new { currency_code = r.Currency, value = Amount(r.Items.Sum(i => i.UnitAmount * i.Quantity)) } },
                    },
                    items = r.Items.Select(i => new
                    {
                        name = i.Name,
                        sku = i.Sku,
                        quantity = i.Quantity.ToString(CultureInfo.InvariantCulture),
                        unit_amount = new { currency_code = r.Currency, value = Amount(i.UnitAmount) },
                    }).ToArray(),
                },
            },
            payment_source = new
            {
                paypal = new
                {
                    experience_context = new
                    {
                        brand_name = r.BrandName,
                        user_action = "PAY_NOW",
                        shipping_preference = "NO_SHIPPING",
                        return_url = r.ReturnUrl,
                        cancel_url = r.CancelUrl,
                    },
                },
            },
        };
        var (json, debugId) = await SendAsync(HttpMethod.Post, "/v2/checkout/orders", JsonSerializer.Serialize(body), requestId, cancellationToken);
        return ParseOrder(json, debugId);
    }

    public async Task<PayPalOrder> GetOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        var (json, debugId) = await SendAsync(HttpMethod.Get, $"/v2/checkout/orders/{Uri.EscapeDataString(orderId)}", null, null, cancellationToken);
        return ParseOrder(json, debugId);
    }

    public async Task<PayPalOrder> AuthorizeOrderAsync(string orderId, Guid requestId, CancellationToken cancellationToken)
    {
        var (json, debugId) = await SendAsync(HttpMethod.Post, $"/v2/checkout/orders/{Uri.EscapeDataString(orderId)}/authorize", "{}", requestId,
            cancellationToken);
        return ParseOrder(json, debugId);
    }

    public async Task<PayPalOrder> CaptureOrderAsync(string orderId, Guid requestId, CancellationToken cancellationToken)
    {
        var (json, debugId) = await SendAsync(HttpMethod.Post, $"/v2/checkout/orders/{Uri.EscapeDataString(orderId)}/capture", "{}", requestId,
            cancellationToken);
        return ParseOrder(json, debugId);
    }

    public async Task<PayPalCapture> CaptureAuthorizationAsync(string authorizationId, Guid requestId, CancellationToken cancellationToken)
    {
        var (json, debugId) = await SendAsync(HttpMethod.Post, $"/v2/payments/authorizations/{Uri.EscapeDataString(authorizationId)}/capture", "{}",
            requestId, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return ParseCapture(doc.RootElement, debugId);
    }

    public async Task VoidAuthorizationAsync(string authorizationId, Guid requestId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, $"/v2/payments/authorizations/{Uri.EscapeDataString(authorizationId)}/void", null, requestId, cancellationToken);

    public async Task<PayPalAuthorization> GetAuthorizationAsync(string authorizationId, CancellationToken cancellationToken)
    {
        var (json, debugId) = await SendAsync(HttpMethod.Get, $"/v2/payments/authorizations/{Uri.EscapeDataString(authorizationId)}", null, null,
            cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return ParseAuthorization(doc.RootElement, debugId);
    }

    public async Task<PayPalCapture> GetCaptureAsync(string captureId, CancellationToken cancellationToken)
    {
        var (json, debugId) = await SendAsync(HttpMethod.Get, $"/v2/payments/captures/{Uri.EscapeDataString(captureId)}", null, null, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return ParseCapture(doc.RootElement, debugId);
    }

    /// <summary>Sends with bearer auth; on 401 invalidates the token and retries once. POSTs always carry PayPal-Request-Id (SEC-34).</summary>
    private async Task<(string Json, string? DebugId)> SendAsync(
        HttpMethod method, string path, string? jsonBody, Guid? requestId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await tokens.GetTokenAsync(cancellationToken);
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
            if (requestId is { } id)
            {
                request.Headers.TryAddWithoutValidation("PayPal-Request-Id", id.ToString());
            }

            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }
            else if (method == HttpMethod.Post)
            {
                request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            }

            using var response = await PayPalTokenProvider.Send(httpClientFactory.CreateClient(HttpClientName), request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var debugId = DebugId(response);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                tokens.Invalidate();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var ex = ToException(response, body);
                log.LogWarning("PayPal {Method} {Path} → {Status} {Name} debug_id={DebugId}", method, Redact(path), (int)response.StatusCode, ex.Name, ex.DebugId);
                throw ex;
            }

            return (string.IsNullOrWhiteSpace(body) ? "{}" : body, debugId);
        }
    }

    internal static PayPalApiException ToException(HttpResponseMessage response, string body)
    {
        string? name = null, debugId = DebugId(response), message = null;
        var issues = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            name = root.TryGetProperty("name", out var n) ? n.GetString() : root.TryGetProperty("error", out var e) ? e.GetString() : null;
            message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            debugId = root.TryGetProperty("debug_id", out var d) ? d.GetString() : debugId;
            if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                issues.AddRange(details.EnumerateArray()
                    .Select(x => x.TryGetProperty("issue", out var i) ? i.GetString() : null)
                    .OfType<string>());
            }
        }
        catch (JsonException)
        {
            // non-JSON error body
        }

        return new PayPalApiException((int)response.StatusCode, name, debugId, issues,
            $"PayPal returned {(int)response.StatusCode} {name}: {message}");
    }

    internal static PayPalOrder ParseOrder(string json, string? debugId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? approveUrl = null;
        if (root.TryGetProperty("links", out var links))
        {
            foreach (var link in links.EnumerateArray())
            {
                var rel = link.GetProperty("rel").GetString();
                if (rel is "approve" or "payer-action")
                {
                    approveUrl = link.GetProperty("href").GetString();
                }
            }
        }

        decimal? amount = null;
        string? currency = null, customId = null;
        PayPalAuthorization? authorization = null;
        PayPalCapture? capture = null;
        if (root.TryGetProperty("purchase_units", out var units) && units.GetArrayLength() > 0)
        {
            var unit = units[0];
            (amount, currency) = ParseAmount(unit, "amount");
            customId = unit.TryGetProperty("custom_id", out var c) ? c.GetString() : null;
            if (unit.TryGetProperty("payments", out var payments))
            {
                if (payments.TryGetProperty("authorizations", out var auths) && auths.GetArrayLength() > 0)
                {
                    authorization = ParseAuthorization(auths[0], debugId);
                }

                if (payments.TryGetProperty("captures", out var caps) && caps.GetArrayLength() > 0)
                {
                    capture = ParseCapture(caps[0], debugId);
                }
            }
        }

        var payerId = root.TryGetProperty("payer", out var payer) && payer.TryGetProperty("payer_id", out var pid) ? pid.GetString() : null;
        return new PayPalOrder(root.GetProperty("id").GetString()!, root.TryGetProperty("status", out var s) ? s.GetString()! : "UNKNOWN",
            approveUrl, amount, currency, customId, payerId, authorization, capture, debugId);
    }

    private static PayPalAuthorization ParseAuthorization(JsonElement e, string? debugId)
    {
        var (amount, currency) = ParseAmount(e, "amount");
        DateTimeOffset? expires = e.TryGetProperty("expiration_time", out var x)
                                  && DateTimeOffset.TryParse(x.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
        return new PayPalAuthorization(e.GetProperty("id").GetString()!, e.GetProperty("status").GetString()!, amount, currency, expires, debugId);
    }

    private static PayPalCapture ParseCapture(JsonElement e, string? debugId)
    {
        var (amount, currency) = ParseAmount(e, "amount");
        return new PayPalCapture(e.GetProperty("id").GetString()!, e.GetProperty("status").GetString()!, amount, currency, debugId);
    }

    private static (decimal? Amount, string? Currency) ParseAmount(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var a))
        {
            return (null, null);
        }

        decimal? value = a.TryGetProperty("value", out var v)
                         && decimal.TryParse(v.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
        return (value, a.TryGetProperty("currency_code", out var c) ? c.GetString() : null);
    }

    private static string? DebugId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("PayPal-Debug-Id", out var values) ? values.FirstOrDefault() : null;

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Redact(string path) => path.Length > 80 ? path[..80] : path;
}

/// <summary>ADR-006: postback verification; the event body is spliced in verbatim (no re-serialization).</summary>
public sealed class PayPalWebhookVerifier(
    IHttpClientFactory httpClientFactory,
    PayPalTokenProvider tokens,
    IOptions<PayPalOptions> options,
    ILogger<PayPalWebhookVerifier> log) : IPayPalWebhookVerifier
{
    public async Task<bool> VerifyAsync(WebhookVerificationRequest request, CancellationToken cancellationToken)
    {
        var webhookId = options.Value.WebhookId;
        if (string.IsNullOrWhiteSpace(webhookId))
        {
            log.LogError("PayPal:WebhookId is not configured; rejecting webhook");
            return false;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("auth_algo", request.AuthAlgo);
            writer.WriteString("cert_url", request.CertUrl);
            writer.WriteString("transmission_id", request.TransmissionId);
            writer.WriteString("transmission_sig", request.TransmissionSig);
            writer.WriteString("transmission_time", request.TransmissionTime);
            writer.WriteString("webhook_id", webhookId);
            writer.WritePropertyName("webhook_event");
            writer.WriteRawValue(request.RawBody, skipInputValidation: false);
            writer.WriteEndObject();
        }

        var token = await tokens.GetTokenAsync(cancellationToken);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/notifications/verify-webhook-signature")
        {
            Content = new ByteArrayContent(stream.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await PayPalTokenProvider.Send(httpClientFactory.CreateClient(PayPalClient.HttpClientName), message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new PayPalUnavailableException($"Webhook verification returned {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            log.LogWarning("Webhook verification call failed with {Status}", (int)response.StatusCode);
            return false;
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("verification_status", out var status) && status.GetString() == "SUCCESS";
    }
}
