namespace Guardrail.Application.Abstractions;

public sealed record PayPalLineItem(string Name, string Sku, int Quantity, decimal UnitAmount);

public sealed record CreateOrderRequest(
    Guid PurchaseId,
    string Intent,
    decimal Amount,
    string Currency,
    string Description,
    IReadOnlyList<PayPalLineItem> Items,
    string ReturnUrl,
    string CancelUrl,
    string BrandName);

/// <summary>Parsed subset of an Orders v2 order (or authorization/capture) that the backend relies on.</summary>
public sealed record PayPalOrder(
    string Id,
    string Status,
    string? ApproveUrl,
    decimal? Amount,
    string? Currency,
    string? CustomId,
    string? PayerId,
    PayPalAuthorization? Authorization,
    PayPalCapture? Capture,
    string? DebugId);

public sealed record PayPalAuthorization(string Id, string Status, decimal? Amount, string? Currency, DateTimeOffset? ExpiresAt, string? DebugId);

public sealed record PayPalCapture(string Id, string Status, decimal? Amount, string? Currency, string? DebugId);

/// <summary>PayPal returned a 4xx/5xx with a parsed error body. Never surfaced raw (ERROR_HANDLING.md §3).</summary>
public sealed class PayPalApiException(int statusCode, string? name, string? debugId, IReadOnlyList<string> issues, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string? Name { get; } = name;

    public string? DebugId { get; } = debugId;

    public IReadOnlyList<string> Issues { get; } = issues;

    public bool IsTransient => StatusCode >= 500 || StatusCode == 429;

    public bool HasIssue(string issue) => Issues.Contains(issue, StringComparer.OrdinalIgnoreCase) || string.Equals(Name, issue, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Network failure or timeout reaching PayPal; safe to retry with the same PayPal-Request-Id.</summary>
public sealed class PayPalUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Only the Payment Service may depend on this port (BR-03, SEC-37).</summary>
public interface IPayPalClient
{
    Task<PayPalOrder> CreateOrderAsync(CreateOrderRequest request, Guid requestId, CancellationToken cancellationToken);

    Task<PayPalOrder> GetOrderAsync(string orderId, CancellationToken cancellationToken);

    Task<PayPalOrder> AuthorizeOrderAsync(string orderId, Guid requestId, CancellationToken cancellationToken);

    /// <summary>CAPTURE-intent fallback (PAYMENT_STATE_MACHINE.md §5).</summary>
    Task<PayPalOrder> CaptureOrderAsync(string orderId, Guid requestId, CancellationToken cancellationToken);

    Task<PayPalCapture> CaptureAuthorizationAsync(string authorizationId, Guid requestId, CancellationToken cancellationToken);

    Task VoidAuthorizationAsync(string authorizationId, Guid requestId, CancellationToken cancellationToken);

    Task<PayPalAuthorization> GetAuthorizationAsync(string authorizationId, CancellationToken cancellationToken);

    Task<PayPalCapture> GetCaptureAsync(string captureId, CancellationToken cancellationToken);
}

public sealed record WebhookVerificationRequest(
    string AuthAlgo,
    string CertUrl,
    string TransmissionId,
    string TransmissionSig,
    string TransmissionTime,
    string RawBody);

public interface IPayPalWebhookVerifier
{
    /// <summary>Returns SUCCESS/FAILURE. Throws <see cref="PayPalUnavailableException"/> when PayPal cannot be reached.</summary>
    Task<bool> VerifyAsync(WebhookVerificationRequest request, CancellationToken cancellationToken);
}
