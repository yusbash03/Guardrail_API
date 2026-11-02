using System.Collections.Concurrent;
using Guardrail.Application.Abstractions;

namespace Guardrail.Integration.Tests.Infrastructure;

/// <summary>Scripted PayPal Sandbox stand-in. Records every PayPal-Request-Id so tests can assert reuse (TESTING_STRATEGY.md §4).</summary>
public sealed class FakePayPalClient : IPayPalClient
{
    private readonly ConcurrentDictionary<string, OrderState> _orders = new();
    private readonly ConcurrentDictionary<Guid, string> _ordersByRequest = new();
    private int _sequence;

    public enum AuthorizeOutcome
    {
        Created,
        Denied,
        InstrumentDeclined,
    }

    public enum CaptureOutcome
    {
        Completed,
        Pending,
        Declined,
    }

    public ConcurrentQueue<(string Operation, Guid RequestId)> RequestIds { get; } = new();

    public ConcurrentQueue<string> Calls { get; } = new();

    public AuthorizeOutcome NextAuthorize { get; set; } = AuthorizeOutcome.Created;

    public CaptureOutcome NextCapture { get; set; } = CaptureOutcome.Completed;

    /// <summary>Number of upcoming capture calls that fail with a 5xx.</summary>
    public int CaptureServerErrors { get; set; }

    /// <summary>When set, GET order reports this amount (IT-17).</summary>
    public decimal? ReportedAmountOverride { get; set; }

    public void Reset()
    {
        NextAuthorize = AuthorizeOutcome.Created;
        NextCapture = CaptureOutcome.Completed;
        CaptureServerErrors = 0;
        ReportedAmountOverride = null;
        RequestIds.Clear();
        Calls.Clear();
    }

    public void ApproveOrder(string orderId) => _orders[orderId].Status = "APPROVED";

    public int VoidCount(string authorizationId) => Calls.Count(c => c == $"void:{authorizationId}");

    public Task<PayPalOrder> CreateOrderAsync(CreateOrderRequest request, Guid requestId, CancellationToken cancellationToken)
    {
        RequestIds.Enqueue(("create", requestId));
        Calls.Enqueue("create");
        var id = _ordersByRequest.GetOrAdd(requestId, _ =>
        {
            var orderId = $"ORDER-{Interlocked.Increment(ref _sequence):D6}";
            _orders[orderId] = new OrderState(orderId, request.Amount, request.Currency, request.PurchaseId.ToString(), request.Intent);
            return orderId;
        });
        return Task.FromResult(ToOrder(_orders[id]));
    }

