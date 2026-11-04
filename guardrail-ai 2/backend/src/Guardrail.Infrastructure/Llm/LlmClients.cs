using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Guardrail.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Guardrail.Infrastructure.Llm;

public sealed class LlmProviderOptions
{
    public string Provider { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string? ApiKey { get; set; }

    public int RequestsPerMinute { get; set; } = 8;

    /// <summary>Optional OpenAI-style reasoning_effort (e.g. "low"); thinking models are otherwise slow enough to hit the timeout.</summary>
    public string? ReasoningEffort { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class LlmSettings
{
    public const string Section = "Llm";

    public LlmProviderOptions Primary { get; set; } = new()
    {
        Provider = "Gemini", BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/", RequestsPerMinute = 8, ReasoningEffort = "low",
    };

    public LlmProviderOptions Fallback { get; set; } = new()
    {
        Provider = "Groq", BaseUrl = "https://api.groq.com/openai/v1", Model = "openai/gpt-oss-120b", RequestsPerMinute = 25,
    };

    public int TimeoutSeconds { get; set; } = 50;

    /// <summary>Longest Retry-After we wait on the primary before failing over for the rest of the turn.</summary>
    public int MaxRetryAfterSeconds { get; set; } = 5;

    /// <summary>
    /// Whole-attempt budget for the primary (queueing, the call and any Retry-After wait). Past it we fail over:
    /// Gemini's free tier sometimes stalls well beyond its usual 8–20 s.
    /// </summary>
    public int PrimaryTimeoutSeconds { get; set; } = 50;

    /// <summary>
    /// Longest Retry-After we wait on the fallback. It is the last resort, so waiting beats failing the turn
    /// (Groq's free tier is 8k tokens/min and a shopping turn exceeds it); bounded by Agent:TurnTimeoutSeconds anyway.
    /// </summary>
    public int FallbackMaxRetryAfterSeconds { get; set; } = 45;
}

public sealed class LlmProviderException(string provider, HttpStatusCode? status, TimeSpan? retryAfter, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Provider { get; } = provider;

    public HttpStatusCode? Status { get; } = status;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Chat-completions + tools adapter for any OpenAI-compatible endpoint (Gemini primary, Groq fallback — ADR-011).</summary>
public sealed class OpenAiCompatibleLlmClient(HttpClient http, LlmProviderOptions provider, ILogger log)
{
    // Keywords some providers reject; arguments are re-validated server-side regardless (AI_TOOL_CONTRACTS.md).
    private static readonly string[] UnsupportedSchemaKeywords = ["additionalProperties", "format", "pattern", "default"];

    public string Name => provider.Provider;

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, LlmOptions options, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = provider.Model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)ToJson(m, provider.Provider)).ToArray<JsonNode?>()),
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxOutputTokens,
        };
        if (!string.IsNullOrWhiteSpace(provider.ReasoningEffort))
        {
            body["reasoning_effort"] = provider.ReasoningEffort;
        }

        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = Sanitize(t.Parameters.DeepClone()),
                },
            }).ToArray<JsonNode?>());
            body["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new LlmProviderException(provider.Provider, null, null, $"{provider.Provider} unreachable or timed out.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("LLM {Provider} returned {Status}", provider.Provider, (int)response.StatusCode);
                throw new LlmProviderException(provider.Provider, response.StatusCode, RetryAfter(response),
                    $"{provider.Provider} returned {(int)response.StatusCode}.");
            }

            return Parse(text);
        }
    }

    private LlmResponse Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var calls = new List<LlmToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var id = call.TryGetProperty("id", out var i) && !string.IsNullOrEmpty(i.GetString()) ? i.GetString()! : $"call_{Guid.NewGuid():N}";
                var args = function.TryGetProperty("arguments", out var a)
                    ? a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText()
                    : "{}";
                var extra = call.TryGetProperty("extra_content", out var e) && e.ValueKind == JsonValueKind.Object
                    ? new LlmProviderMetadata(provider.Provider, e.GetRawText())
                    : null;
                calls.Add(new LlmToolCall(id, function.GetProperty("name").GetString() ?? string.Empty, args, extra));
            }
        }

        LlmUsage? usage = null;
        if (doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            usage = new LlmUsage(
                u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0,
                u.TryGetProperty("completion_tokens", out var o) ? o.GetInt32() : 0);
        }

        return new LlmResponse(content, calls, provider.Provider, provider.Model, usage);
    }

    private static JsonObject ToJson(LlmMessage m, string providerName)
    {
        var obj = new JsonObject
        {
            ["role"] = m.Role switch
            {
                LlmRole.System => "system",
                LlmRole.User => "user",
                LlmRole.Assistant => "assistant",
                _ => "tool",
            },
            ["content"] = m.Content ?? string.Empty,
        };
        if (m.ToolCalls is { Count: > 0 } calls)
        {
            obj["tool_calls"] = new JsonArray(calls.Select(c =>
            {
                var call = new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                };

                // Only echo metadata to the provider that produced it; others (Groq) reject unknown fields after a failover.
                if (c.ProviderMetadata is { } meta && meta.Provider == providerName)
                {
                    call["extra_content"] = JsonNode.Parse(meta.Json);
                }

                return (JsonNode)call;
            }).ToArray<JsonNode?>());
        }

        if (m.ToolCallId is not null)
        {
            obj["tool_call_id"] = m.ToolCallId;
        }

        return obj;
    }

    private static JsonNode? Sanitize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in UnsupportedSchemaKeywords)
                {
                    obj.Remove(key);
                }

                foreach (var (_, child) in obj.ToList())
                {
                    Sanitize(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Sanitize(child);
                }

                break;
        }

        return node;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
}

