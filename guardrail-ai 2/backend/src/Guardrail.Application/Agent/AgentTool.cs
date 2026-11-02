using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentValidation;
using Guardrail.Application.Common;
using Guardrail.Domain.Common;

namespace Guardrail.Application.Agent;

/// <summary>Identity comes from the authenticated request, never from tool arguments (AI_GUARDRAILS.md §3.3).</summary>
public sealed record AgentToolContext(Guid UserId, Guid ConversationId, string? CorrelationId);

/// <summary>Reference to a server object; hydrated from the database at read time so the UI never shows model-invented numbers.</summary>
public sealed record CardRef(string Type, Guid? Id, IReadOnlyList<Guid>? Ids = null);

public static class CardTypes
{
    public const string ProductList = "PRODUCT_LIST";
    public const string Product = "PRODUCT";
    public const string MerchantList = "MERCHANT_LIST";
    public const string PolicySummary = "POLICY_SUMMARY";
    public const string Budget = "BUDGET";
    public const string Cart = "CART";
    public const string Evaluation = "EVALUATION";
    public const string Approval = "APPROVAL";
    public const string Purchase = "PURCHASE";
}

public sealed record ToolOutcome(bool Ok, object? Data, CardRef? Card, string? ErrorCode, string? ErrorMessage)
{
    public static ToolOutcome Success(object data, CardRef? card = null) => new(true, data, card, null, null);

    public static ToolOutcome Failure(string code, string message) => new(false, null, null, code, message);

    public static ToolOutcome From(DomainError error) => Failure(error.Code, error.Message);

    /// <summary>The envelope returned to the model: { ok, data, card } or { ok: false, error: { code, message } }.</summary>
    public object Envelope() => Ok
        ? new { ok = true, data = Data, card = Card }
        : new { ok = false, error = new { code = ErrorCode, message = ErrorMessage } };
}

public interface IAgentTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>Canonical JSON schema (AI_TOOL_CONTRACTS.md), additionalProperties:false.</summary>
    JsonObject InputSchema { get; }

    Task<ToolOutcome> ExecuteAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Strict argument binding: unknown fields (e.g. a model-supplied <c>price</c>) are rejected before the handler runs (T5),
/// then FluentValidation mirrors the contract.
/// </summary>
public abstract class AgentTool<TArgs> : IAgentTool
    where TArgs : class
{
    private static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
    };

    private readonly IValidator<TArgs>? _validator;

    protected AgentTool(IValidator<TArgs>? validator = null) => _validator = validator;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public JsonObject InputSchema => JsonNode.Parse(SchemaJson)!.AsObject();

    protected abstract string SchemaJson { get; }

    public async Task<ToolOutcome> ExecuteAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        TArgs? args;
        try
        {
            args = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonSerializer.Deserialize<TArgs>("{}", StrictOptions)
                : arguments.Deserialize<TArgs>(StrictOptions);
        }
        catch (JsonException ex)
        {
            return ToolOutcome.Failure(ErrorCodes.ValidationFailed, $"Invalid arguments: {ex.Message.Split('.')[0]}.");
        }

        if (args is null)
        {
            return ToolOutcome.Failure(ErrorCodes.ValidationFailed, "Arguments are required.");
        }

        if (_validator is not null && await _validator.CheckAsync(args, cancellationToken) is { } error)
        {
            var details = error.ValidationErrors is null ? error.Message : string.Join("; ", error.ValidationErrors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}"));
            return ToolOutcome.Failure(ErrorCodes.ValidationFailed, details);
        }

        return await HandleAsync(args, context, cancellationToken);
    }

    protected abstract Task<ToolOutcome> HandleAsync(TArgs args, AgentToolContext context, CancellationToken cancellationToken);
}

/// <summary>Arguments with no fields; still rejects unknown properties.</summary>
public sealed record NoArgs;

public static class ToolValidation
{
    public static readonly string[] CategoryCodes = Domain.Common.CategoryCodes.All.ToArray();

    public static IRuleBuilderOptions<T, string?> CategoryCode<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(c => c is null || CategoryCodes.Contains(c)).WithMessage($"Must be one of {string.Join(", ", CategoryCodes)}.");

    public static IRuleBuilderOptions<T, string?> Period<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(p => p is null || EnumText.TryParse<BudgetPeriodType>(p, out _)).WithMessage("Must be WEEKLY or MONTHLY.");
}