    public Task<PayPalOrder> GetOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"get:{orderId}");
        return Task.FromResult(ToOrder(_orders[orderId]));
    }

    public Task<PayPalOrder> AuthorizeOrderAsync(string orderId, Guid requestId, CancellationToken cancellationToken)
    {
        RequestIds.Enqueue(("authorize", requestId));
        Calls.Enqueue($"authorize:{orderId}");
        var order = _orders[orderId];
        if (order.Status != "APPROVED" && order.Authorization is null)
        {
            throw new PayPalApiException(422, "UNPROCESSABLE_ENTITY", "dbg", ["ORDER_NOT_APPROVED"], "not approved");
        }

        if (order.Authorization is null)
        {
            switch (NextAuthorize)
            {
                case AuthorizeOutcome.InstrumentDeclined:
                    throw new PayPalApiException(422, "UNPROCESSABLE_ENTITY", "dbg", ["INSTRUMENT_DECLINED"], "declined");
                case AuthorizeOutcome.Denied:
                    order.Authorization = new PayPalAuthorization($"AUTH-{orderId}", "DENIED", order.Amount, order.Currency, null, "dbg");
                    break;
                default:
                    order.Authorization = new PayPalAuthorization($"AUTH-{orderId}", "CREATED", ReportedAmountOverride ?? order.Amount, order.Currency,
                        DateTimeOffset.UtcNow.AddDays(29), "dbg");
                    order.Status = "COMPLETED";
                    break;
            }
        }

        return Task.FromResult(ToOrder(order));
    }

    public Task<PayPalOrder> CaptureOrderAsync(string orderId, Guid requestId, CancellationToken cancellationToken)
    {
        RequestIds.Enqueue(("capture-order", requestId));
        Calls.Enqueue($"capture-order:{orderId}");
        var order = _orders[orderId];
        order.Capture ??= NewCapture(order);
        order.Status = "COMPLETED";
        return Task.FromResult(ToOrder(order));
    }

    public Task<PayPalCapture> CaptureAuthorizationAsync(string authorizationId, Guid requestId, CancellationToken cancellationToken)
    {
        RequestIds.Enqueue(("capture", requestId));
        Calls.Enqueue($"capture:{authorizationId}");
        if (CaptureServerErrors > 0)
        {
            CaptureServerErrors--;
            throw new PayPalApiException(500, "INTERNAL_SERVER_ERROR", "dbg", [], "boom");
        }

        var order = _orders.Values.Single(o => o.Authorization?.Id == authorizationId);
        order.Capture ??= NewCapture(order);
        return Task.FromResult(order.Capture);
    }

    public Task VoidAuthorizationAsync(string authorizationId, Guid requestId, CancellationToken cancellationToken)
    {
        RequestIds.Enqueue(("void", requestId));
        Calls.Enqueue($"void:{authorizationId}");
        var order = _orders.Values.Single(o => o.Authorization?.Id == authorizationId);
        order.Authorization = order.Authorization! with { Status = "VOIDED" };
        return Task.CompletedTask;
    }

    public Task<PayPalAuthorization> GetAuthorizationAsync(string authorizationId, CancellationToken cancellationToken) =>
        Task.FromResult(_orders.Values.Single(o => o.Authorization?.Id == authorizationId).Authorization!);

    public Task<PayPalCapture> GetCaptureAsync(string captureId, CancellationToken cancellationToken) =>
        Task.FromResult(_orders.Values.Single(o => o.Capture?.Id == captureId).Capture!);

    private PayPalCapture NewCapture(OrderState order) => NextCapture switch
    {
        CaptureOutcome.Pending => new PayPalCapture($"CAP-{order.Id}", "PENDING", order.Amount, order.Currency, "dbg"),
        CaptureOutcome.Declined => new PayPalCapture($"CAP-{order.Id}", "DECLINED", order.Amount, order.Currency, "dbg"),
        _ => new PayPalCapture($"CAP-{order.Id}", "COMPLETED", order.Amount, order.Currency, "dbg"),
    };

    private PayPalOrder ToOrder(OrderState o) => new(
        o.Id, o.Status, $"https://www.sandbox.paypal.com/checkoutnow?token={o.Id}", ReportedAmountOverride ?? o.Amount, o.Currency, o.CustomId,
        "PAYER123", o.Authorization, o.Capture, "dbg");

    private sealed class OrderState(string id, decimal amount, string currency, string customId, string intent)
    {
        public string Id { get; } = id;

        public decimal Amount { get; } = amount;

        public string Currency { get; } = currency;

        public string CustomId { get; } = customId;

        public string Intent { get; } = intent;

        public string Status { get; set; } = "PAYER_ACTION_REQUIRED";

        public PayPalAuthorization? Authorization { get; set; }

        public PayPalCapture? Capture { get; set; }
    }
}

public sealed class FakeWebhookVerifier : IPayPalWebhookVerifier
{
    public bool Result { get; set; } = true;

    public bool Unavailable { get; set; }

    public Task<bool> VerifyAsync(WebhookVerificationRequest request, CancellationToken cancellationToken) =>
        Unavailable ? throw new PayPalUnavailableException("down") : Task.FromResult(Result);
}

/// <summary>Replays a script of model steps; each step sees the conversation so far (tool results included). CI never calls a real LLM.</summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    private readonly ConcurrentQueue<Func<IReadOnlyList<LlmMessage>, LlmResponse>> _steps = new();

    public bool Unavailable { get; set; }

    public List<IReadOnlyList<LlmMessage>> Requests { get; } = [];

    public void Script(params Func<IReadOnlyList<LlmMessage>, LlmResponse>[] steps)
    {
        _steps.Clear();
        foreach (var step in steps)
        {
            _steps.Enqueue(step);
        }
    }

    public static LlmResponse Text(string text) => new(text, [], "Scripted", "test", null);

    public static LlmResponse Tools(params (string Name, string Args)[] calls) =>
        new(null, calls.Select((c, i) => new LlmToolCall($"call_{Guid.NewGuid():N}_{i}", c.Name, c.Args)).ToList(), "Scripted", "test", null);

    public Task<LlmResponse> CompleteAsync(
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, LlmOptions options, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new LlmUnavailableException("down", TimeSpan.FromSeconds(30));
        }

        lock (Requests)
        {
            Requests.Add(messages.ToList());
        }

        return Task.FromResult(_steps.TryDequeue(out var step) ? step(messages) : Text("Done."));
    }
}