/// <summary>Per-provider token buckets shared by all requests (client-side limiter so we queue instead of hitting 429).</summary>
public sealed class LlmRateLimiters(IOptions<LlmSettings> options) : IDisposable
{
    public RateLimiter Primary { get; } = Create(options.Value.Primary.RequestsPerMinute);

    public RateLimiter Fallback { get; } = Create(options.Value.Fallback.RequestsPerMinute);

    public void Dispose()
    {
        Primary.Dispose();
        Fallback.Dispose();
    }

    private static TokenBucketRateLimiter Create(int perMinute) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = Math.Max(1, perMinute),
        TokensPerPeriod = Math.Max(1, perMinute),
        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
        QueueLimit = 10,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    });
}

/// <summary>
/// Primary → fallback failover (AI_AGENT_SPECIFICATION.md §3). Scoped per request so a failover lasts for the rest of the turn,
/// except that a failing fallback hands back to the primary once rather than ending the turn.
/// Both unavailable → <see cref="LlmUnavailableException"/> (503 AGENT_UNAVAILABLE); the rest of the app keeps working.
/// </summary>
public sealed class RateLimitedLlmRouter(
    IHttpClientFactory httpClientFactory,
    LlmRateLimiters limiters,
    IOptions<LlmSettings> options,
    ILogger<RateLimitedLlmRouter> log) : ILlmClient
{
    public const string PrimaryClientName = "llm-primary";
    public const string FallbackClientName = "llm-fallback";

    private bool _primaryDisabled;

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, LlmOptions llmOptions, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        TimeSpan? retryAfter = null;
        var primaryTried = false;

        if (!_primaryDisabled && settings.Primary.IsConfigured)
        {
            primaryTried = true;
            try
            {
                return await CallPrimaryAsync(settings, messages, tools, llmOptions, cancellationToken);
            }
            catch (LlmProviderException ex)
            {
                retryAfter = ex.RetryAfter;
                log.LogWarning("Primary LLM {Provider} failed ({Status}); failing over for the rest of the turn", ex.Provider, ex.Status);
                _primaryDisabled = true;
            }
        }

        if (settings.Fallback.IsConfigured)
        {
            try
            {
                return await CallAsync(FallbackClientName, settings.Fallback, limiters.Fallback, messages, tools, llmOptions,
                    TimeSpan.FromSeconds(settings.FallbackMaxRetryAfterSeconds), cancellationToken);
            }
            catch (LlmProviderException ex)
            {
                retryAfter = ex.RetryAfter ?? retryAfter;
                log.LogWarning("Fallback LLM {Provider} failed ({Status})", ex.Provider, ex.Status);
            }
        }

        // The fallback is rate-limited mid-turn (Groq's 8k tokens/min): the primary failed on an earlier step, so give it another go.
        if (!primaryTried && settings.Primary.IsConfigured)
        {
            try
            {
                return await CallPrimaryAsync(settings, messages, tools, llmOptions, cancellationToken);
            }
            catch (LlmProviderException ex)
            {
                retryAfter = ex.RetryAfter ?? retryAfter;
                log.LogWarning("Primary LLM {Provider} failed again ({Status})", ex.Provider, ex.Status);
            }
        }

        throw new LlmUnavailableException("No LLM provider is available.", retryAfter ?? TimeSpan.FromSeconds(30));
    }

    private async Task<LlmResponse> CallPrimaryAsync(
        LlmSettings settings,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        LlmOptions llmOptions,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(settings.PrimaryTimeoutSeconds));
        try
        {
            return await CallAsync(PrimaryClientName, settings.Primary, limiters.Primary, messages, tools, llmOptions,
                TimeSpan.FromSeconds(settings.MaxRetryAfterSeconds), attempt.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmProviderException(settings.Primary.Provider, null, null,
                $"{settings.Primary.Provider} did not respond within {settings.PrimaryTimeoutSeconds}s.", ex);
        }
    }

    private async Task<LlmResponse> CallAsync(
        string clientName,
        LlmProviderOptions provider,
        RateLimiter limiter,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        LlmOptions llmOptions,
        TimeSpan maxRetryAfter,
        CancellationToken cancellationToken)
    {
        using var lease = await limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new LlmProviderException(provider.Provider, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(10), "Local rate limit reached.");
        }

        var client = new OpenAiCompatibleLlmClient(httpClientFactory.CreateClient(clientName), provider, log);
        try
        {
            return await client.CompleteAsync(messages, tools, llmOptions, cancellationToken);
        }
        catch (LlmProviderException ex) when (ex.Status == HttpStatusCode.TooManyRequests
                                              && ex.RetryAfter is { } wait && wait <= maxRetryAfter)
        {
            // Honour a short Retry-After once, then let the caller fail over (or give up, on the fallback).
            await Task.Delay(wait, cancellationToken);
            return await client.CompleteAsync(messages, tools, llmOptions, cancellationToken);
        }
    }
}
