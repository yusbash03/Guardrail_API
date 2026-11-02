using System.Text.Json.Nodes;

namespace Guardrail.Application.Abstractions;

public enum LlmRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <param name="ProviderMetadata">Opaque provider data that must be echoed back on the next request (e.g. Gemini 3 thought signatures).</param>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson, LlmProviderMetadata? ProviderMetadata = null);

public sealed record LlmProviderMetadata(string Provider, string Json);

public sealed record LlmMessage(LlmRole Role, string? Content, IReadOnlyList<LlmToolCall>? ToolCalls = null, string? ToolCallId = null);

public sealed record LlmToolDefinition(string Name, string Description, JsonObject Parameters);

public sealed record LlmOptions(double Temperature, int MaxOutputTokens);

public sealed record LlmUsage(int InputTokens, int OutputTokens);

public sealed record LlmResponse(string? Text, IReadOnlyList<LlmToolCall> ToolCalls, string Provider, string Model, LlmUsage? Usage);

/// <summary>Primary and fallback providers are rate-limited or unreachable (→ 503 AGENT_UNAVAILABLE).</summary>
public sealed class LlmUnavailableException(string message, TimeSpan? retryAfter = null, Exception? inner = null) : Exception(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Provider-neutral chat completion with tool calling (AI_AGENT_SPECIFICATION.md §2).</summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, LlmOptions options, CancellationToken cancellationToken);
}
