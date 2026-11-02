using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Agent.Tools;
using Guardrail.Application.Common;
using Guardrail.Domain.Agent;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Guardrail.Application.Agent;

/// <summary>
/// The closed allowlist of 11 tools (AI_TOOL_CONTRACTS.md, ADR-003). There is no ApprovePurchase, Checkout, CapturePayment,
/// UpdatePolicy, ApproveMerchant, BlockMerchant, WriteLedger, RunSql or HttpFetch tool.
/// </summary>
public sealed class ToolRegistry
{
    public static readonly IReadOnlyList<Type> ToolTypes =
    [
        typeof(SearchProductsTool),
        typeof(GetProductDetailsTool),
        typeof(SearchMerchantsTool),
        typeof(GetApprovedMerchantsTool),
        typeof(GetSpendingPoliciesTool),
        typeof(GetRemainingBudgetTool),
        typeof(CreateShoppingCartTool),
        typeof(EvaluatePurchaseTool),
        typeof(RequestPurchaseApprovalTool),
        typeof(GetPurchaseStatusTool),
        typeof(GetSpendingSummaryTool),
    ];

    private readonly Dictionary<string, IAgentTool> _tools;

    public ToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = tools.Where(t => ToolTypes.Contains(t.GetType())).ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<IAgentTool> All => _tools.Values;

    public IAgentTool? Find(string name) => _tools.GetValueOrDefault(name);

    public IReadOnlyList<LlmToolDefinition> Definitions() =>
        _tools.Values.OrderBy(t => ToolTypes.ToList().IndexOf(t.GetType()))
            .Select(t => new LlmToolDefinition(t.Name, t.Description, t.InputSchema)).ToList();
}

public sealed record DispatchResult(string ToolName, string ToolCallId, ToolOutcome Outcome, AgentExecutionStatus Status, int DurationMs, string ModelContent);

/// <summary>Validate args → execute use case with the JWT user → shape result → persist AgentExecution (AI_AGENT_SPECIFICATION.md §2).</summary>
public sealed class ToolDispatcher(
    ToolRegistry registry,
    IAppDbContext db,
    AuditWriter audit,
    IOptions<AgentOptions> options,
    TimeProvider clock,
    ILogger<ToolDispatcher> log)
{
    public async Task<DispatchResult> ExecuteAsync(LlmToolCall call, AgentToolContext context, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ToolOutcome outcome;
        AgentExecutionStatus status;
        var tool = registry.Find(call.Name);

        if (tool is null)
        {
            outcome = ToolOutcome.Failure(ErrorCodes.UnknownTool, $"There is no tool named '{Truncate(call.Name, 60)}'.");
            status = AgentExecutionStatus.Rejected;
        }
        else
        {
            try
            {
                using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                outcome = await tool.ExecuteAsync(args.RootElement.Clone(), context, cancellationToken);
                status = outcome.Ok ? AgentExecutionStatus.Succeeded : AgentExecutionStatus.Rejected;
            }
            catch (JsonException)
            {
                outcome = ToolOutcome.Failure(ErrorCodes.ValidationFailed, "Arguments must be a JSON object.");
                status = AgentExecutionStatus.Rejected;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Tool {Tool} failed", call.Name);
                db.ClearTracking();
                outcome = ToolOutcome.Failure(ErrorCodes.InternalError, "The tool failed unexpectedly.");
                status = AgentExecutionStatus.Failed;
            }
        }

        var durationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var resultJson = JsonDefaults.Serialize(outcome.Envelope());

        db.AgentExecutions.Add(AgentExecution.Record(
            Ids.New(), context.ConversationId, context.UserId, Truncate(call.Name, 100), Truncate(call.Id, 100),
            SafeJson(call.ArgumentsJson), resultJson, status, outcome.ErrorCode, durationMs, clock.GetUtcNow()));

        if (status == AgentExecutionStatus.Rejected && outcome.ErrorCode is ErrorCodes.NotFound or ErrorCodes.UnknownTool or ErrorCodes.ValidationFailed)
        {
            audit.Write(AuditActions.AgentToolRejected, "AgentConversation", context.ConversationId, context.UserId, ActorType.Agent,
                new { tool = Truncate(call.Name, 100), code = outcome.ErrorCode });
            log.LogWarning("Agent tool {Tool} rejected with {Code}", call.Name, outcome.ErrorCode);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new DispatchResult(call.Name, call.Id, outcome, status, durationMs, Bound(resultJson, options.Value.MaxToolResultBytes));
    }

    /// <summary>Tool results reach the model as ≤ 8 KB JSON; larger results are truncated with "truncated": true.</summary>
    private static string Bound(string json, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(json) <= maxBytes)
        {
            return json;
        }

        var preview = json[..Math.Min(json.Length, maxBytes - 200)];
        return JsonDefaults.Serialize(new { ok = true, truncated = true, dataPreview = preview });
    }

    private static string SafeJson(string? json)
    {
        try
        {
            using var _ = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return string.IsNullOrWhiteSpace(json) ? "{}" : json;
        }
        catch (JsonException)
        {
            return JsonDefaults.Serialize(new { unparsable = Truncate(json ?? string.Empty, 2000) });
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
